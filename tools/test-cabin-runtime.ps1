param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run, [ValidateRange(2,8)][int]$Players = 3)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$channel = Join-Path $root ('.local/' + $Run + '-cabin')
foreach ($stage in @(0,1)) {
    for ($player = 0; $player -lt $Players; $player++) {
        foreach ($kind in @('motion','radio-on','radio-off')) {
            $path = Join-Path $channel ($kind + '-' + $stage + '-' + $player + '.txt')
            if (!(Test-Path -LiteralPath $path)) { throw ('Missing cabin proof: ' + $path) }
            $result = Get-Content -LiteralPath $path -Raw
            if ($result -notmatch '^Pass=True;') { throw ($path + ': ' + $result) }
            Write-Output ('PASS ' + $kind + '/' + $stage + '/' + $player + ': ' + $result.Trim())
        }
    }
}
Write-Output 'ALL CABIN CHECKS PASSED'
