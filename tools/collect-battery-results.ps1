param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run,
    [ValidateSet('Recovery','Unwired','Reload')][string]$Mode = 'Recovery',
    [string]$SaveHashManifest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expected = @('save-recovered')
if ($Mode -ne 'Unwired') { $expected += 'tool-input' }
if ($Mode -eq 'Recovery') {
    $expected += @('connected','host-tightened','guest-loosened','removed','reinstalled','guest-retightened',
        'joint-loss','after-joint-loss','host-retightened')
}
$identities = @()
$checks = 0
foreach ($role in @('host','client')) {
    $path = Join-Path $root ('.local/' + $Run + '-' + $role + '-report.txt')
    $lines = @(Get-Content -LiteralPath $path)
    if ($lines -notcontains 'Level=GAME' -or $lines -notcontains 'ReadyPlayers=2') { throw ('Game pair did not become ready: ' + $role) }
    if (@($lines | Where-Object { $_ -match '^(ActionError|ModuleError)=' }).Count) { throw ('Runtime error in ' + $path) }
    $log = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '/mwcoop/logs/mwcoop.log')) -Raw
    if ($log -match 'ERREUR (?:message (?:Part|Bolt)\b|crochet\s*:|MSC battery probe:)|MSC compatibility:') { throw ('Synchronization error in ' + $role + ' log.') }
    $game = if ($role -eq 'host') { 'game' } else { 'game-client' }
    foreach ($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $root ('.local/' + $game + '/Mods/' + $dll)) -Algorithm SHA256).Hash
        $built = (Get-FileHash -LiteralPath (Join-Path $root ('build/' + [IO.Path]::GetFileName($dll))) -Algorithm SHA256).Hash
        if ($tested -ne $built) { throw ('Tested DLL differs from current build: ' + $role + ' ' + $dll) }
        Write-Output ($role + ' SHA256 ' + $tested + '  ' + $dll)
    }
    $names = @($expected)
    if ($Mode -eq 'Recovery' -and $role -eq 'host') { $names += 'saved' }
    foreach ($name in $names) { foreach ($side in @('plus','minus')) {
        $scenario = $name + '-' + $side
        $rows = @($lines | Where-Object { $_.StartsWith('Battery=' + $scenario + ';') })
        if ($rows.Count -ne 1 -or !$rows[0].Contains(';Pass=True;')) { throw ('Missing or failed ' + $role + ' scenario: ' + $scenario + ': ' + ($rows -join ' ')) }
        $checks++
        Write-Output ($role + ': ' + $rows[0])
    } }
    $identities += @($lines | Where-Object { $_.StartsWith('ModMvid=') })
}
if ($identities.Count -ne 2 -or @($identities | Select-Object -Unique).Count -ne 1) { throw 'Missing or mismatched mod build identities.' }
$manifests = @((Join-Path $root ('.local/' + $Run + '-original-before.json')))
if ($SaveHashManifest) { $manifests += $SaveHashManifest }
foreach ($manifest in $manifests) { foreach ($entry in @(Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json)) {
    if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) { throw ('Original save changed: ' + $entry.Path) }
} }
Write-Output ('PASS: ' + $checks + ' battery checks; matching mod binaries; original save hashes unchanged. ' + $identities[0])
