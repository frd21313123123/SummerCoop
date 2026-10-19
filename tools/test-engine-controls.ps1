param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$InstallRun,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ReloadRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Check([string]$run, [string]$name) {
    $path = Join-Path $root ('.local\' + $run + '-engine\' + $name + '.txt')
    if (!(Test-Path -LiteralPath $path)) { throw ('Missing engine proof: ' + $path) }
    $value = Get-Content -LiteralPath $path -Raw
    if ($value -notmatch '^Pass=True;') { throw ($name + ': ' + $value) }
    Write-Output ('PASS ' + $run + '/' + $name + ': ' + $value.Trim())
}
foreach ($role in @('host','guest')) {
    foreach ($name in @('identity','mounted','input-routing','overlap')) { Check $InstallRun ($role + '-' + $name) }
    foreach ($name in @('reloaded','input-routing','overlap','start-0','start-1')) { Check $ReloadRun ($role + '-' + $name) }
}
Check $InstallRun 'host-saved'
Check $ReloadRun 'guest-horn-positive-0'
Check $ReloadRun 'host-horn-positive-1'
Write-Output 'ALL ENGINE CONTROL CHECKS PASSED'
