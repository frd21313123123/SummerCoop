$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$model = Get-Content -LiteralPath (Join-Path $root 'src\Core\Model.cs') -Raw
if ($model -notmatch 'ModVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)"') { throw 'Unable to read the mod version.' }
$version = $Matches[1]
foreach ($dll in @('SummerCoop.dll','SummerCoop.Core.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $root ('build\' + $dll)))) { throw 'Build both mod DLLs before packaging.' }
}
$release = Join-Path $root ('dist\SummerCoop-' + $version)
New-Item -ItemType Directory -Path (Join-Path $release 'Mods\References') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.dll') -Destination (Join-Path $release 'Mods\SummerCoop.dll') -Force
Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination (Join-Path $release 'Mods\References\SummerCoop.Core.dll') -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
& $compiler /nologo /langversion:5 /target:exe ('/out:' + (Join-Path $release 'Mods\References\SummerCoop.ProfileUpdate.exe')) ('/reference:' + (Join-Path $root 'build\SummerCoop.Core.dll')) (Join-Path $root 'tools\MigrateProfile.cs')
if ($LASTEXITCODE) { throw 'Profile update helper build failed.' }
foreach ($name in @('README.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $release -Force }
Copy-Item -LiteralPath (Join-Path $root 'tools\install.ps1') -Destination (Join-Path $release 'install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $release -Recurse -Force
$files = Get-ChildItem -LiteralPath $release -File -Recurse | Where-Object Name -ne 'SHA256.txt'
$files | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.FullName.Substring($release.Length + 1) } | Set-Content -LiteralPath (Join-Path $release 'SHA256.txt') -Encoding ASCII
$zip = Join-Path $root ('dist\SummerCoop-' + $version + '.zip')
Compress-Archive -LiteralPath (Join-Path $release 'Mods'),(Join-Path $release 'README.md'),(Join-Path $release 'LICENSE'),(Join-Path $release 'docs'),(Join-Path $release 'install.ps1'),(Join-Path $release 'SHA256.txt') -DestinationPath $zip -Force
Write-Output $zip
& (Join-Path $PSScriptRoot 'build-installer.ps1') -PackagePath $release
& (Join-Path $PSScriptRoot 'build-launcher.ps1') -PackagePath $release
$sourceZip = Join-Path $root ('dist\SummerCoop-' + $version + '-source.zip')
Compress-Archive -LiteralPath (Join-Path $root 'src'),(Join-Path $root 'tests'),(Join-Path $root 'tools'),(Join-Path $root 'launcher'),(Join-Path $root 'docs'),(Join-Path $root '.github'),(Join-Path $root 'README.md'),(Join-Path $root 'LICENSE'),(Join-Path $root '.gitignore'),(Join-Path $root '.gitattributes') -DestinationPath $sourceZip -Force
Write-Output $sourceZip
