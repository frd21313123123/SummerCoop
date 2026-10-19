param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$HostGame = 'game',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ClientGame = 'game-client'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expected = @('moving-fasteners','host-battery-remove','host-battery-install','guest-battery-remove','guest-battery-install',
    'host-wheel-remove','host-wheel-install','guest-wheel-remove','guest-wheel-install','host-door-open','host-door-close',
    'remounted-guest-door-open','remounted-guest-door-close','starter-battery-identity','starter-battery-install')
$identities = @()
foreach ($role in @('host','client')) {
    $path = Join-Path $root ('.local/' + $Run + '-' + $role + '-report.txt')
    $lines = @(Get-Content -LiteralPath $path)
    if ($lines -notcontains 'Level=GAME' -or $lines -notcontains 'ReadyPlayers=2') { throw ('Game pair did not become ready: ' + $role) }
    if (@($lines | Where-Object { $_ -match '^(ActionError|ModuleError)=' }).Count) { throw ('Runtime error in ' + $path) }
    $log = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '/mwcoop/logs/mwcoop.log')) -Raw
    if ($log -match 'ERREUR (?:message (?:Part|Bolt|CarDoor)\b|crochet\s*:|MSC attachment probe:)|MSC compatibility:') { throw ('Synchronization error in ' + $role + ' log.') }
    $game = if ($role -eq 'host') { $HostGame } else { $ClientGame }
    foreach ($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $root ('.local/' + $game + '/Mods/' + $dll)) -Algorithm SHA256).Hash
        $built = (Get-FileHash -LiteralPath (Join-Path $root ('build/' + [IO.Path]::GetFileName($dll))) -Algorithm SHA256).Hash
        if ($tested -ne $built) { throw ('Tested DLL differs from current build: ' + $role + ' ' + $dll) }
        Write-Output ($role + ' SHA256 ' + $tested + '  ' + $dll)
    }
    foreach ($name in $expected) {
        $rows = @($lines | Where-Object { $_.StartsWith('Attachments=' + $name + ';') })
        if ($rows.Count -ne 1 -or !$rows[0].Contains(';Pass=True;')) { throw ('Missing or failed ' + $role + ' scenario: ' + $name + ': ' + ($rows -join ' ')) }
        Write-Output ($role + ': ' + $rows[0])
    }
    $identities += @($lines | Where-Object { $_.StartsWith('ModMvid=') })
}
if ($identities.Count -ne 2 -or @($identities | Select-Object -Unique).Count -ne 1) { throw 'Missing or mismatched mod build identities.' }
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($entry in $before) {
    if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) { throw ('Personal save changed: ' + $entry.Path) }
}
Write-Output ('PASS: 30 attachment checks; matching mod binaries; personal save hashes unchanged. ' + $identities[0])
