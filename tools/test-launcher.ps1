param([string]$GamePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'build\launcher-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is missing.' }
$testExe = Join-Path $testDir 'SummerCoop.LauncherTests.exe'
& $compiler /nologo /langversion:5 /target:exe ('/out:' + $testExe) (Join-Path $root 'launcher\LauncherCore.cs') (Join-Path $root 'launcher\SteamInvitation.cs') (Join-Path $root 'tests\Launcher\LauncherTests.cs')
if ($LASTEXITCODE) { throw 'Launcher tests build failed.' }
& $testExe | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
if ($LASTEXITCODE) { throw 'Launcher tests failed.' }
# Compile the real WinForms launcher too, with explicitly inert fixtures. Never publish this EXE.
$package = Join-Path $testDir 'fixture-package'
New-Item -ItemType Directory -Path (Join-Path $package 'Mods\References') -Force | Out-Null
foreach ($file in @('SummerCoop.dll','References\SummerCoop.Core.dll','References\SummerCoop.ProfileUpdate.exe')) {
    'CI fixture, not a mod binary' | Set-Content -LiteralPath (Join-Path (Join-Path $package 'Mods') $file) -Encoding ASCII
}
& (Join-Path $PSScriptRoot 'build-launcher.ps1') -PackagePath $package -OutputPath (Join-Path $testDir 'DO-NOT-PUBLISH-fixture-Launcher.exe')
$uiTestExe = Join-Path $testDir 'SummerCoop.LauncherUiTests.exe'
$artResources = @('/resource:' + (Join-Path $root 'launcher\assets\background.png') + ',SummerCoop.Launcher.Background')
$uiSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'launcher') -Filter '*.cs' -File | Where-Object Name -ne 'Program.cs' | Sort-Object Name | ForEach-Object FullName)
& $compiler /nologo /langversion:5 /target:exe ('/out:' + $uiTestExe) ('/win32manifest:' + (Join-Path $root 'launcher\app.manifest')) /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $artResources (Join-Path $root 'tools\Installer\InstallerCore.cs') $uiSources (Join-Path $root 'tests\Launcher\LauncherUiTests.cs')
if ($LASTEXITCODE) { throw 'Launcher GUI tests build failed.' }
& $uiTestExe (Join-Path $testDir 'screenshots') | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt') -Append
if ($LASTEXITCODE) { throw 'Launcher GUI tests failed.' }
if ($GamePath) {
    $bindingTest = Join-Path $testDir 'SummerCoop.SteamBindingTests.exe'
    & $compiler /nologo /langversion:5 /target:exe ('/out:' + $bindingTest) (Join-Path $root 'launcher\SteamInvitation.cs') (Join-Path $root 'launcher\SteamInviteClient.cs') (Join-Path $root 'tests\Launcher\SteamBindingTests.cs')
    if ($LASTEXITCODE) { throw 'Steam binding checks build failed.' }
    & $bindingTest ([IO.Path]::GetFullPath($GamePath)) | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt') -Append
    if ($LASTEXITCODE) { throw 'Steam binding checks failed.' }
}
