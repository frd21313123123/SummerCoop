param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$HostGame = 'game',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ClientGame = 'game-client'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rows = @('SummerCoop bolt position verification', ('Run: ' + $Run))
foreach ($role in @('host','client')) {
    $prefix = Join-Path $root ('.local/' + $Run + '-' + $role)
    $report = @(Get-Content -LiteralPath ($prefix + '-report.txt') -Encoding UTF8)
    # The guest records first and exits; Ready on the host can then be false.
    if ($report -notcontains 'ReadyPlayers=2' -or $report -notcontains 'Level=GAME' -or
        $role -eq 'client' -and $report -notcontains 'SaveReceived=True') { throw ('Game was not ready: ' + $role) }
    $result = @($report | Where-Object { $_ -match '^BoltPositions=PASS;' })
    if ($result.Count -ne 1 -or $result[0] -notmatch ';Bolts=([0-9]+);Hidden=([0-9]+);Suspension=([0-9]+);Stages=([0-9]+);Snapshots=([0-9]+)$') { throw ('Missing native checks: ' + $role) }
    if ([int]$Matches[1] -lt 16 -or [int]$Matches[2] -lt 1 -or [int]$Matches[3] -lt 1 -or [int]$Matches[4] -ne 9 * [int]$Matches[1] -or
        $role -eq 'client' -and [int]$Matches[5] -ne [int]$Matches[4]) { throw ('Insufficient native coverage: ' + $role) }
    if ($report -notcontains 'BoltPositionsButton=PASS' -or !($report -match '^BoltPositionsBulk=PASS;') -or
        $report -match '^(ActionError|ModuleError)=|^BoltPositions.*=FAIL') { throw ('Failed bolt position checks: ' + $role) }
    $log = Get-Content -LiteralPath (Join-Path $prefix 'mwcoop/logs/mwcoop.log') -Raw
    if ($log -match 'ERREUR message Bolt\b|MSC bolt position QA:|MSC bolt position repair:') { throw ('Runtime repair error: ' + $role) }
    if (!(Test-Path -LiteralPath ($prefix + '-bolt-positions.png'))) { throw ('Missing menu screenshot: ' + $role) }
    $game = if ($role -eq 'host') { $HostGame } else { $ClientGame }
    foreach ($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $root ('.local/' + $game + '/Mods/' + $dll)) -Algorithm SHA256).Hash
        $built = (Get-FileHash -LiteralPath (Join-Path $root ('build/' + [IO.Path]::GetFileName($dll))) -Algorithm SHA256).Hash
        if ($tested -ne $built) { throw ('Tested DLL differs from current build: ' + $role + ' ' + $dll) }
        $rows += $role + ' SHA256 ' + $tested + '  ' + $dll
    }
    $rows += @($report | Where-Object { $_ -match '^(ModVersion|NetVersion|ModMvid|BoltPositions)' } | ForEach-Object { $role + ' ' + $_ })
}
$original = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($file in $original) {
    if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) { throw ('Original save changed: ' + $file.Path) }
}
$rows += ('Original save files unchanged: ' + $original.Count)
$output = Join-Path $root 'docs/BOLT-POSITIONS-RESULTS.txt'
$rows | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output $output
