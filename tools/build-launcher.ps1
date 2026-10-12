param([string]$PackagePath, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$PackagePath) {
    $model = Get-Content -LiteralPath (Join-Path $root 'src\Core\Model.cs') -Raw
    if ($model -notmatch 'ModVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)"') { throw 'Unable to read the mod version.' }
    $PackagePath = Join-Path $root ('dist\SummerCoop-' + $Matches[1])
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
if (!$OutputPath) { $OutputPath = Join-Path $root 'dist\SummerCoop-Launcher.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is missing.' }
$files = @('SummerCoop.dll','References\SummerCoop.Core.dll','References\SummerCoop.ProfileUpdate.exe')
$manifest = Join-Path (Split-Path -Parent $OutputPath) 'launcher-payload-sha256.txt'
$lines = @()
$resources = @()
for ($i = 0; $i -lt $files.Count; $i++) {
    $file = Join-Path (Join-Path $PackagePath 'Mods') $files[$i]
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw ('Missing launcher payload: ' + $file) }
    $lines += (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash + '  ' + $files[$i]
    $resources += '/resource:' + $file + ',SummerCoop.Payload.' + $i
}
$lines | Set-Content -LiteralPath $manifest -Encoding ASCII
$resources += '/resource:' + $manifest + ',SummerCoop.Payload.SHA256'
# Фон вшивается в тот же EXE: один файл без внешних картинок.
# Внешний background.jpg/png рядом с EXE по-прежнему имеет приоритет в рантайме.
$background = $null
foreach ($candidate in @('launcher\assets\background.png','launcher\assets\background.jpg','launcher\assets\background.jpeg')) {
    $full = Join-Path $root $candidate
    if (Test-Path -LiteralPath $full -PathType Leaf) { $background = $full; break }
}
if ($background) { $resources += '/resource:' + $background + ',SummerCoop.Launcher.Background' }
else { Write-Warning 'Background art not found in launcher/assets; the launcher will use the drawn placeholder.' }
& $compiler /nologo /langversion:5 /target:winexe /optimize+ /platform:anycpu ('/out:' + $OutputPath) ('/win32manifest:' + (Join-Path $root 'launcher\app.manifest')) /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $resources (Join-Path $root 'tools\Installer\InstallerCore.cs') (Join-Path $root 'launcher\LauncherCore.cs') (Join-Path $root 'launcher\BackgroundArt.cs') (Join-Path $root 'launcher\MainForm.cs') (Join-Path $root 'launcher\Program.cs')
if ($LASTEXITCODE) { throw 'Launcher build failed.' }
((Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($OutputPath)) | Set-Content -LiteralPath ($OutputPath + '.sha256') -Encoding ASCII
Write-Output $OutputPath
