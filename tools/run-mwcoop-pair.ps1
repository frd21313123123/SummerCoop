param([ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run = ('mwcoop-' + (Get-Date -Format yyyyMMdd-HHmmss)), [int]$Port = 27991, [int]$Duration = 35, [switch]$Actions, [switch]$Vehicles, [switch]$Garage, [switch]$Workshop, [switch]$Platforms, [switch]$Activities, [switch]$Regression, [switch]$Assembly, [switch]$Carrier, [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$HostGame = 'game', [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ClientGame = 'game-client', [ValidateSet('JONNEZ','BOAT','KEKMET','SATSUMA')][string[]]$VehicleNames,
    [switch]$EngineLifecycle, [switch]$Cabin, [switch]$Performance, [ValidateRange(1,8)][int]$Players = 2, [ValidateRange(0,600)][int]$Warmup = 60,
    [ValidateRange(320,7680)][int]$Width = 640, [ValidateRange(240,4320)][int]$Height = 480,
    [ValidateSet('Local','Host','Client')][string]$Role = 'Local', [string]$JoinAddress = '127.0.0.1',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string[]]$ExtraClientGames,
    [string]$SeedMusicCache,
    [string]$BuildDirectory, [string]$SeedProfile, [switch]$Optimization, [switch]$PartsProfile, [switch]$ToolCases, [switch]$ToolCaseMenu, [switch]$BoltPositions, [switch]$Attachments, [switch]$AttachmentDoors, [switch]$Battery, [ValidateSet('Recovery','Unwired','Reload')][string]$BatteryMode = 'Recovery', [switch]$Shopping, [switch]$Bindings, [switch]$Wallet, [switch]$Cheats, [switch]$Engine, [switch]$SummerFixes, [switch]$MusicOnly, [ValidateSet('Host','Guest')][string]$ActionActor = 'Host', [ValidateSet('Host','Guest')][string]$SaveQuitActor)
$ErrorActionPreference = 'Stop'
if ($AttachmentDoors) { $Attachments = $true }
if ($MusicOnly) { $SummerFixes = $true }
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'build' }
$BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
if ($SeedProfile) { $SeedProfile = (Resolve-Path -LiteralPath $SeedProfile).Path }
if ($SeedMusicCache) { $SeedMusicCache = (Resolve-Path -LiteralPath $SeedMusicCache).Path }
if ($PartsProfile -and !$Performance) { throw 'Parts profiling requires passive performance mode.' }
if ($Performance) {
    if ($SaveQuitActor) { throw 'Save/quit testing cannot run in performance mode.' }
    if ($EngineLifecycle -or $Cabin -or $Actions -or $Vehicles -or $Garage -or $Workshop -or $Platforms -or $Activities -or $Regression -or $Assembly -or $Carrier -or $Optimization -or $BoltPositions -or $Attachments -or $Battery -or $Shopping -or $Bindings -or $Wallet -or $Cheats -or $Engine -or $SummerFixes) { throw 'Performance mode cannot run functional probes.' }
    if (!$PSBoundParameters.ContainsKey('Duration')) { $Duration = 600 }
    if (!$PSBoundParameters.ContainsKey('Width')) { $Width = 2560 }
    if (!$PSBoundParameters.ContainsKey('Height')) { $Height = 1440 }
}
$instances = @()
if ($Role -ne 'Client') { $instances += [pscustomobject]@{ Name='host'; Game=$HostGame; Host=$true } }
if ($Role -ne 'Host' -and $Players -gt 1) { $instances += [pscustomobject]@{ Name='client'; Game=$ClientGame; Host=$false } }
if ($Role -eq 'Local') {
    for ($clientNumber = 2; $clientNumber -lt $Players; $clientNumber++) {
        $gameName = if ($ExtraClientGames -and $ExtraClientGames.Count -ge $clientNumber - 1) { $ExtraClientGames[$clientNumber - 2] } else { 'game-client-' + $clientNumber }
        $instances += [pscustomobject]@{ Name=('client' + $clientNumber); Game=$gameName; Host=$false }
    }
}
if ($instances.Count -eq 0) { throw 'Client mode needs Players >= 2.' }
if (@($instances.Game | Select-Object -Unique).Count -ne $instances.Count) { throw 'Each local game instance must use its own directory.' }
$desktopHost = Join-Path $root 'build\TestDesktopHost.exe'
$desktopSource = Join-Path $PSScriptRoot 'TestDesktopHost.cs'
if (!(Test-Path -LiteralPath $desktopHost) -or (Get-Item -LiteralPath $desktopSource).LastWriteTimeUtc -gt (Get-Item -LiteralPath $desktopHost).LastWriteTimeUtc) {
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe') /nologo /target:winexe ('/out:' + $desktopHost) $desktopSource
    if ($LASTEXITCODE) { throw 'Test desktop helper compilation failed.' }
}
if ($Vehicles -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 135 }
if ($Cabin -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 125 }
if ($EngineLifecycle -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 170 }
if ($Garage -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 140 }
if ($Workshop -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 320 }
if ($Platforms -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 190 }
if ($Activities -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 270 }
if ($Regression -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 295 }
if ($Assembly -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 180 }
if ($Attachments -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 185 }
if ($AttachmentDoors -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 55 }
if ($Battery -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 85 }
if ($Carrier -and !$Assembly -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 135 }
if ($Shopping -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 140 }
if ($Cheats -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = 95 }
if ($SummerFixes -and !$PSBoundParameters.ContainsKey('Duration')) { $Duration = if ($MusicOnly) { 130 } else { 280 } }
$vehicleChannel = Join-Path $local ($Run + '-vehicles')
$garageChannel = Join-Path $local ($Run + '-garage')
$workshopChannel = Join-Path $local ($Run + '-workshop')
$platformChannel = Join-Path $local ($Run + '-platforms')
$regressionChannel = Join-Path $local ($Run + '-regression')
$activitiesChannel = Join-Path $local ($Run + '-activities')
$assemblyChannel = Join-Path $local ($Run + '-assembly')
$carrierChannel = Join-Path $local ($Run + '-carrier')
$shoppingChannel = Join-Path $local ($Run + '-shopping')
$bindingsChannel = Join-Path $local ($Run + '-bindings')
$walletChannel = Join-Path $local ($Run + '-wallet')
$cheatsChannel = Join-Path $local ($Run + '-cheats')
$summerFixChannel = Join-Path $local ($Run + '-summerfixes')
if ($Port -lt 1024 -or $Port -gt 65535 -or $Duration -lt 10 -or $Duration -gt $(if ($Performance) { 3600 } else { 900 })) { throw 'Invalid test port or duration.' }
foreach ($instance in $instances) {
    if (Test-Path -LiteralPath (Join-Path $local ($Run + '-' + $instance.Name))) { throw 'Use a fresh test run name.' }
    $dir = Join-Path $local $instance.Game
    $exe = Join-Path $dir 'mysummercar.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw 'Prepare isolated .local/game and .local/game-client first.' }
    $running = @(Get-Process mysummercar -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
    # Unity may remain visible to name enumeration briefly after its PID wait
    # reports completion. Allow its ordinary native shutdown to finish.
    for ($attempt = 0; $running.Count -gt 0 -and $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 500
        $running = @(Get-Process mysummercar -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
    }
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
if ($Shopping) { New-Item -ItemType Directory -Path $shoppingChannel | Out-Null }
if ($Bindings) { New-Item -ItemType Directory -Path $bindingsChannel | Out-Null }
if ($Wallet) { New-Item -ItemType Directory -Path $walletChannel | Out-Null }
if ($Engine) { New-Item -ItemType Directory -Path (Join-Path $local ($Run + '-engine')) | Out-Null }
if ($Cabin) { New-Item -ItemType Directory -Path (Join-Path $local ($Run + '-cabin')) | Out-Null }
if ($EngineLifecycle) { New-Item -ItemType Directory -Path (Join-Path $local ($Run + '-engine-lifecycle')) | Out-Null }
if ($Cheats) { New-Item -ItemType Directory -Path $cheatsChannel | Out-Null }
if ($SummerFixes) { New-Item -ItemType Directory -Path $summerFixChannel | Out-Null }
$original = Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'
$before = @(Get-ChildItem -LiteralPath $original -File -ErrorAction SilentlyContinue | Where-Object Name -in @('defaultES2File.txt','items.txt','meshsave.txt','graveyard.txt','trophies.txt','carparts.txt') | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $local ($Run + '-original-before.json')) -Encoding utf8
foreach ($instance in $instances) {
    $roleName = $instance.Name
    $dir = Join-Path $local $instance.Game
    $exe = Join-Path $dir 'mysummercar.exe'
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'SummerCoop.dll') -Destination (Join-Path $dir 'Mods\SummerCoop.dll') -Force
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'SummerCoop.Core.dll') -Destination (Join-Path $dir 'Mods\References\SummerCoop.Core.dll') -Force
    $profile = Join-Path $local ($Run + '-' + $roleName)
    if ($SeedMusicCache -and !$instance.Host) {
        New-Item -ItemType Directory -Path $profile -Force | Out-Null
        Copy-Item -LiteralPath $SeedMusicCache -Destination (Join-Path $profile 'music') -Recurse
    }
    if ($SeedProfile -and $instance.Host) {
        $saveDestination = Join-Path $profile 'mwcoop-profiles\host'
        New-Item -ItemType Directory -Path $saveDestination -Force | Out-Null
        Get-ChildItem -LiteralPath $SeedProfile -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $saveDestination }
    }
    $report = Join-Path $local ($Run + '-' + $roleName + '-report.txt')
    $log = Join-Path $local ($Run + '-' + $roleName + '-game.log')
    $mode = if ($instance.Host) { '--summercoop-host-lan ' + $Port } else { '--summercoop-join-lan ' + $JoinAddress + ' --summercoop-port ' + $Port }
    # An invisible desktop isolates input even before mods load. Keep graphics:
    # MSC's Unity 5 crashes while loading GAME with -nographics.
    $sound = if ($SummerFixes) { '' } else { '-nosound ' }
    $arguments = $sound + '-screen-fullscreen 0 -screen-width ' + $Width + ' -screen-height ' + $Height + ' -logFile "' + $log + '" ' + $mode + ' --summercoop-profile "' + $profile + '" --summercoop-name Smoke-' + $roleName + ' --summercoop-test-report "' + $report + '" --summercoop-test-duration ' + $Duration
    $arguments += ' --summercoop-test-players ' + $Players
    if ($Performance) { $arguments += ' --summercoop-test-performance yes --summercoop-test-warmup ' + $Warmup }
    if ($PartsProfile) { $arguments += ' --summercoop-test-parts-profile yes' }
    if ($SummerFixes) { $arguments += ' --summercoop-test-summerfixes "' + $summerFixChannel + '"' }
    if ($MusicOnly) { $arguments += ' --summercoop-test-music-only yes' }
    if ($Optimization) { $arguments += ' --summercoop-test-optimization yes' }
    if ($ToolCases) { $arguments += ' --summercoop-test-toolcases yes' }
    if ($ToolCaseMenu) { $arguments += ' --summercoop-test-toolcases-menu yes' }
    if ($BoltPositions) { $arguments += ' --summercoop-test-bolt-positions yes' }
    if ($Attachments) { $arguments += ' --summercoop-test-attachments ' + $(if ($AttachmentDoors) { 'doors' } else { 'yes' }) }
    if ($Battery) { $arguments += ' --summercoop-test-battery ' + $BatteryMode.ToLowerInvariant() }
    if ($SaveQuitActor) { $arguments += ' --summercoop-test-save-quit ' + $SaveQuitActor }
    if ($Actions) { $arguments += ' --summercoop-test-actions yes' }
    if ($ActionActor -eq 'Guest') { $arguments += ' --summercoop-test-action-actor guest' }
    if ($Assembly) { $arguments += ' --summercoop-test-assembly-channel "' + $assemblyChannel + '"' }
    if ($Carrier) { $arguments += ' --summercoop-test-carrier-channel "' + $carrierChannel + '"' }
    if ($Shopping) { $arguments += ' --summercoop-test-shopping-channel "' + $shoppingChannel + '"' }
    if ($Bindings) { $arguments += ' --summercoop-test-bindings-channel "' + $bindingsChannel + '"' }
    if ($Wallet) { $arguments += ' --summercoop-test-wallet-channel "' + $walletChannel + '"' }
    if ($Engine) { $arguments += ' --summercoop-test-engine-channel ' + [char]34 + (Join-Path $local ($Run + '-engine')) + [char]34 }
    if ($Cabin) { $arguments += ' --summercoop-test-cabin-channel ' + [char]34 + (Join-Path $local ($Run + '-cabin')) + [char]34 }
    if ($EngineLifecycle) { $arguments += ' --summercoop-test-engine-lifecycle ' + [char]34 + (Join-Path $local ($Run + '-engine-lifecycle')) + [char]34 }
    if ($Cheats) { $arguments += ' --summercoop-test-cheats-channel "' + $cheatsChannel + '"' }
    if ($Garage) { $arguments += ' --summercoop-test-garage-channel "' + $garageChannel + '"' }
    if ($Workshop) { $arguments += ' --summercoop-test-workshop-channel "' + $workshopChannel + '"' }
    if ($Vehicles) { $arguments += ' --summercoop-test-vehicle-channel "' + $vehicleChannel + '"' }
    if ($Platforms) { $arguments += ' --summercoop-test-platform-channel "' + $platformChannel + '"' }
    if ($Activities) { $arguments += ' --summercoop-test-activities-channel "' + $activitiesChannel + '"' }
    if ($Regression) { $arguments += ' --summercoop-test-regression-channel ' + [char]34 + $regressionChannel + [char]34 }
    if ($VehicleNames) { $arguments += ' --summercoop-test-vehicle-list ' + ($VehicleNames -join ',') }
    $pidFile = Join-Path $local ($Run + '-' + $roleName + '.pid')
    $desktopName = 'SummerCoopTest-' + $Run
    $supervisorTimeout = if ($Performance) { $Duration + $Warmup + 720 } else { [Math]::Max(660, $Duration + 360) }
    $encoded = @($exe,$arguments,$dir,$desktopName,$pidFile,[string]$supervisorTimeout) | ForEach-Object { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($_)) }
    $supervisor = Start-Process -FilePath $desktopHost -WorkingDirectory $root -ArgumentList $encoded -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(10)
    while (!(Test-Path -LiteralPath $pidFile) -and !(Test-Path -LiteralPath ($pidFile + '.error')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath ($pidFile + '.error')) { throw (Get-Content -LiteralPath ($pidFile + '.error') -Raw) }
    if (!(Test-Path -LiteralPath $pidFile)) { throw 'Isolated desktop launch timed out.' }
    [pscustomobject]@{ Role=$roleName; Id=[int](Get-Content -LiteralPath $pidFile); SupervisorId=$supervisor.Id; Desktop=$desktopName; GameDirectory=$dir; Profile=$profile; Report=$report; Log=$log; Performance=[bool]$Performance; Players=$Players; Width=$Width; Height=$Height; SharedMachine=($instances.Count -gt 1) } | ConvertTo-Json -Compress
}
