param(
    [Parameter(Mandatory=$true)][ValidateSet('Pack','Unpack')][string]$Mode,
    [string]$GamePath = $env:SUMMERCOOP_GAME_PATH,
    [string]$ArchivePath = '',
    [string]$Destination = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$ArchivePath) { $ArchivePath = Join-Path $root 'ci\msc-references.zip.aes' }
if (!$Destination) { $Destination = Join-Path $root 'build\ci-game' }
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
$Destination = [IO.Path]::GetFullPath($Destination)
if (!$env:SUMMERCOOP_REFS_PASSPHRASE) { throw 'Set the SUMMERCOOP_REFS_PASSPHRASE secret before preparing CI references.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$required = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','ES2.dll','MSCLoader.dll','0Harmony.dll')
$work = Join-Path $root ('build\ci-references-' + [Guid]::NewGuid().ToString('N'))
$plain = Join-Path $work 'references.zip'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$aes = [Security.Cryptography.Aes]::Create()
$aes.KeySize = 256
$aes.Mode = [Security.Cryptography.CipherMode]::CBC
$aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
$material = $null
$hmac = $null
function DeriveKey([byte[]]$salt) {
    # This overload uses PBKDF2-HMAC-SHA1 in both .NET Framework and modern .NET.
    $kdf = [Security.Cryptography.Rfc2898DeriveBytes]::new([Text.Encoding]::UTF8.GetBytes($env:SUMMERCOOP_REFS_PASSPHRASE), $salt, 200000)
    try { return $kdf.GetBytes(64) } finally { $kdf.Dispose() }
}
try {
    if ($Mode -eq 'Pack') {
        if (!$GamePath) { throw 'Specify -GamePath for an installed My Summer Car with MSCLoader.' }
        $source = Join-Path $GamePath 'mysummercar_Data\Managed'
        $staging = Join-Path $work 'game'
        $managed = Join-Path $staging 'mysummercar_Data\Managed'
        New-Item -ItemType Directory -Path $managed -Force | Out-Null
        $manifest = [ordered]@{}
        foreach ($name in $required) {
            $file = Join-Path $source $name
            if (!(Test-Path -LiteralPath $file)) { throw "Missing private compilation reference: $name" }
            Copy-Item -LiteralPath $file -Destination $managed
            $manifest[$name] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        }
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'references.json') -Encoding UTF8
        [IO.Compression.ZipFile]::CreateFromDirectory($staging, $plain)
        $salt = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
        [byte[]]$material = DeriveKey $salt
        $aes.Key = [byte[]]($material[0..31])
        $aes.GenerateIV()
        $bytes = [IO.File]::ReadAllBytes($plain)
        $transform = $aes.CreateEncryptor()
        try { $cipher = $transform.TransformFinalBlock($bytes, 0, $bytes.Length) } finally { $transform.Dispose() }
        # SCREFS01 | salt[32] | IV[16] | AES-CBC ciphertext | HMAC-SHA256[32].
        $message = New-Object byte[] (56 + $cipher.Length)
        [Buffer]::BlockCopy([Text.Encoding]::ASCII.GetBytes('SCREFS01'), 0, $message, 0, 8)
        [Buffer]::BlockCopy($salt, 0, $message, 8, 32)
        [Buffer]::BlockCopy($aes.IV, 0, $message, 40, 16)
        [Buffer]::BlockCopy($cipher, 0, $message, 56, $cipher.Length)
        $hmac = [Security.Cryptography.HMACSHA256]::new([byte[]]($material[32..63]))
        $tag = $hmac.ComputeHash($message)
        $packed = New-Object byte[] ($message.Length + 32)
        [Buffer]::BlockCopy($message, 0, $packed, 0, $message.Length)
        [Buffer]::BlockCopy($tag, 0, $packed, $message.Length, 32)
        New-Item -ItemType Directory -Path (Split-Path -Parent $ArchivePath) -Force | Out-Null
        [IO.File]::WriteAllBytes($ArchivePath, $packed)
        Write-Output "Authenticated encrypted CI references written to $ArchivePath"
    } else {
        if (!(Test-Path -LiteralPath $ArchivePath)) { throw 'Encrypted CI reference archive is missing. Run ci-references.ps1 -Mode Pack once.' }
        $length = (Get-Item -LiteralPath $ArchivePath).Length
        if ($length -lt 104 -or $length -gt 32MB) { throw 'Invalid encrypted reference archive size.' }
        $packed = [IO.File]::ReadAllBytes($ArchivePath)
        if ([Text.Encoding]::ASCII.GetString($packed, 0, 8) -ne 'SCREFS01') { throw 'Unknown CI reference encryption format.' }
        [byte[]]$material = DeriveKey ([byte[]]($packed[8..39]))
        $hmac = [Security.Cryptography.HMACSHA256]::new([byte[]]($material[32..63]))
        $messageLength = $packed.Length - 32
        $tag = $hmac.ComputeHash($packed, 0, $messageLength)
        $difference = 0
        for ($i = 0; $i -lt 32; ++$i) { $difference = $difference -bor ($tag[$i] -bxor $packed[$messageLength + $i]) }
        if ($difference) { throw 'CI reference authentication failed: the archive and repository Secret do not match, or the archive is damaged.' }
        $aes.Key = [byte[]]($material[0..31])
        $aes.IV = [byte[]]($packed[40..55])
        $transform = $aes.CreateDecryptor()
        try { $bytes = $transform.TransformFinalBlock($packed, 56, $messageLength - 56) } finally { $transform.Dispose() }
        [IO.File]::WriteAllBytes($plain, $bytes)
        if (Test-Path -LiteralPath $Destination) { throw 'CI reference destination must be a new directory.' }
        [IO.Compression.ZipFile]::ExtractToDirectory($plain, $Destination)
        $manifest = Get-Content -LiteralPath (Join-Path $Destination 'references.json') -Raw | ConvertFrom-Json
        foreach ($name in $required) {
            $file = Join-Path $Destination ('mysummercar_Data\Managed\' + $name)
            if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $manifest.$name) {
                throw "Private CI reference failed integrity check: $name"
            }
        }
        Write-Output "Private build references authenticated and restored to $Destination"
    }
} finally {
    $aes.Dispose()
    if ($hmac) { $hmac.Dispose() }
    if ($material) { [Array]::Clear($material, 0, $material.Length) }
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $root 'build')) + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($work).StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
