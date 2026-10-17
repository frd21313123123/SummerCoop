param([string]$LauncherPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'build\steam-binding-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
if (!$LauncherPath) {
    $LauncherPath = Join-Path $testDir 'SummerCoop-Launcher.exe'
    & (Join-Path $PSScriptRoot 'build-launcher.ps1') -OutputPath $LauncherPath
}
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$results = [Collections.Generic.List[string]]::new()
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'MSVC is required for the native Steam binding tests.' }
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$testExe = Join-Path $testDir 'SteamBindingTests.exe'
$testSource = Join-Path $root 'tests\Launcher\SteamBindingTests.cpp'
$batch = Join-Path $testDir 'compile-tests.cmd'
@"
@echo off
call "$vcvars" >nul || exit /b 1
cl /nologo /O1 /MT /W3 /EHsc /utf-8 /std:c++17 /D_CRT_SECURE_NO_WARNINGS /Fo"$testDir\SteamBindingTests.obj" /Fe"$testExe" "$testSource" || exit /b 1
"@ | Set-Content -LiteralPath $batch -Encoding ASCII
& $env:ComSpec /d /c $batch
if ($LASTEXITCODE) { throw 'Steam binding test compilation failed.' }
$output = @(& $testExe)
if ($LASTEXITCODE) { throw ($output -join "`n") }
$output | ForEach-Object { if ($_ -like 'PASS *') { $results.Add($_) } }

function VdfQuote([string]$text) { '"' + $text.Replace('\', '\\').Replace('"', '\"') + '"' }
function Config([string]$line) {
    @'
"UserLocalConfigStore"
{
    "Software" { "Valve" { "Steam" { "apps" {
        "516750"
        {
            "cloud" { "last_sync_state" "synchronized" }
            "LaunchOptions" {{LAUNCH}}
            "LastPlayed" "12345"
        }
        "480" { "LaunchOptions" "\"C:\\Other game\\MWCoop.exe\" %command%" }
    } } } }
    "OtherSettings" { "KeepMe" "unchanged" }
}
'@.Replace('{{LAUNCH}}', (VdfQuote $line))
}
function PutText([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
function AddOverlay([string]$path, [byte]$value) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Append)
    try { $stream.WriteByte($value) } finally { $stream.Dispose() }
}
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function RunBinding([int]$expected = 0) {
    $arguments = '/test-steam-binding "' + $steamRoot + '" "' + $stateFile + '" "' + $report + '"'
    $p = Start-Process -FilePath $source -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$p.WaitForExit(20000)) { $p.Kill(); throw 'Steam registration timed out.' }
    if ($p.ExitCode -ne $expected) { throw "Steam registration returned $($p.ExitCode), expected $expected. $(Get-Content -LiteralPath $report -Raw)" }
    Get-Content -LiteralPath $report -Raw
}
$fixture = Join-Path $testDir ([Guid]::NewGuid().ToString('N') + ' space & Юникод !')
$sourceDir = Join-Path $fixture 'current launcher'
$aliasDir = Join-Path $fixture 'old launcher'
$steamRoot = Join-Path $fixture 'Steam'
$configDir = Join-Path $steamRoot 'userdata\123456\config'
foreach ($dir in @($sourceDir, $aliasDir, $configDir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$source = Join-Path $sourceDir 'SummerCoop-Launcher.exe'
$alias = Join-Path $aliasDir 'SummerCoop-Launcher-0.4.1.exe'
$config = Join-Path $configDir 'localconfig.vdf'
$stateFile = Join-Path $fixture 'profile\steam-launcher.ini'
$report = Join-Path $fixture 'registration-report.txt'
Copy-Item -LiteralPath $LauncherPath -Destination $source
Copy-Item -LiteralPath $LauncherPath -Destination $alias
AddOverlay $alias 42
$sourceIni = Join-Path $sourceDir 'summercoop-launcher.ini'
$aliasIni = Join-Path $aliasDir 'summercoop-launcher.ini'
PutText $sourceIni "[Lanceur]`r`nLangue=ru`r`nAutoUpdate=0`r`nSauvegarde=my-save"
PutText $aliasIni "[Lanceur]`r`nLangue=en`r`nReseau=steam"
$sourceIniHash = Hash $sourceIni
$aliasIniHash = Hash $aliasIni
$flags = ' %command% -screen-fullscreen 0 +connect_lobby 123456789'
$originalConfig = Config ('"' + $alias + '"' + $flags)
$expectedConfig = Config ('"' + $source + '"' + $flags)
PutText $config $originalConfig
$registered = RunBinding
if ($registered -notmatch 'configs_updated=1' -or $registered -notmatch 'aliases_updated=1') { throw 'Registration did not repair both Steam and its cached target.' }
if ([IO.File]::ReadAllText($config) -cne $expectedConfig) { throw 'Registration changed unrelated Steam settings or launch arguments.' }
if ((Hash $source) -ne (Hash $alias)) { throw 'The legacy executable was not replaced with the current bytes.' }
$results.Add('PASS real native registration repairs Steam and its cached legacy target with Unicode paths')
if ([IO.File]::ReadAllText($config + '.summercoop.bak') -cne $originalConfig) { throw 'Original Steam configuration backup is missing or incorrect.' }
$results.Add('PASS preserve an exact backup of the original Steam settings')
if ((Hash $sourceIni) -ne $sourceIniHash -or (Hash $aliasIni) -ne $aliasIniHash) { throw 'Registration replaced launcher settings.' }
$results.Add('PASS preserve settings of both launcher copies and invitation/game arguments')
$unchanged = RunBinding
if ($unchanged -notmatch 'configs_updated=0' -or $unchanged -notmatch 'aliases_updated=0') { throw 'Registration keeps rewriting unchanged files.' }
$results.Add('PASS repeat registration leaves current files unchanged')

# A running Steam client may persist its cached old command over our repair.
PutText $config $originalConfig
$null = RunBinding
if ([IO.File]::ReadAllText($config) -cne $expectedConfig) { throw 'A cached old Steam command was not repaired again.' }
AddOverlay $source 43
$null = RunBinding
if ((Hash $source) -ne (Hash $alias)) { throw 'An update lost the legacy path after Steam had already been rebound.' }
$results.Add('PASS repair a cached Steam command and remember its old path across later EXE updates')

$before = [IO.File]::ReadAllText($config)
$writer = [IO.File]::Open($config, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
try {
    $null = RunBinding 1
} finally { $writer.Dispose() }
if ([IO.File]::ReadAllText($config) -cne $before) { throw 'Registration overwrote an active Steam writer.' }
$null = RunBinding
$results.Add('PASS defer an active Steam configuration writer and recover on the next run')

AddOverlay $alias 44
$lockedHash = Hash $alias
$lock = [IO.File]::Open($alias, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $null = RunBinding 1
    if ((Hash $alias) -ne $lockedHash) { throw 'A locked legacy executable was damaged.' }
} finally { $lock.Dispose() }
$null = RunBinding
if ((Hash $source) -ne (Hash $alias)) { throw 'Legacy executable sync did not recover after the lock was released.' }
$results.Add('PASS leave a locked launcher intact and retry after its file is released')

$futureDir = Join-Path $fixture 'newer release'
New-Item -ItemType Directory -Path $futureDir -Force | Out-Null
$future = Join-Path $futureDir 'SummerCoop-Launcher-99.0.0.exe'
Copy-Item -LiteralPath $source -Destination $future
& $testExe /future-version $future
if ($LASTEXITCODE) { throw 'Could not prepare the future-version test copy.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($future).FileMajorPart -ne 32767) { throw 'The future-version fixture is invalid.' }
$futureHash = Hash $future
PutText $config (Config ('"' + $future + '"' + $flags))
$lock = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try { $newer = RunBinding 1 } finally { $lock.Dispose() }
if ($newer -notmatch [regex]::Escape('launcher=' + $future) -or (Hash $future) -ne $futureHash) { throw 'An older launcher displaced the newer Steam target.' }
if ([IO.File]::ReadAllText($config) -cne (Config ('"' + $future + '"' + $flags))) { throw 'Steam was redirected to an older launcher.' }
$results.Add('PASS an older running launcher selects the newer Steam target without downgrading it')

$unexpected = @(Get-ChildItem -LiteralPath $sourceDir -File | Where-Object Name -notin @('SummerCoop-Launcher.exe','SummerCoop-Launcher.exe.sha256','summercoop-launcher.ini'))
$staging = @(Get-ChildItem -LiteralPath $fixture -Recurse -File | Where-Object Name -like '*.tmp')
if ($unexpected.Count -or $staging.Count) { throw 'Steam registration left extra files in the standalone folder or abandoned staging files.' }
$results.Add('PASS keep the standalone folder clean and remove all staging files')
$results.Add("ALL $($results.Count) STEAM BINDING TESTS PASSED")
$results | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
