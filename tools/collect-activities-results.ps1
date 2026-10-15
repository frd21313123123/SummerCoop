param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$model = [IO.File]::ReadAllText((Join-Path $root 'src/Core/Model.cs'))
if ($model -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw 'Missing mod version.' }
$version = $Matches[1]
$session = [IO.File]::ReadAllText((Join-Path $root 'vendor/MWCoop/src/MWCoop/Net/Session.cs'))
if ($session -notmatch 'NetVersion\s*=\s*([0-9]+)') { throw 'Missing network version.' }
$networkVersion = $Matches[1]
$cases = @('ingredients','wood-split','water','stove-wood','lid-mounted','lid-removed','stove-concurrent','stove-fire','stove-full',
    'sauna-steam','sauna-heat','kilju-finished','wood-burned','kilju-bottled','stove-capacity-race','native-save-kilju','bucket-empty',
    'grill-opened','grill-cooked','grill-burnt','wood-cycle','coffee-brewed','coffee-poured','coffee-drunk','native-save-coffee')
$rows = @(('SummerCoop ' + $version + ' - native activities verification'), ('Recorded: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')), ('Run: ' + $Run), '')
foreach ($role in @('host','client')) {
    $report = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '-report.txt')) -Encoding UTF8)
    foreach ($required in @(('ModVersion=' + $version), ('NetVersion=' + $networkVersion), 'Level=GAME')) {
        if ($report -notcontains $required) { throw ('Wrong native build or incomplete connection: ' + $role + ' ' + $required) }
    }
    if ($role -eq 'client' -and ($report -notcontains 'SaveReceived=True' -or $report -notcontains 'Ready=True' -or $report -notcontains 'Players=2')) { throw 'Guest did not complete the native connection/save transfer.' }
    $expected = @($cases); if ($role -eq 'host') { $expected += 'steam-personal' }
    $results = @($report | Where-Object { $_ -match '^Activity=' })
    if ($results.Count -ne $expected.Count) { throw ('Incomplete activities checks: ' + $role + ' ' + $results.Count + '/' + $expected.Count) }
    foreach ($case in $expected) {
        if (@($results | Where-Object { $_ -match ('^Activity=' + [regex]::Escape($case) + ';Pass=True;') }).Count -ne 1) { throw ('Missing or failed native scenario: ' + $role + ' ' + $case) }
    }
    if ($report -match '^(ActionError|ModuleError)=') { throw ('Native probe/module error: ' + $role) }
    $log = [IO.File]::ReadAllText((Join-Path $root ('.local/' + $Run + '-' + $role + '/mwcoop/logs/mwcoop.log')))
    if ($log -match 'ERREUR message (?:Home|Fire|Cook|Consume)\b|ERREUR crochet|MSC activities probe:|MSC compatibility: MscBrewing') { throw ('Native activity or message error: ' + $role) }
    $nativeLog = [IO.File]::ReadAllText((Join-Path $root ('.local/' + $Run + '-' + $role + '-game.log')))
    if ($nativeLog -match 'Invalid AABB|transform.position.*NaN|transform.rotation.*NaN') { throw ('Invalid native physics state: ' + $role) }
    $game = Join-Path $root $(if ($role -eq 'host') { '.local/game' } else { '.local/game-client' })
    foreach ($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $game ('Mods/' + $dll)) -Algorithm SHA256).Hash
        $built = (Get-FileHash -LiteralPath (Join-Path $root ('build/' + [IO.Path]::GetFileName($dll))) -Algorithm SHA256).Hash
        if ($tested -ne $built) { throw ('Tested DLL differs from the current build: ' + $dll) }
        $rows += $role + ' SHA256 ' + $tested + '  ' + $dll
    }
    $rows += @('', ($role + ' report:'))
    $rows += @($report | Where-Object { $_ -match '^(Engine|ModVersion|NetVersion|Level|Ready|Players|SaveReceived|Activity|FrameP95ms|FrameMaxms)=' })
}
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) { throw 'An original single-player save changed.' } }
$rows += @('', ('Original single-player save files checked: ' + $before.Count + '; changed: 0'), '',
    'Automated checks (121 component regressions are not 121 native MWCoop gameplay tests):')
foreach ($test in @('build/test-results.txt','build/msc-tests/test-results.txt','build/mwcoop-network-tests/test-results.txt','build/launcher-tests/test-results.txt','build/installer-tests/test-results.txt')) {
    $summary = @(Get-Content -LiteralPath (Join-Path $root $test) | Where-Object { $_ -match '^ALL .* PASSED$|^PASS launcher GUI:' })
    if ($summary.Count -eq 0) { throw ('Missing test result: ' + $test) }
    $rows += $test + ': ' + ($summary -join ' | ')
}
$rows += @('',
    'Fixture limits: native FSM input/actions; items are spawned natively on both peers. No mouse or store-purchase automation.',
    'BrewTime=20 and BurnTime=30 in the fixture only; sauna is preheated; sausage Grilled/Burnt transitions are invoked directly.',
    'Native fermentation, fuel consumption, coffee and pouring then run in the game. No full grill thermal-curve test.',
    'ES2 tags are read back in the same process; no separate restart/reload, Jokke payment, fish-trap or full-gameplay test.',
    'Steam-personal checks the host local steam FSM and camera effect; natural stress drift is reported, not treated as a remote effect.',
    'The host records after the guest finishes; Ready=False/Players=1 then means the guest already quit.',
    'No two-account Steam/WAN test or 60 FPS guarantee. See ACTIVITIES.md for reproduction and scope.')
$output = Join-Path $root 'docs/ACTIVITIES-RESULTS.txt'
$rows | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output $output
