param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root ('.local/' + $Run + '-cheats')
$kinds = @('loading-money-click','solo-money','early-credit-preserved','ready-money','personal-money',
    'time-6','time-8','time-12','time-16','time-18','time-22','host-on','host-off','guest-on','guest-off','midnight',
    'sober-local','sober-command','drinking-can-recur')
$count = 0
foreach ($role in @('host','guest')) {
    foreach ($kind in $kinds) {
        $file = Join-Path $directory ($role + '-cheat-' + $kind + '.txt')
        if (!(Test-Path -LiteralPath $file)) { throw ('Missing cheat proof: ' + $file) }
        $result = Get-Content -LiteralPath $file -Raw
        if ($result -notmatch ';Pass=True(?:;|\s*$)') { throw ($file + ': ' + $result) }
        $count++; Write-Output ('PASS ' + $role + '/' + $kind + ': ' + $result.Trim())
    }
}
$file = Join-Path $directory 'guest-cheat-sober-button.txt'
if (!(Test-Path -LiteralPath $file) -or (Get-Content -LiteralPath $file -Raw) -notmatch ';Pass=True(?:;|\s*$)') { throw ('Missing successful sober button click: ' + $file) }
$count++; Write-Output 'PASS guest/sober-button'
Write-Output ('ALL ' + $count + ' CHEAT CHECKS PASSED')
