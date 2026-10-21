param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run, [ValidateRange(2,8)][int]$Players = 3,
    [ValidateSet('HAYOSIKO','SATSUMA')][string]$Vehicle = 'HAYOSIKO', [switch]$Road)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$channel = Join-Path $root ('.local/' + $Run + '-cabin')
foreach ($stage in @(0,1)) {
    for ($player = 0; $player -lt $Players; $player++) {
        $kinds = if ($Vehicle -eq 'SATSUMA') { @('motion') } else { @('motion','radio-on','radio-off') }
        foreach ($kind in $kinds) {
            $path = Join-Path $channel ($kind + '-' + $stage + '-' + $player + '.txt')
            if (!(Test-Path -LiteralPath $path)) { throw ('Missing cabin proof: ' + $path) }
            $result = Get-Content -LiteralPath $path -Raw
            if ($result -notmatch '^Pass=True;') { throw ($path + ': ' + $result) }
            if ($Vehicle -eq 'SATSUMA' -and $result -notmatch ';Vehicle=SATSUMA;') { throw ('Wrong cabin fixture: ' + $path) }
            if ($Vehicle -eq 'SATSUMA') {
                $distance = [regex]::Match($result, ';Distance=([0-9.]+);')
                $minimum = if ($Road) { 2 } else { 4 }
                if (!$distance.Success -or [double]::Parse($distance.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) -le $minimum) {
                    throw ('Satsuma did not travel far enough: ' + $path)
                }
            }
            if ($Road -and $result -notmatch ';Fixture=native engine and road physics;') { throw ('Road proof required: ' + $path) }
            Write-Output ('PASS ' + $kind + '/' + $stage + '/' + $player + ': ' + $result.Trim())
        }
    }
}
Write-Output 'ALL CABIN CHECKS PASSED'
