param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root ('.local/' + $Run + '-summerfixes')
$checks = @('passenger-stereo-foot-range')
foreach ($stage in 0..1) {
    foreach ($check in @('roles','reach','volume-and-visual','cd-start','next-track','radio-mode','station','cd-resume','eject','power-off')) {
        $checks += "passenger-stereo-$stage-$check"
    }
}
foreach ($role in @('host','guest')) {
    foreach ($check in $checks) {
        $path = Join-Path $directory ($role + '-' + $check + '.txt')
        if (!(Test-Path -LiteralPath $path)) { throw "Missing passenger stereo proof: $path" }
        $proof = Get-Content -LiteralPath $path -Raw
        if ($proof -notmatch ';Pass=True(?:;|\s*$)') { throw ($path + ': ' + $proof) }
        Write-Output ('PASS ' + $role + '/' + $check)
    }
    $instance = if ($role -eq 'host') { 'host' } else { 'client' }
    $report = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $instance + '-report.txt')) -Raw
    if ($report -notmatch '(?m)^ReadyPlayers=2\r?$' -or $report -match '(?m)^(ActionError|ModuleError)=') { throw "Unclean stereo session: $instance" }
    $log = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $instance + '/mwcoop/logs/mwcoop.log')) -Raw
    if ($log -match 'ERREUR message (?:Stereo|Cd|CarVisual)\b') { throw "Invalid stereo traffic: $instance" }
}
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) { throw ('Original save changed: ' + $file.Path) } }
Write-Output ('ALL ' + ($checks.Count * 2) + ' PASSENGER STEREO CHECKS PASSED; original saves unchanged')
