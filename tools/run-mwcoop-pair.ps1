param([ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run = ('mwcoop-' + (Get-Date -Format yyyyMMdd-HHmmss)), [int]$Port = 27991, [int]$Duration = 35, [switch]$Actions, [switch]$Vehicles, [switch]$Garage, [switch]$Workshop, [switch]$Platforms, [switch]$Activities, [switch]$Regression, [switch]$Assembly, [switch]$Carrier, [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$HostGame = 'game', [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ClientGame = 'game-client', [ValidateSet('JONNEZ','BOAT','KEKMET','SATSUMA')][string[]]$VehicleNames)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
$desktopHost = Join-Path $root 'build\TestDesktopHost.exe'
$desktopSource = Join-Path $PSScriptRoot 'TestDesktopHost.cs'
if (!(Test-Path -LiteralPath $desktopHost) -or (Get-Item -LiteralPath $desktopSource).LastWriteTimeUtc -gt (Get-Item -LiteralPath $desktopHost).LastWriteTimeUtc) {
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe') /nologo /target:winexe ('/out:' + $desktopHost) $desktopSource
    if ($LASTEXITCODE) { throw 'Test desktop helper compilation failed.' }
}
if ($Vehicles -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 135 }
if ($Garage -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 140 }
if ($Workshop -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 295 }
if ($Platforms -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 190 }
if ($Activities -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 270 }
if ($Regression -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 295 }
if ($Assembly -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 180 }
if ($Carrier -and !$Assembly -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 135 }
$vehicleChannel = Join-Path $local ($Run + '-vehicles')
$garageChannel = Join-Path $local ($Run + '-garage')
$workshopChannel = Join-Path $local ($Run + '-workshop')
$platformChannel = Join-Path $local ($Run + '-platforms')
$regressionChannel = Join-Path $local ($Run + '-regression')
$activitiesChannel = Join-Path $local ($Run + '-activities')
$assemblyChannel = Join-Path $local ($Run + '-assembly')
$carrierChannel = Join-Path $local ($Run + '-carrier')
if ($Port -lt 1024 -or $Port -gt 65535 -or $Duration -lt 10 -or $Duration -gt 300) { throw 'Invalid test port or duration.' }
foreach ($role in @('host','client')) {
    if (Test-Path -LiteralPath (Join-Path $local ($Run + '-' + $role))) { throw 'Use a fresh test run name.' }
    $dir = Join-Path $local $(if ($role -eq 'host') { $HostGame } else { $ClientGame })
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
if ($Regression) {
    if (Test-Path -LiteralPath $regressionChannel) { throw 'Use a fresh regression test run name.' }
    New-Item -ItemType Directory -Path $regressionChannel | Out-Null
}
if ($Activities) {
    if (Test-Path -LiteralPath $activitiesChannel) { throw 'Use a fresh activities test run name.' }
    New-Item -ItemType Directory -Path $activitiesChannel | Out-Null
}
if ($Assembly) { New-Item -ItemType Directory -Path $assemblyChannel | Out-Null }
if ($Carrier) { New-Item -ItemType Directory -Path $carrierChannel | Out-Null }
$original = Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'
$before = @(Get-ChildItem -LiteralPath $original -File -ErrorAction SilentlyContinue | Where-Object Name -in @('defaultES2File.txt','items.txt','meshsave.txt','graveyard.txt','trophies.txt','carparts.txt') | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $local ($Run + '-original-before.json')) -Encoding utf8
foreach ($role in @('host','client')) {
    $dir = Join-Path $local $(if ($role -eq 'host') { $HostGame } else { $ClientGame })
    $exe = Join-Path $dir 'mysummercar.exe'
    Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.dll') -Destination (Join-Path $dir 'Mods\SummerCoop.dll') -Force
    Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination (Join-Path $dir 'Mods\References\SummerCoop.Core.dll') -Force
    $profile = Join-Path $local ($Run + '-' + $role)
    $report = Join-Path $local ($Run + '-' + $role + '-report.txt')
    $log = Join-Path $local ($Run + '-' + $role + '-game.log')
    $mode = if ($role -eq 'host') { '--summercoop-host-lan ' + $Port } else { '--summercoop-join-lan 127.0.0.1 --summercoop-port ' + $Port }
    # An invisible desktop isolates input even before mods load. Keep graphics:
    # MSC's Unity 5 crashes while loading GAME with -nographics.
    $arguments = '-nosound -screen-fullscreen 0 -screen-width 640 -screen-height 480 -logFile "' + $log + '" ' + $mode + ' --summercoop-profile "' + $profile + '" --summercoop-name Smoke-' + $role + ' --summercoop-test-report "' + $report + '" --summercoop-test-duration ' + $Duration
    if ($Actions) { $arguments += ' --summercoop-test-actions yes' }
    if ($Assembly) { $arguments += ' --summercoop-test-assembly-channel "' + $assemblyChannel + '"' }
    if ($Carrier) { $arguments += ' --summercoop-test-carrier-channel "' + $carrierChannel + '"' }
    if ($Garage) { $arguments += ' --summercoop-test-garage-channel "' + $garageChannel + '"' }
    if ($Workshop) { $arguments += ' --summercoop-test-workshop-channel "' + $workshopChannel + '"' }
    if ($Vehicles) { $arguments += ' --summercoop-test-vehicle-channel "' + $vehicleChannel + '"' }
    if ($Platforms) { $arguments += ' --summercoop-test-platform-channel "' + $platformChannel + '"' }
    if ($Activities) { $arguments += ' --summercoop-test-activities-channel "' + $activitiesChannel + '"' }
    if ($Regression) { $arguments += ' --summercoop-test-regression-channel ' + [char]34 + $regressionChannel + [char]34 }
    if ($VehicleNames) { $arguments += ' --summercoop-test-vehicle-list ' + ($VehicleNames -join ',') }
    $pidFile = Join-Path $local ($Run + '-' + $role + '.pid')
    $desktopName = 'SummerCoopTest-' + $Run
    $encoded = @($exe,$arguments,$dir,$desktopName,$pidFile) | ForEach-Object { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($_)) }
    $supervisor = Start-Process -FilePath $desktopHost -WorkingDirectory $root -ArgumentList $encoded -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(10)
    while (!(Test-Path -LiteralPath $pidFile) -and !(Test-Path -LiteralPath ($pidFile + '.error')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath ($pidFile + '.error')) { throw (Get-Content -LiteralPath ($pidFile + '.error') -Raw) }
    if (!(Test-Path -LiteralPath $pidFile)) { throw 'Isolated desktop launch timed out.' }
    [pscustomobject]@{ Role=$role; Id=[int](Get-Content -LiteralPath $pidFile); SupervisorId=$supervisor.Id; Desktop=$desktopName; Profile=$profile; Report=$report; Log=$log } | ConvertTo-Json -Compress
}
