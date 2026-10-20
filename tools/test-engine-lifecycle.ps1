param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run, [ValidateRange(2,8)][int]$Players = 3)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root ('.local/' + $Run + '-engine-lifecycle')
$count = 0
foreach ($stage in 0..3) {
    $kinds = if ($stage -lt 2) { @('car-crank','car-release') } else { @('boat-start','boat-outside-stop','boat-drive','boat-gear','boat-stop') }
    foreach ($kind in $kinds) {
        for ($player = 0; $player -lt $Players; $player++) {
            $file = Join-Path $directory ($kind + '-' + $stage + '-' + $player + '.txt')
            if (!(Test-Path -LiteralPath $file)) { throw ('Missing engine proof: ' + $file) }
            $result = Get-Content -LiteralPath $file -Raw
            if ($result -notmatch '^Pass=True;') { throw ($file + ': ' + $result) }
            $count++; Write-Output ('PASS ' + $kind + '/' + $stage + '/' + $player + ': ' + $result.Trim())
        }
    }
}
Write-Output ('ALL ' + $count + ' ENGINE LIFECYCLE CHECKS PASSED')
