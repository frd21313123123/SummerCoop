param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$model = [IO.File]::ReadAllText((Join-Path $root 'src/Core/Model.cs'))
if ($model -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw 'Missing mod version.' }
$version = $Matches[1]
$session = [IO.File]::ReadAllText((Join-Path $root 'vendor/MWCoop/src/MWCoop/Net/Session.cs'))
if ($session -notmatch 'NetVersion\s*=\s*([0-9]+)') { throw 'Missing network version.' }
$networkVersion = $Matches[1]
$rows = @(('SummerCoop ' + $version + ' - native moving surface verification'), ('Recorded: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')), ('Run: ' + $Run), '')
foreach ($role in @('host','client')) {
    $report = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '-report.txt')) -Encoding UTF8)
    foreach ($required in @(('ModVersion=' + $version), ('NetVersion=' + $networkVersion), 'Level=GAME')) {
        if ($report -notcontains $required) { throw ('Wrong native build or incomplete connection: ' + $role + ' ' + $required) }
    }
    if ($role -eq 'client' -and ($report -notcontains 'SaveReceived=True' -or $report -notcontains 'Ready=True' -or $report -notcontains 'Players=2')) { throw 'Guest did not complete the native two-player connection/save transfer.' }
    $results = @($report | Where-Object { $_ -match '^Platform=' })
    if ($results.Count -ne 6) { throw ('Incomplete platform checks: ' + $role) }
    foreach ($target in @('BOAT','HAYOSIKO','FLATBED')) {
        foreach ($driver in @('host','guest')) {
            $expectedRole = if (($role -eq 'host') -eq ($driver -eq 'host')) { 'driver' } else { 'rider' }
            $check = @($results | Where-Object { $_ -match ('^Platform=' + $target + ';Driver=' + $driver + ';Role=' + $expectedRole + ';Pass=True;') })
            if ($check.Count -ne 1) { throw ('Missing or failed native scenario: ' + $role + ' ' + $target + ' ' + $driver) }
        }
    }
    if ($report -match '^(ActionError|ModuleError)=') { throw ('Native probe/module error: ' + $role) }
    $log = [IO.File]::ReadAllText((Join-Path $root ('.local/' + $Run + '-' + $role + '/mwcoop/logs/mwcoop.log')))
    if ($log -match 'ERREUR message (?:PlayerState|Vehicle)\b|MSC platform probe:|MSC compatibility: supports MSC') { throw ('Native platform or message error: ' + $role) }
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
    $rows += @($report | Where-Object { $_ -match '^(Engine|ModVersion|NetVersion|Level|Ready|Players|SaveReceived|Platform|FrameP95ms|FrameMaxms)=' })
}
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) { throw 'An original single-player save changed.' } }
$rows += @('', ('Original single-player save files checked: ' + $before.Count + '; changed: 0'), '',
    'Automated checks (121 component regressions are not 121 native MWCoop gameplay tests):')
foreach ($test in @('build/test-results.txt','build/msc-tests/test-results.txt','build/mwcoop-network-tests/test-results.txt','build/launcher-tests/test-results.txt')) {
    $summary = @(Get-Content -LiteralPath (Join-Path $root $test) | Where-Object { $_ -match '^ALL .* PASSED$|^PASS launcher GUI:' })
    if ($summary.Count -eq 0) { throw ('Missing test result: ' + $test) }
    $rows += $test + ': ' + ($summary -join ' | ')
}
$rows += @('',
    'Fixture limits: native CharacterMotor and colliders; surface pose, speed (4 m/s) and input are controlled.',
    'BOAT/HAYOSIKO use native driver entry; FLATBED uses physical motion and the normal owned-push network path.',
    'No trailer hitch test. Leaving support is checked by an out-of-vehicle warp, not walking over every edge.',
    'Run-over immunity is inspected in code; the fixture speed stays below the run-over threshold.',
    'The host records eight seconds after the guest finishes; Ready=False/Players=1 then means the guest already quit.',
    'No complete playthrough, every modded surface, two-account Steam/WAN test or 60 FPS guarantee.',
    'See MOVING-SURFACES.md for reproduction and scope.')
$output = Join-Path $root 'docs/MOVING-SURFACES-RESULTS.txt'
$rows | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output $output
