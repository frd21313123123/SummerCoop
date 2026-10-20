param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,26}$')][string]$Run,
    [string]$BuildDirectory,
    [int]$Port = 27993
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'build' }
$jobs = @()
function WaitUntil([scriptblock]$condition, [int]$seconds, [string]$failure) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $condition) { return }; Start-Sleep -Seconds 2 }
    throw $failure
}
function CheckGuest($job, [bool]$cached) {
    $report = Get-Content -LiteralPath $job.Report -Raw
    $log = Get-Content -LiteralPath (Join-Path $job.Profile 'mwcoop/logs/mwcoop.log') -Raw
    if ($report -notmatch '(?m)^ReadyPlayers=2\r?$' -or $report -notmatch '(?m)^Level=GAME\r?$' -or $report -match '(?m)^(ActionError|ModuleError)=' -or $log -match 'ERREUR message (?:Music|Cd)\b') { throw 'Guest did not join cleanly' }
    if ($log -notmatch 'MSC music ready: ([a-f0-9]{64});files=6;downloaded=(\d+)') { throw 'Missing complete music catalogue' }
    $catalogue = $Matches[1]; $downloaded = [long]$Matches[2]
    if (($cached -and $downloaded -ne 0) -or (!$cached -and $downloaded -le 0)) { throw "Unexpected downloaded bytes: $downloaded" }
    if ($log -notmatch 'import fini -- radio 2, CD1 1, CD2 2, CD3 1') { throw 'Incomplete native import' }
    foreach ($group in @('Radio','CD1','CD2','CD3')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root ('.local/game-assembly-host/' + $group)) -Filter 'track*.ogg') {
            $target = Join-Path $job.Profile ('music/catalogues/' + $catalogue + '/' + $group + '/' + $file.Name)
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw 'Late join music hash mismatch' }
        }
    }
    Write-Output ("PASS late join cached=$cached downloaded=$downloaded catalogue=$catalogue")
}
try {
    $hostJob = & (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') -Run $Run -Role Host -Duration 330 -Port $Port -HostGame game-assembly-host -BuildDirectory $BuildDirectory | ConvertFrom-Json
    $jobs += $hostJob
    $state = Join-Path $hostJob.Profile 'mwcoop/serveur.txt'
    WaitUntil { (Test-Path -LiteralPath $state) -and (Get-Content -LiteralPath $state -Raw) -match "`tGAME`t" } 180 'Host did not enter GAME'
    Write-Output 'PASS host is already in GAME before the guest connects'
    $first = & (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') -Run ($Run + '-first') -Role Client -Duration 30 -Port $Port -ClientGame game-assembly-client -BuildDirectory $BuildDirectory | ConvertFrom-Json
    $jobs += $first
    WaitUntil { Test-Path -LiteralPath $first.Report } 180 'First guest report missing'
    CheckGuest $first $false
    WaitUntil { !(Get-Process -Id $first.Id -ErrorAction SilentlyContinue) } 25 'First guest did not exit'
    $second = & (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') -Run ($Run + '-back') -Role Client -Duration 30 -Port $Port -ClientGame game-assembly-client -BuildDirectory $BuildDirectory -SeedMusicCache (Join-Path $first.Profile 'music') | ConvertFrom-Json
    $jobs += $second
    WaitUntil { Test-Path -LiteralPath $second.Report } 180 'Reconnected guest report missing'
    CheckGuest $second $true
    $guestOwn = Join-Path $root '.local/game-assembly-client/Radio/track1.ogg'
    if ((Get-FileHash -LiteralPath $guestOwn -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root '.local/game-assembly-host/Radio/track3.ogg') -Algorithm SHA256).Hash) { throw 'Guest music was overwritten' }
    Write-Output 'PASS late join, verified cache reuse after reconnect and unchanged guest music'
} finally {
    foreach ($job in $jobs) {
        $process = Get-Process -Id $job.Id -ErrorAction SilentlyContinue
        if ($process -and $process.Path -eq (Join-Path $job.GameDirectory 'mysummercar.exe')) { Stop-Process -Id $process.Id -Force; $process.WaitForExit(10000) | Out-Null }
    }
}
