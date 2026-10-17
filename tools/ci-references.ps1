param(
    [Parameter(Mandatory=$true)][ValidateSet('Pack','Unpack')][string]$Mode,
    [string]$GamePath = $env:SUMMERCOOP_GAME_PATH,
    [string]$ArchivePath = '',
    [string]$Destination = '',
    [string]$GpgPath = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$ArchivePath) { $ArchivePath = Join-Path $root 'ci\msc-references.zip.gpg' }
if (!$Destination) { $Destination = Join-Path $root 'build\ci-game' }
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
$Destination = [IO.Path]::GetFullPath($Destination)
if (!$env:SUMMERCOOP_REFS_PASSPHRASE) { throw 'Set the SUMMERCOOP_REFS_PASSPHRASE secret before preparing CI references.' }
if (!$GpgPath) {
    $gpg = Get-Command gpg -ErrorAction SilentlyContinue
    if ($gpg) { $GpgPath = $gpg.Source }
    else { $GpgPath = Join-Path $env:ProgramFiles 'Git\usr\bin\gpg.exe' }
}
if (!(Test-Path -LiteralPath $GpgPath)) { throw 'GnuPG is required (included with Git for Windows).' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$required = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','ES2.dll','MSCLoader.dll','0Harmony.dll')
$work = Join-Path $root ('build\ci-references-' + [Guid]::NewGuid().ToString('N'))
$plain = Join-Path $work 'references.zip'
$gpgHome = Join-Path $work 'gnupg'
New-Item -ItemType Directory -Path $gpgHome -Force | Out-Null
function GpgFile([string]$path) {
    # Git's MSYS build does not accept backslash paths when launched directly.
    if ($GpgPath -match '[\\/]usr[\\/]bin[\\/]gpg.exe$' -and $path -match '^[A-Za-z]:') {
        return '/' + $path.Substring(0, 1).ToLowerInvariant() + $path.Substring(2).Replace('\', '/')
    }
    return $path.Replace('\', '/')
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
        New-Item -ItemType Directory -Path (Split-Path -Parent $ArchivePath) -Force | Out-Null
        $operation = '--symmetric --cipher-algo AES256 --compress-algo none --output "' + (GpgFile $ArchivePath) + '" "' + (GpgFile $plain) + '"'
    } else {
        if (!(Test-Path -LiteralPath $ArchivePath)) { throw 'Encrypted CI reference archive is missing. Run ci-references.ps1 -Mode Pack once.' }
        $operation = '--decrypt --output "' + (GpgFile $plain) + '" "' + (GpgFile $ArchivePath) + '"'
    }
    # The passphrase goes through stdin, never process arguments or logs.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $GpgPath
    $start.Arguments = '--no-options --homedir "' + (GpgFile $gpgHome) + '" --batch --yes --no-symkey-cache --pinentry-mode loopback --passphrase-fd 0 ' + $operation
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.WriteLine($env:SUMMERCOOP_REFS_PASSPHRASE)
    $process.StandardInput.Close()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw 'Private reference encryption/decryption failed.' }
    $process.Dispose()
    if ($Mode -eq 'Unpack') {
        if (Test-Path -LiteralPath $Destination) { throw 'CI reference destination must be a new directory.' }
        [IO.Compression.ZipFile]::ExtractToDirectory($plain, $Destination)
        $manifest = Get-Content -LiteralPath (Join-Path $Destination 'references.json') -Raw | ConvertFrom-Json
        foreach ($name in $required) {
            $file = Join-Path $Destination ('mysummercar_Data\Managed\' + $name)
            if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $manifest.$name) {
                throw "Private CI reference failed integrity check: $name"
            }
        }
        Write-Output "Private build references restored to $Destination"
    } else { Write-Output "Encrypted CI references written to $ArchivePath" }
} finally {
    # Only this invocation's generated workspace directory is removed.
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $root 'build')) + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($work).StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
