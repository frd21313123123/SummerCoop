param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$channel = Join-Path $root ('.local/' + $Run + '-bindings')
foreach ($role in @('host','guest')) {
    foreach ($kind in @('catalog-posted','catalog-delivered','parcels-free','parcels-empty')) {
        $path = Join-Path $channel ($role + '-bindings-' + $kind + '.txt')
        if (!(Test-Path -LiteralPath $path) -or (Get-Content -LiteralPath $path -Raw) -notmatch ';Pass=True(?:;|\s*$)') { throw ('Failed parcel check: ' + $path) }
        Write-Output ('PASS ' + $role + '/' + $kind)
    }
}
$path = Join-Path $channel 'guest-bindings-parcels-button.txt'
if (!(Test-Path -LiteralPath $path) -or (Get-Content -LiteralPath $path -Raw) -notmatch ';Pass=True(?:;|\s*$)') { throw 'Parcel button was not clicked successfully.' }
$hostPackages = @(Get-Content -LiteralPath (Join-Path $channel 'host-packages.txt'))
$guestPackages = @(Get-Content -LiteralPath (Join-Path $channel 'guest-packages.txt'))
if ($hostPackages.Count -ne 1 -or $guestPackages.Count -ne 1 -or $hostPackages[0] -ne $guestPackages[0]) { throw 'Expected exactly one identical native parcel on each peer.' }
Write-Output 'ALL PARCEL CHECKS PASSED'
