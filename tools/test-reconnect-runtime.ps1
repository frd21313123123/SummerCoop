param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,28}$')][string]$Run,
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$SeedProfile,
    [ValidateRange(2,4)][int]$Players = 4,
    [int]$Port = 28320
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
function StopOwned($job) {
    $process = Get-Process -Id $job.Id -ErrorAction SilentlyContinue
    $expected = Join-Path $job.GameDirectory 'mysummercar.exe'
    if ($process -and $process.Path -eq $expected) { Stop-Process -Id $process.Id -Force; $process.WaitForExit(10000) | Out-Null }
}
function WaitUntil([scriptblock]$condition, [int]$seconds, [string]$failure) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $condition) { return }; Start-Sleep -Seconds 2 }
    throw $failure
}
function CheckReport($job, [int]$players, [int]$bolt) {
    $text = Get-Content -LiteralPath $job.Report -Raw
    if ($text -match '(?m)^(ActionError|ModuleError)=' -or $text -notmatch ('(?m)^ReadyPlayers=' + $players + '\r?$') -or $text -notmatch 'OptimizationCache=PASS' -or $text -notmatch 'RepairCache=PASS ' -or $text -notmatch 'PartOwnership=PASS ') { throw ('Incomplete reconnect report: ' + $job.Report) }
    if ($text -notmatch ('(?m)^Starter=Installed=True;[^\r\n]*;Bolt=' + $bolt + '\r?$')) { throw ('Starter state did not survive reconnect/reload: ' + $job.Report) }
    Write-Output ('PASS reconnect/reload ' + $job.Report)
}
$jobs = @(); $replacement = @(); $reload = @()
try {
    $jobs = @(& (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') -Run $Run -Duration 300 -Players $Players -Actions -Optimization -Port $Port -BuildDirectory $BuildDirectory -SeedProfile $SeedProfile -ExtraClientGames @('game-assembly-host','game-assembly-client') | ForEach-Object { $_ | ConvertFrom-Json })
    $hostJob = $jobs | Where-Object Role -eq 'host'
    $state = Join-Path $hostJob.Profile 'mwcoop\serveur.txt'
    function RosterCount() {
        try {
            $lines = @(Get-Content -LiteralPath $state -ErrorAction Stop | Where-Object { $_.StartsWith("joueur`t") })
            return @($lines | Where-Object { ($_ -split "`t")[4] -eq '1' }).Count
        } catch { return 0 }
    }
    WaitUntil { (RosterCount) -eq $Players } 220 'Expected players did not reach GAME before reconnect'
    $hostLog = Join-Path $hostJob.Profile 'mwcoop\logs\mwcoop.log'
    WaitUntil { (Get-Content -LiteralPath $hostLog -Raw) -match 'MSC bolt published [^\r\n]* = [0-9]+' } 45 'Host bolt action was not published'
    $published = [regex]::Match((Get-Content -LiteralPath $hostLog -Raw), 'MSC bolt published [^\r\n]* = ([0-9]+)')
    $bolt = [int]$published.Groups[1].Value
    $oldClient = $jobs | Where-Object Role -eq 'client'
    StopOwned $oldClient
    # Preserve the transport's 120-second loading-safe timeout. An abruptly
    # stopped guest cannot send a graceful goodbye, so allow the full timeout.
    WaitUntil { (RosterCount) -eq ($Players - 1) } 150 'Host retained a disconnected player after the transport timeout'
    $replacement = @(& (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') -Run ($Run + '-back') -Role Client -Duration 55 -Players $Players -Optimization -Port $Port -BuildDirectory $BuildDirectory | ForEach-Object { $_ | ConvertFrom-Json })
    WaitUntil { (RosterCount) -eq $Players } 180 'Replacement client did not rejoin the existing host'
    WaitUntil { Test-Path -LiteralPath $replacement[0].Report } 100 'Replacement client report missing'
    CheckReport $replacement[0] $Players $bolt
    if ((Get-Content -LiteralPath $replacement[0].Report -Raw) -notmatch 'SaveReceived=True') { throw 'Replacement client did not receive a native save' }
    WaitUntil { Test-Path -LiteralPath $hostJob.Report } 240 'Host report missing after reconnect'
    foreach ($job in $jobs | Where-Object Role -ne 'client') { CheckReport $job $Players $bolt }
    WaitUntil { @($jobs + $replacement | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue }).Count -eq 0 } 30 'Reconnect test processes did not exit'
    $saved = Join-Path $hostJob.Profile 'mwcoop-profiles\host'
    $reload = @(& (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') -Run ($Run + '-reload') -Duration 45 -Players 2 -Optimization -Port ($Port + 1) -BuildDirectory $BuildDirectory -SeedProfile $saved | ForEach-Object { $_ | ConvertFrom-Json })
    WaitUntil { @($reload | Where-Object { !(Test-Path -LiteralPath $_.Report) }).Count -eq 0 } 220 'Reload reports missing'
    foreach ($job in $reload) { CheckReport $job 2 $bolt }
    @($jobs + $replacement + $reload) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $local ($Run + '-verification.json')) -Encoding UTF8
} finally {
    foreach ($job in @($jobs + $replacement + $reload)) { StopOwned $job }
}
