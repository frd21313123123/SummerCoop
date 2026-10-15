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
# The launcher is the only setup tool: its command line installs the embedded
# mod into a fixture game the same way the automatic update and the interface do.
$fixtureGame = Join-Path $testDir 'cli-game'
$fixtureProfile = Join-Path $testDir 'cli-world'
$cliLog = Join-Path $testDir 'cli-install.txt'
foreach ($stale in @($fixtureGame, $fixtureProfile, $cliLog)) {
    $target = [IO.Path]::GetFullPath($stale)
    if (!$target.StartsWith([IO.Path]::GetFullPath($testDir) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped the test directory.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
New-Item -ItemType Directory -Path (Join-Path $fixtureGame 'mysummercar_Data\Managed') -Force | Out-Null
'fixture game, not executable' | Set-Content -LiteralPath (Join-Path $fixtureGame 'mysummercar.exe') -Encoding ASCII
'fixture loader' | Set-Content -LiteralPath (Join-Path $fixtureGame 'mysummercar_Data\Managed\MSCLoader.dll') -Encoding ASCII
New-Item -ItemType Directory -Path $fixtureProfile -Force | Out-Null
# The launcher is a GUI executable that must be waited for; Start-Process does
# not quote argument arrays, so one quoted command line is passed explicitly.
$cliArguments = '--install --game="' + $fixtureGame + '" --profile="' + $fixtureProfile + '" --log="' + $cliLog + '"'
$cliInstall = Start-Process -FilePath (Join-Path $testDir 'DO-NOT-PUBLISH-fixture-Launcher.exe') -ArgumentList $cliArguments -WindowStyle Hidden -PassThru -Wait
if ($cliInstall.ExitCode) { throw 'Launcher command line installation failed.' }
foreach ($file in @('SummerCoop.dll','References\SummerCoop.Core.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path (Join-Path $fixtureGame 'Mods') $file))) { throw ('Launcher command line did not install ' + $file) }
}
if (!(Test-Path -LiteralPath $cliLog) -or (Get-Item -LiteralPath $cliLog).Length -eq 0) { throw 'Launcher command line did not write the installation log.' }
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
