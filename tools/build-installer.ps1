param([string]$PackagePath, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$PackagePath) {
    $model = Get-Content -LiteralPath (Join-Path $root 'src\Core\Model.cs') -Raw
    if ($model -notmatch 'ModVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)"') { throw 'Unable to read the mod version.' }
    $PackagePath = Join-Path $root ('dist\SummerCoop-' + $Matches[1])
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
if (!$OutputPath) { $OutputPath = Join-Path $root 'dist\SummerCoop-Setup.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$files = @('SummerCoop.dll','References\SummerCoop.Core.dll','References\SummerCoop.ProfileUpdate.exe')
$manifest = Join-Path (Split-Path -Parent $OutputPath) 'installer-payload-sha256.txt'
$lines = @()
$resources = @()
for ($i = 0; $i -lt $files.Count; $i++) {
    $file = Join-Path (Join-Path $PackagePath 'Mods') $files[$i]
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw ('Missing installer payload: ' + $file) }
    $lines += (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash + '  ' + $files[$i]
    $resources += '/resource:' + $file + ',SummerCoop.Payload.' + $i
}
$lines | Set-Content -LiteralPath $manifest -Encoding ASCII
$resources += '/resource:' + $manifest + ',SummerCoop.Payload.SHA256'
& $compiler /nologo /langversion:5 /target:winexe /optimize+ /platform:anycpu ('/out:' + $OutputPath) ('/win32manifest:' + (Join-Path $root 'tools\Installer\app.manifest')) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $resources (Join-Path $root 'tools\Installer\InstallerCore.cs') (Join-Path $root 'tools\Installer\Program.cs')
if ($LASTEXITCODE) { throw 'Installer build failed.' }
((Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($OutputPath)) | Set-Content -LiteralPath ($OutputPath + '.sha256') -Encoding ASCII
Write-Output $OutputPath
