param([ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run = ('mwcoop-' + (Get-Date -Format yyyyMMdd-HHmmss)), [int]$Port = 27991, [int]$Duration = 35, [switch]$Actions, [switch]$Vehicles, [switch]$Garage, [switch]$Workshop, [switch]$Platforms, [switch]$Activities, [ValidateSet('JONNEZ','BOAT','KEKMET','SATSUMA')][string[]]$VehicleNames)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
if ($Vehicles -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 135 }
if ($Garage -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 140 }
if ($Workshop -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 295 }
if ($Platforms -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 190 }
if ($Activities -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 270 }
$vehicleChannel = Join-Path $local ($Run + '-vehicles')
$garageChannel = Join-Path $local ($Run + '-garage')
$workshopChannel = Join-Path $local ($Run + '-workshop')
$platformChannel = Join-Path $local ($Run + '-platforms')
$activitiesChannel = Join-Path $local ($Run + '-activities')
if ($Port -lt 1024 -or $Port -gt 65535 -or $Duration -lt 10 -or $Duration -gt 300) { throw 'Invalid test port or duration.' }
foreach ($role in @('host','client')) {
    if (Test-Path -LiteralPath (Join-Path $local ($Run + '-' + $role))) { throw 'Use a fresh test run name.' }
    $dir = Join-Path $local $(if ($role -eq 'host') { 'game' } else { 'game-client' })
    $exe = Join-Path $dir 'mysummercar.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw 'Prepare isolated .local/game and .local/game-client first.' }
    $running = @(Get-Process mysummercar -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
    if ($running.Count -gt 0) { throw ('Test copy already running: ' + $exe) }
}
if ($Vehicles) {
    if (Test-Path -LiteralPath $vehicleChannel) { throw 'Use a fresh vehicle test run name.' }
    New-Item -ItemType Directory -Path $vehicleChannel | Out-Null
}
if ($Garage) {
    if (Test-Path -LiteralPath $garageChannel) { throw 'Use a fresh garage test run name.' }
    New-Item -ItemType Directory -Path $garageChannel | Out-Null
}
if ($Workshop) {
    if (Test-Path -LiteralPath $workshopChannel) { throw 'Use a fresh workshop test run name.' }
    New-Item -ItemType Directory -Path $workshopChannel | Out-Null
}
if ($Platforms) {
    if (Test-Path -LiteralPath $platformChannel) { throw 'Use a fresh platform test run name.' }
    New-Item -ItemType Directory -Path $platformChannel | Out-Null
}
if ($Activities) {
    if (Test-Path -LiteralPath $activitiesChannel) { throw 'Use a fresh activities test run name.' }
    New-Item -ItemType Directory -Path $activitiesChannel | Out-Null
}
$original = Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'
$before = @(Get-ChildItem -LiteralPath $original -File -ErrorAction SilentlyContinue | Where-Object Name -in @('defaultES2File.txt','items.txt','meshsave.txt','graveyard.txt','trophies.txt','carparts.txt') | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $local ($Run + '-original-before.json')) -Encoding utf8
foreach ($role in @('host','client')) {
    $dir = Join-Path $local $(if ($role -eq 'host') { 'game' } else { 'game-client' })
    Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.dll') -Destination (Join-Path $dir 'Mods\SummerCoop.dll') -Force
    Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination (Join-Path $dir 'Mods\References\SummerCoop.Core.dll') -Force
    $profile = Join-Path $local ($Run + '-' + $role)
    $report = Join-Path $local ($Run + '-' + $role + '-report.txt')
    $log = Join-Path $local ($Run + '-' + $role + '-game.log')
    $mode = if ($role -eq 'host') { '--summercoop-host-lan ' + $Port } else { '--summercoop-join-lan 127.0.0.1 --summercoop-port ' + $Port }
    $arguments = '-screen-fullscreen 0 -screen-width 640 -screen-height 480 -logFile "' + $log + '" ' + $mode + ' --summercoop-profile "' + $profile + '" --summercoop-name Smoke-' + $role + ' --summercoop-test-report "' + $report + '" --summercoop-test-duration ' + $Duration
    if ($Actions) { $arguments += ' --summercoop-test-actions yes' }
    if ($Garage) { $arguments += ' --summercoop-test-garage-channel "' + $garageChannel + '"' }
    if ($Workshop) { $arguments += ' --summercoop-test-workshop-channel "' + $workshopChannel + '"' }
    if ($Vehicles) { $arguments += ' --summercoop-test-vehicle-channel "' + $vehicleChannel + '"' }
    if ($Platforms) { $arguments += ' --summercoop-test-platform-channel "' + $platformChannel + '"' }
    if ($Activities) { $arguments += ' --summercoop-test-activities-channel "' + $activitiesChannel + '"' }
    if ($VehicleNames) { $arguments += ' --summercoop-test-vehicle-list ' + ($VehicleNames -join ',') }
    $process = Start-Process -FilePath (Join-Path $dir 'mysummercar.exe') -WorkingDirectory $dir -ArgumentList $arguments -WindowStyle Hidden -PassThru
    [pscustomobject]@{ Role=$role; Id=$process.Id; Profile=$profile; Report=$report; Log=$log } | ConvertTo-Json -Compress
}
