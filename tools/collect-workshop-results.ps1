param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run, [switch]$ModOnly,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$HostGame = 'game', [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ClientGame = 'game-client')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$model = [IO.File]::ReadAllText((Join-Path $root 'src/Core/Model.cs'))
if ($model -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw 'Missing mod version.' }
$version = $Matches[1]
$session = [IO.File]::ReadAllText((Join-Path $root 'vendor/MWCoop/src/MWCoop/Net/Session.cs'))
if ($session -notmatch 'NetVersion\s*=\s*([0-9]+)') { throw 'Missing network version.' }
$networkVersion = $Matches[1]
$rows = @(('SummerCoop ' + $version + ' - native workshop verification'), ('Recorded: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')), ('Run: ' + $Run), '')
foreach ($role in @('host','client')) {
    $reportPath = Join-Path $root ('.local/' + $Run + '-' + $role + '-report.txt')
    $report = @(Get-Content -LiteralPath $reportPath -Encoding UTF8)
    if ($report -notcontains ('ModVersion=' + $version) -or $report -notcontains ('NetVersion=' + $networkVersion) -or $report -notcontains 'Level=GAME') { throw ('Wrong native build or level: ' + $role) }
    if ($role -eq 'client' -and $report -notcontains 'SaveReceived=True') { throw 'Guest did not receive the native save.' }
    foreach ($kind in @(@{Pattern='^Workshop=';Count=5},@{Pattern='^Wiring=';Count=2},@{Pattern='^Belt=';Count=3})) {
        $results = @($report | Where-Object { $_ -match $kind.Pattern })
        if ($results.Count -ne $kind.Count) { throw ('Incomplete ' + $kind.Pattern + ' checks: ' + $role) }
        foreach ($line in $results) { if ($line -notmatch ';Mismatches=0(?:;|$)' -or $line -match '(?:WrongStages|WrongBolted|StateErrors|Failed|LooseTerminals)=[1-9]') { throw ('Native mismatch: ' + $role + ' ' + $line) } }
    }
    $races = @($report | Where-Object { $_ -match '^BoltRepairRace=' })
    if ($races.Count -ne 5 -or $races -match 'LostTurns=[1-9]' -or $role -eq 'client' -and !($races -match 'Attempts=[1-9]')) { throw ('Missing or failed bolt repair race checks: ' + $role) }
    if ($report -match '^(ActionError|ModuleError)=') { throw ('Native probe/module error: ' + $role) }
    $log = [IO.File]::ReadAllText((Join-Path $root ('.local/' + $Run + '-' + $role + '/mwcoop/logs/mwcoop.log')))
    if ($log -match 'ERREUR message (?:Part|Bolt|Setting)\b|MSC (?:workshop|belt|wiring) probe:') { throw ('Native message error: ' + $role) }
    if ($role -eq 'host' -and !($report -match '^WorkshopSave=Floats=[1-9][0-9]*;WireFlags=[1-9][0-9]*;BeltFlags=[1-9][0-9]*;Mismatches=0$')) { throw 'Missing native ES2 persistence check.' }
    $game = Join-Path (Join-Path $root '.local') $(if ($role -eq 'host') { $HostGame } else { $ClientGame })
    foreach ($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $game ('Mods/' + $dll)) -Algorithm SHA256).Hash
        $built = (Get-FileHash -LiteralPath (Join-Path $root ('build/' + [IO.Path]::GetFileName($dll))) -Algorithm SHA256).Hash
        if ($tested -ne $built) { throw ('Tested DLL differs from the current build: ' + $dll) }
        $rows += $role + ' SHA256 ' + $tested + '  ' + $dll
    }
    $rows += @('', ($role + ' report:'))
    $rows += @($report | Where-Object { $_ -match '^(Engine|ModVersion|NetVersion|Level|Ready|Players|SaveReceived|Workshop|BoltRepairRace|Wiring|Belt|WorkshopSave|FrameP95ms|FrameMaxms)=' })
}
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) { throw 'An original single-player save changed.' } }
$rows += @('', ('Original single-player save files checked: ' + $before.Count + '; changed: 0'), '',
    'Automated checks (component test counts are not counts of native gameplay scenarios):')
$testFiles = @('build/test-results.txt','build/msc-tests/test-results.txt','build/mwcoop-network-tests/test-results.txt')
if (!$ModOnly) { $testFiles += @('build/installer-tests/test-results.txt','build/launcher-tests/test-results.txt') }
foreach ($test in $testFiles) {
    $summary = @(Get-Content -LiteralPath (Join-Path $root $test) | Where-Object { $_ -match '^ALL .* PASSED$|^PASS launcher GUI:' })
    if ($summary.Count -eq 0) { throw ('Missing test result: ' + $test) }
    $rows += $test + ': ' + ($summary -join ' | ')
}
$rows += @('',
    'Fixture limits: native FSM actions replace mouse input; prerequisite parts are mounted by the fixture.',
    'Wiring Status gates are disabled to expose unmounted connectors; the belt is spawned natively on both peers.',
    'Physics is paused only for the five workshop phases and resumed for native wiring/belt timers.',
    'The fifth phase tightens fully loose fasteners as the guest; the belt fixture then releases the alternator pivot natively.',
    'Delayed pre-turn repair packets are injected explicitly in QA mode; LostTurns must remain zero.',
    'No complete Satsuma build, running-engine electrical circuit, shop purchase or two-account Steam/WAN test.',
    'See WORKSHOP.md for reproduction and scope. This report covers the tested scenarios, not every gameplay path.')
$output = Join-Path $root 'docs/WORKSHOP-RESULTS.txt'
$rows | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output $output
