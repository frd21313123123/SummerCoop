param([string]$LauncherPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'build\native-customization-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
if (!$LauncherPath) {
    $LauncherPath = Join-Path $testDir 'SummerCoop-Launcher.exe'
    & (Join-Path $PSScriptRoot 'build-launcher.ps1') -OutputPath $LauncherPath
} elseif ([IO.Path]::GetFullPath($LauncherPath) -ne (Join-Path $testDir 'SummerCoop-Launcher.exe')) {
    Copy-Item -LiteralPath $LauncherPath -Destination (Join-Path $testDir 'SummerCoop-Launcher.exe') -Force
    $LauncherPath = Join-Path $testDir 'SummerCoop-Launcher.exe'
}
# The native capture mode discovers this inert game beside its own EXE.
# It uses the real renderer and window messages without launching any game.
$managed = Join-Path $testDir 'mysummercar_Data\Managed'
$data = Join-Path $testDir 'MWCoop'
New-Item -ItemType Directory -Path $managed, $data -Force | Out-Null
'fixture' | Set-Content -LiteralPath (Join-Path $testDir 'mysummercar.exe') -Encoding ASCII
'fixture' | Set-Content -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -Encoding ASCII
'[Lanceur]' | Set-Content -LiteralPath (Join-Path $testDir 'summercoop-launcher.ini') -Encoding ASCII
$config = Join-Path $data 'mwcoop.ini'
$results = [Collections.Generic.List[string]]::new()
function Capture($name, $clicks, $patterns) {
    "[Coop]`r`nApparence=char_shirt21`r`nCouleurVoiture=123456`r`n" | Set-Content -LiteralPath $config -Encoding ASCII
    $png = Join-Path $testDir ($name + '.png')
    $arguments = '/capture "' + $png + '" serveur /lang ru /theme sombre ' + $clicks
    $process = Start-Process -FilePath $LauncherPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw "Capture timed out: $name" }
    if ($process.ExitCode -ne 0) { throw "Capture failed: $name" }
    $output = Get-Content -LiteralPath ($png + '.txt') -Raw
    foreach ($pattern in $patterns) { if ($output -notmatch $pattern) { throw "$name missing $pattern`n$output" } }
    $results.Add("PASS $name")
}
if (Test-Path -LiteralPath (Join-Path $data 'cache\perso.mesh')) {
    Capture 'running-outfit' '/clic 100 360 /clic 1210 412' @('page 2 .*state 3', 'look char_shirt22 color 123456 perso 1')
} else {
    Capture 'running-outfit' '/clic 100 360 /clic 850 180' @('page 2 .*state 3', 'look char_shirt02 color 123456')
}
Capture 'running-car' '/clic 100 410 /clic 1050 180' @('page 3 .*state 3', 'color D9C49C')
Capture 'running-cancel' '/clic 100 410 /clic 1050 180 /clic 1000 700' @('color D9C49C', 'page 0 .*state 3 .*color 123456')
Capture 'running-ok' '/clic 100 410 /clic 1050 180 /clic 1170 700' @('page 0 .*state 3 .*color D9C49C')
Capture 'running-look-segments' '/clic 100 360 /clic 870 44 /clic 1050 180' @('page 2 .*state 3', 'page 3 .*state 3 .*color D9C49C')
$results.Add('ALL 5 NATIVE CUSTOMIZATION TESTS PASSED')
$results | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
