param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run, [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$HostGame = 'game', [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ClientGame = 'game-client')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$model = Get-Content -LiteralPath (Join-Path $root 'src/Core/Model.cs') -Raw
if ($model -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw 'Missing mod version.' }
$version = $Matches[1]
$session = Get-Content -LiteralPath (Join-Path $root 'vendor/MWCoop/src/MWCoop/Net/Session.cs') -Raw
if ($session -notmatch 'NetVersion\s*=\s*([0-9]+)') { throw 'Missing network version.' }
$network = $Matches[1]
$channel = Join-Path $root ('.local/' + $Run + '-assembly')
$hostManifest = Get-Content -LiteralPath (Join-Path $channel 'host-manifest.txt')
$guestManifest = Get-Content -LiteralPath (Join-Path $channel 'guest-manifest.txt')
if (@(Compare-Object $hostManifest $guestManifest).Count) { throw 'Peers tested different assembly slots.' }
foreach ($part in @('/Subframe|','/Wishbone_FL|','/Wishbone_FR|','/CamshaftGear|','/SteeringWheel|','/Oilpan|','/Piston1|','/Piston2|','/Piston3|','/Piston4|')) {
    if (!($hostManifest | Select-String -SimpleMatch $part)) { throw ('Missing regression part: ' + $part) }
}
foreach ($corner in @('FL','FR','RL','RR')) {
    if (!($hostManifest | Select-String -SimpleMatch ('TriggerWheel' + $corner + '_New['))) { throw ('Missing wheel mount: ' + $corner) }
}
$rows = @(('SummerCoop ' + $version + ' - native car assembly verification'), ('Recorded: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')), ('Run: ' + $Run), '')
foreach ($role in @('host','client')) {
    $report = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '-report.txt')))
    if ($report -notcontains ('ModVersion=' + $version) -or $report -notcontains ('NetVersion=' + $network) -or $report -notcontains 'Level=GAME') { throw ('Wrong native build/level: ' + $role) }
    if ($role -eq 'client' -and $report -notcontains 'SaveReceived=True') { throw 'Guest did not receive the native save.' }
    $checks = @($report | Where-Object { $_ -match '^Assembly=' })
    if ($checks.Count -ne 4) { throw ('Incomplete assembly phases: ' + $role) }
    foreach ($phase in 0..3) {
        $check = @($checks | Where-Object { $_ -match ('^Assembly=' + $phase + ';') })
        if ($check.Count -ne 1 -or $check[0] -notmatch ';Mismatches=0;WrongInstalled=0;WrongJoints=0;WrongRotation=0;Tilted=[05]$' -or $check[0] -notmatch ';Parts=(1[0-9][0-9]|[2-9][0-9][0-9]);') { throw ('Assembly phase failed: ' + $role + ' ' + ($check -join ' ')) }
        $tilts = if ($phase % 2 -eq 1 -and $check[0] -match ';Role=actor;') { 5 } else { 0 }
        if ($check[0] -notmatch (';Tilted=' + $tilts + '$')) { throw ('Rotation regression was not exercised: ' + $role + ' phase ' + $phase) }
    }
    $carrier = @($report | Where-Object { $_ -match '^Carrier=' })
    if (Test-Path -LiteralPath (Join-Path $root ('.local/' + $Run + '-carrier'))) {
        $expectedCarrier = [ordered]@{
            'host-stationary-add'=1; 'guest-take'=0; 'guest-stationary-add'=1; 'host-take'=0;
            'concurrent-add'=2; 'both-take'=0; 'capacity-nine'=9; 'full-preserves-wood'=9; 'refill-after-full'=9
        }
        if ($carrier.Count -ne $expectedCarrier.Count) { throw ('Incomplete carrier checks: ' + $role) }
        foreach ($label in $expectedCarrier.Keys) {
            $count = $expectedCarrier[$label]
            if ($carrier -notcontains ('Carrier=' + $label + ';Count=' + $count + ';Visible=' + $count + ';Pass=True')) { throw ('Carrier check failed: ' + $role + ' ' + $label) }
        }
    }
    if ($report -match '^(ActionError|ModuleError)=') { throw ('Native probe/module error: ' + $role) }
    $log = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '/mwcoop/logs/mwcoop.log')) -Raw
    if ($log -match 'ERREUR message (?:Part|Bolt|Setting|Home)\b|MSC (?:assembly|carrier) probe:|MSC compatibility:|MSC ambiguous (?:assembly|tuning)|MSC assembly unavailable after retry') { throw ('Native sync error: ' + $role) }
    $game = Join-Path $root $(if ($role -eq 'host') { '.local/' + $HostGame } else { '.local/' + $ClientGame })
    foreach ($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $game ('Mods/' + $dll)) -Algorithm SHA256).Hash
        $built = (Get-FileHash -LiteralPath (Join-Path $root ('build/' + [IO.Path]::GetFileName($dll)))).Hash
        if ($tested -ne $built) { throw ('Tested DLL differs from current build: ' + $dll) }
        $rows += $role + ' SHA256 ' + $tested + '  ' + $dll
    }
    $rows += @('', ($role + ' report:'))
    $rows += @($report | Where-Object { $_ -match '^(Engine|ModVersion|NetVersion|ModMvid|Level|Ready|Players|SaveReceived|Assembly|Carrier|FrameP95ms|FrameMaxms)=' })
}
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) { throw 'An original single-player save changed.' } }
$rows += @('', ('Original single-player save files checked: ' + $before.Count + '; changed: 0'))
foreach ($test in @('build/test-results.txt','build/msc-tests/test-results.txt')) {
    $summary = @(Get-Content -LiteralPath (Join-Path $root $test) | Where-Object { $_ -match '^ALL .* PASSED$' })
    if ($summary.Count -ne 1) { throw ('Missing test result: ' + $test) }
    $rows += $test + ': ' + $summary[0]
}
$rows += @('', 'Scope: native assembly/removal actions over the real MWCoop UDP transport, both actor directions.',
    'Timing: native camshaft Adjust actions; absolute angle, stored database angle and mounted pose compared.',
    'Rotation: four pistons and oilpan tilted after native parenting in both actor directions; checked against native zero local rotation.',
    'Fixture: physics steps paused with stationary rigidbodies; inactive assembly triggers enabled and joint setup follows native transitions.',
    'Carrier (when included): stationary deposits by both actors, both withdrawals, concurrent deposits, capacity nine, excess log preserved and refill.',
    'Limits: not a manual playthrough, road/crash physics, running-engine validation or two-account Steam/WAN test.')
$output = Join-Path $root 'docs/ASSEMBLY-SYNC-RESULTS.txt'
$rows | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output $output
