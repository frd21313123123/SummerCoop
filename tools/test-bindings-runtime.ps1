param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run, [switch]$FleetariInstant)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root ('.local/' + $Run + '-bindings')
$kinds = @('references','fleetari-open','brochure-local','fleetari-order','fleetari-payment','fleetari-complete','fleetari-tuning','catalog-posted','catalog-delivered','fleetari-draft','catalog-draft','shelf-guest-pick','shelf-both-pick','shelf-guest-return','fleetari-closed','fleetari-reopened')
if ($FleetariInstant) { $kinds += @('fleetari-instant','fleetari-empty') }
$count = 0
foreach ($role in @('host','guest')) {
    foreach ($kind in $kinds) {
        $file = Join-Path $directory ($role + '-bindings-' + $kind + '.txt')
        if (!(Test-Path -LiteralPath $file)) { throw ('Missing native binding proof: ' + $file) }
        $result = Get-Content -LiteralPath $file -Raw
        if ($result -notmatch ';Pass=True(?:;|\s*$)') { throw ($file + ': ' + $result) }
        $count++; Write-Output ('PASS ' + $role + '/' + $kind + ': ' + $result.Trim())
    }
}
if ($FleetariInstant) {
    $file = Join-Path $directory 'guest-bindings-fleetari-button.txt'
    if (!(Test-Path -LiteralPath $file) -or (Get-Content -LiteralPath $file -Raw) -notmatch ';Pass=True(?:;|\s*$)') { throw ('Missing successful Fleetari button click: ' + $file) }
    $count++; Write-Output 'PASS guest/fleetari-button'
}
Write-Output ('ALL ' + $count + ' NATIVE BINDING CHECKS PASSED')
