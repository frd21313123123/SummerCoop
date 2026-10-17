param(
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,28}$')][string]$Run = ('opt-' + (Get-Date -Format yyyyMMdd-HHmmss)),
    [ValidateSet('Smoke','GuestActions','Assembly','Workshop','Vehicles','Garage','Platforms','Activities','Regression')][string[]]$Cases = @('Smoke','GuestActions','Assembly','Workshop','Vehicles','Garage','Platforms','Activities','Regression'),
    [ValidateRange(2,4)][int]$Players = 4, [string]$BuildDirectory, [string]$SeedProfile,
    [int]$Port = 28200, [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'build' }
if (!$SeedProfile) { $SeedProfile = Join-Path $root '.local\performance-seed' }
$testedModule = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $BuildDirectory 'SummerCoop.dll'))).ManifestModule.ModuleVersionId.ToString()
$definitions = @{
    Smoke=@{Duration=45;Actions=$true}
    GuestActions=@{Duration=45;Actions=$true;ActionActor='Guest'}
    Assembly=@{Duration=240;Assembly=$true;Carrier=$true}
    Workshop=@{Duration=330;Workshop=$true}
    Vehicles=@{Duration=135;Vehicles=$true}
    Garage=@{Duration=180;Garage=$true}
    Platforms=@{Duration=240;Platforms=$true}
    Activities=@{Duration=270;Activities=$true}
    Regression=@{Duration=420;Regression=$true}
}
$evidence = [Collections.Generic.List[object]]::new()
foreach ($case in $Cases) {
    $caseRun = $Run + '-' + $case.ToLowerInvariant()
    # Complex pair fixtures instantiate matching local test products on both actors.
    # Use the existing two-peer fixtures; real four-player starter/vehicle/platform tests are separate.
    $casePlayers = if ($case -in @('Assembly','Workshop','Activities','Regression')) { 2 } else { $Players }
    $invoke = @{
        Run=$caseRun; Port=$Port++; Players=$casePlayers; Optimization=$true
        BuildDirectory=$BuildDirectory; SeedProfile=$SeedProfile
        ExtraClientGames=@('game-assembly-host','game-assembly-client')
    }
    foreach ($key in $definitions[$case].Keys) { $invoke[$key] = $definitions[$case][$key] }
    if ($VerifyOnly) {
        $gameNames = @('game','game-client','game-assembly-host','game-assembly-client')
        $launches = @(for ($index=0; $index -lt $casePlayers; $index++) {
            $role = if ($index -eq 0) { 'host' } elseif ($index -eq 1) { 'client' } else { 'client' + $index }
            $profile = Join-Path $root ('.local\' + $caseRun + '-' + $role)
            [pscustomobject]@{Role=$role;Profile=$profile;Report=($profile + '-report.txt');GameDirectory=(Join-Path $root ('.local\' + $gameNames[$index]))}
        })
    } else {
        $launches = @(& (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') @invoke | ForEach-Object { $_ | ConvertFrom-Json })
        $launches | ConvertTo-Json -Compress
        $launches | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root ('.local\' + $caseRun + '-launches.json')) -Encoding UTF8
        $deadline = (Get-Date).AddSeconds($invoke.Duration + 240)
        while (@($launches | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue }).Count -and (Get-Date) -lt $deadline) {
            foreach ($launch in $launches) {
                $log = Join-Path $launch.Profile 'mwcoop\logs\mwcoop.log'
                if ((Test-Path -LiteralPath $log) -and (Get-Content -LiteralPath $log -Raw) -match 'ERREUR MSC ([a-z]+ probe:|compatibility:)') {
                    foreach ($owned in $launches) {
                        $process = Get-Process -Id $owned.Id -ErrorAction SilentlyContinue
                        if ($process -and $process.Path -eq (Join-Path $owned.GameDirectory 'mysummercar.exe')) { Stop-Process -Id $process.Id -Force }
                    }
                    throw ('Functional probe failed; inspect ' + $log)
                }
            }
            Start-Sleep -Seconds 2
        }
    }
    $actionStage = -1
    $starterStages = [Collections.Generic.List[int]]::new()
    foreach ($launch in $launches) {
        if (!(Test-Path -LiteralPath $launch.Report)) { throw ('Missing functional report: ' + $launch.Report) }
        $report = Get-Content -LiteralPath $launch.Report -Raw
        if ($report -notmatch ('(?m)^ModMvid=' + [regex]::Escape($testedModule) + '\r?$')) { throw ('Report belongs to another DLL: ' + $launch.Report) }
        if ($report -match '(?m)^(ActionError|ModuleError)=' -or $report -match '(Pass=False|Mismatches=[1-9]|WrongInstalled=[1-9]|WrongJoints=[1-9]|WrongStages=[1-9]|StateErrors=[1-9]|Failed=[1-9]|OptimizationCache=FAIL)') {
            throw ('Gameplay regression: ' + $launch.Report + [Environment]::NewLine + $report)
        }
        if ($report -notmatch ('(?m)^ReadyPlayers=' + $casePlayers + '\r?$') -or $report -notmatch '(?m)^OptimizationCache=PASS' -or $report -notmatch '(?m)^RepairCache=PASS ' -or $report -notmatch '(?m)^PartOwnership=PASS ') { throw ('Participants or cache/ownership checks incomplete: ' + $launch.Report) }
        if ($case -eq 'Garage') {
            foreach ($line in ($report -split '\r?\n' | Where-Object { $_.StartsWith('Garage=') })) {
                foreach ($metric in @('RootError','LevelError','CarError')) {
                    $match = [regex]::Match($line, ($metric + '=([0-9.]+)'))
                    $limit = if ($metric -eq 'LevelError') { .01 } else { .08 }
                    if (!$match.Success -or [double]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) -gt $limit) { throw ('Jack convergence exceeded tolerance: ' + $line) }
                }
                if ($line -match 'Tracked=False|Problem=.+') { throw ('Jack native action failed: ' + $line) }
            }
        }
        if ($case -in @('Smoke','GuestActions')) {
            if ($report -match '(?m)^Starter=[^\r\n]*;Bolt=([0-9]+)') { $starterStages.Add([int]$Matches[1]) }
            if ($report -match '(?m)^StarterAction=Before=[^\r\n]*;Bolt=([0-9]+);After=[^\r\n]*;Bolt=([0-9]+)') {
                if ($Matches[1] -eq $Matches[2]) { throw ('Bolt exercise made no change: ' + $launch.Report) }
                $actionStage = [int]$Matches[2]
            }
        }
        if ($launch.Role -eq 'host' -or $report -match 'Role=(actor|observer|driver|rider)') {
            $required = switch ($case) { 'Assembly' {'Assembly='} 'Workshop' {'Workshop='} 'Vehicles' {'VehicleProbe='} 'Garage' {'Garage='} 'Platforms' {'Platform='} 'Activities' {'Activity='} 'Regression' {'Regression='} default {'Starter=Installed=True'} }
            if ($report -notmatch [regex]::Escape($required)) { throw ('Functional scenario missing: ' + $required + ' in ' + $launch.Report) }
            $requiredCounts = switch ($case) {
                'Assembly' { @{Assembly=4;Carrier=9} }
                'Workshop' { @{Workshop=4;Wiring=2;Belt=3} }
                'Vehicles' { @{VehicleProbe=8} }
                'Garage' { @{Garage=4} }
                'Platforms' { @{Platform=6} }
                'Activities' { @{Activity=$(if ($launch.Role -eq 'host') {26} else {25})} }
                'Regression' { @{Regression=$(if ($launch.Role -eq 'host') {29} else {28})} }
                default { @{} }
            }
            foreach ($prefix in $requiredCounts.Keys) {
                if ([regex]::Matches($report, ('(?m)^' + $prefix + '=')).Count -ne $requiredCounts[$prefix]) { throw ('Incomplete ' + $prefix + ' phases: ' + $launch.Report) }
            }
        }
        $runtimeLog = Join-Path $launch.Profile 'mwcoop\logs\mwcoop.log'
        if ((Get-Content -LiteralPath $runtimeLog -Raw) -match 'MSC compatibility:|ERREUR message (Part|Bolt|Setting|Home|Prop|Shop)\b|MSC assembly unavailable after retry') { throw ('Runtime synchronization error: ' + $runtimeLog) }
        foreach ($dll in @('SummerCoop.dll','SummerCoop.Core.dll')) {
            $relative = if ($dll -eq 'SummerCoop.Core.dll') { 'Mods\References\' + $dll } else { 'Mods\' + $dll }
            if ((Get-FileHash -LiteralPath (Join-Path $launch.GameDirectory $relative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $BuildDirectory $dll)).Hash) { throw ('Test build changed: ' + $launch.GameDirectory) }
        }
        $evidence.Add([pscustomobject]@{Case=$case;Role=$launch.Role;Players=$casePlayers;Report=$launch.Report;Passed=$true})
        Write-Output ('PASS ' + $case + '/' + $launch.Role + ' players=' + $casePlayers)
    }
    if ($case -in @('Smoke','GuestActions') -and ($actionStage -lt 0 -or $starterStages.Count -ne $casePlayers -or @($starterStages | Where-Object { $_ -ne $actionStage }).Count)) { throw ('Native bolt did not converge on all replicas: ' + $caseRun) }
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root ('.local\' + $Run + '-verification.json')) -Encoding UTF8
}
