param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root ('.local/' + $Run + '-summerfixes')
$checks = @('cd-next-track','cd-switch-radio','cd-switch-back','radio-playback-drift','independent-radio-streams','music-power-off',
    'stereo-cd-power-and-volume','stereo-guest-volume-down-stays-on','stereo-native-cd-start','stereo-native-next-track',
    'stereo-concurrent-turns','stereo-full-sync-recovery','stereo-power-off-buttons','cd-snapshot-eject-recovery','stereo-volume-reaches-speakers')
foreach ($disc in 1..3) { foreach ($operation in @('remove-case','return-case','playback','eject')) { $checks += "cd-$disc-$operation" } }
foreach ($car in @('HAYOSIKO','GIFU','FERNDALE','KEKMET','SATSUMA')) { $checks += "stereo-radio-$car" }
$count = 0
foreach ($role in @('host','guest')) {
    foreach ($check in $checks) {
        $path = Join-Path $directory ($role + '-' + $check + '.txt')
        if (!(Test-Path -LiteralPath $path)) { throw "Missing stereo proof: $path" }
        $proof = Get-Content -LiteralPath $path -Raw
        if ($proof -notmatch ';Pass=True(?:;|\s*$)') { throw ($path + ': ' + $proof) }
        Write-Output ('PASS ' + $role + '/' + $check); $count++
    }
    $instance = if ($role -eq 'host') { 'host' } else { 'client' }
    $report = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $instance + '-report.txt')) -Raw
    if ($report -notmatch '(?m)^ReadyPlayers=2\r?$' -or $report -match '(?m)^(ActionError|ModuleError)=') { throw "Unclean stereo session: $instance" }
    $log = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $instance + '/mwcoop/logs/mwcoop.log')) -Raw
    if ($log -match 'ERREUR message (?:Stereo|Cd|Resync)\b') { throw "Invalid stereo/recovery traffic: $instance" }
}
$button = Get-Content -LiteralPath (Join-Path $directory 'guest-stereo-sync-button.txt') -Raw
if ($button -notmatch ';Pass=True(?:;|\s*$)') { throw 'The actual F10 synchronization button did not work.' }
Write-Output ('ALL ' + ($count + 1) + ' STEREO AND SYNCHRONIZATION CHECKS PASSED')
