param([string]$LauncherPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDirectory = Join-Path $root 'build\native-save-tests'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
if (!$LauncherPath) {
    $LauncherPath = Join-Path $root 'SummerCoop-Launcher\SummerCoop-Launcher.exe'
    & (Join-Path $PSScriptRoot 'build-launcher.ps1')
}
$fixture = Join-Path $testDirectory ('worlds space & Юникод-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$report = Join-Path $testDirectory 'test-results.txt'
$arguments = '/test-saves "' + $fixture + '" "' + $report + '"'
$process = Start-Process -FilePath ([IO.Path]::GetFullPath($LauncherPath)) -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Native save tests timed out.' }
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
    throw 'Native save tests failed.'
}
Get-Content -LiteralPath $report
if ((Get-Content -LiteralPath $report -Raw) -notmatch 'ALL NATIVE SAVE TESTS PASSED') { throw 'Native save test report is incomplete.' }
