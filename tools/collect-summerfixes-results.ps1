param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$HostGame = 'game',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ClientGame = 'game-client',
    [string]$BuildDirectory,
    [switch]$MusicOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'build' }
$expected = @('radio-playback-drift','independent-radio-streams','music-power-off','cd-next-track','cd-switch-radio','cd-switch-back')
foreach ($disc in 1..3) { foreach ($action in @('remove-case','return-case','playback','eject')) { $expected += "cd-$disc-$action" } }
if (!$MusicOnly) {
    $expected += @('music-import-gaps','music-cache-ready','cure-sting-command','cure-sting-effect','cure-flash-command','cure-flash-effect','flash-can-recur','sting-can-recur','house-wasp-disabled','house-wasp-restored','cheats-disabled-restores-wasp')
    foreach ($disc in 1..3) { $expected += "cd-identity-$disc" }
    foreach ($rocket in 1..2) { foreach ($action in @('fuse','aim','flight','spent')) { $expected += "rocket-$rocket-$action" } }
    foreach ($actor in @('host','guest')) { foreach ($panel in @('Door_Left','Door_Right','Bootlid','Hood')) { $expected += "$actor-install-$panel"; $expected += "$actor-bolts-open-close-$panel" } }
}
$results = [Collections.Generic.List[string]]::new()
$identities = @()
foreach ($role in @('host','client')) {
    $profile = Join-Path $root ('.local/' + $Run + '-' + $role)
    $report = Get-Content -LiteralPath ($profile + '-report.txt') -Raw
    if ($report -notmatch '(?m)^Level=GAME\r?$' -or $report -notmatch '(?m)^ReadyPlayers=2\r?$' -or $report -match '(?m)^(ActionError|ModuleError)=') { throw "Game pair did not finish cleanly: $role" }
    $identities += [regex]::Match($report, '(?m)^ModMvid=([^\r\n]+)').Groups[1].Value
    $log = Get-Content -LiteralPath (Join-Path $profile 'mwcoop/logs/mwcoop.log') -Raw
    if ($log -match 'ERREUR (?:message (?:Cd|Music|Part|Bolt|Prop|Home)\b|crochet\s*:)|Regression=probe-error;|MSC compatibility:') { throw "Runtime synchronization error: $role" }
    $label = if ($role -eq 'client') { 'guest' } else { 'host' }
    foreach ($scenario in $expected) {
        $line = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-summerfixes/' + $label + '-' + $scenario + '.txt')) -Raw
        if (!$line.Contains(';Pass=True;')) { throw "$role failed $scenario`: $line" }
        $results.Add($role + ': ' + $line.Trim())
    }
    $game = if ($role -eq 'host') { $HostGame } else { $ClientGame }
    foreach ($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $root ('.local/' + $game + '/Mods/' + $dll)) -Algorithm SHA256).Hash
        $built = (Get-FileHash -LiteralPath (Join-Path $BuildDirectory ([IO.Path]::GetFileName($dll))) -Algorithm SHA256).Hash
        if ($tested -ne $built) { throw "Tested DLL differs from build: $role $dll" }
    }
}
if (@($identities | Select-Object -Unique).Count -ne 1 -or !$identities[0]) { throw 'Mod identities differ between peers' }
$guestCache = Join-Path $root ('.local/' + $Run + '-client/music/catalogues')
$catalogues = @(Get-ChildItem -LiteralPath $guestCache -Directory)
if ($catalogues.Count -ne 1) { throw 'Expected one verified host catalogue' }
foreach ($group in @('Radio','CD1','CD2','CD3')) {
    $source = Join-Path $root ('.local/' + $HostGame + '/' + $group)
    foreach ($file in Get-ChildItem -LiteralPath $source -Filter 'track*.ogg' -File) {
        $copy = Join-Path $catalogues[0].FullName ($group + '/' + $file.Name)
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) { throw "Cached music differs: $group/$($file.Name)" }
    }
}
$guestOwn = Join-Path $root ('.local/' + $ClientGame + '/Radio/track1.ogg')
$different = Join-Path $root ('.local/' + $HostGame + '/Radio/track3.ogg')
if ((Get-FileHash -LiteralPath $guestOwn -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $different -Algorithm SHA256).Hash) { throw 'Guest fixture music was changed' }
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($entry in $before) { if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) { throw ('Personal save changed: ' + $entry.Path) } }
$directory = Join-Path $root ('build/summerfixes-tests/' + $Run); New-Item -ItemType Directory -Path $directory -Force | Out-Null
$results | Set-Content -LiteralPath (Join-Path $directory 'test-results.txt') -Encoding UTF8
$results | Write-Output
Write-Output ("PASS: " + $results.Count + ' native checks, matching mod binaries, verified music cache and unchanged guest music/personal saves. MVID=' + $identities[0])
