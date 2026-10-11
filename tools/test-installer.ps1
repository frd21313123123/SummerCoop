$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'build\installer-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$testExe = Join-Path $testDir 'SummerCoop.InstallerTests.exe'
& $compiler /nologo /langversion:5 /target:exe ('/out:' + $testExe) (Join-Path $root 'tools\Installer\InstallerCore.cs') (Join-Path $root 'tests\Installer\InstallerTests.cs')
if ($LASTEXITCODE) { throw 'Installer tests build failed.' }
& $testExe | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
if ($LASTEXITCODE) { throw 'Installer tests failed.' }
# Compile the real GUI and embedded payload path too, using explicitly inert fixtures.
# CI does not have the game adapter DLL; this executable must never be published.
$package = Join-Path $testDir 'fixture-package'
New-Item -ItemType Directory -Path (Join-Path $package 'Mods\References') -Force | Out-Null
foreach ($file in @('SummerCoop.dll','References\SummerCoop.Core.dll','References\SummerCoop.ProfileUpdate.exe')) {
    'CI fixture, not a mod binary' | Set-Content -LiteralPath (Join-Path (Join-Path $package 'Mods') $file) -Encoding ASCII
}
& (Join-Path $PSScriptRoot 'build-installer.ps1') -PackagePath $package -OutputPath (Join-Path $testDir 'DO-NOT-PUBLISH-fixture-Setup.exe')
