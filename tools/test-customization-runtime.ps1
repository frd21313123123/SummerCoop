param([ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run = ('launcher-looks-' + (Get-Date -Format yyyyMMdd-HHmmss)), [switch]$VerifyOnly, [switch]$ColdCache, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
$configs = @('game', 'game-client') | ForEach-Object { Join-Path $local ($_ + '\MWCoop\mwcoop.ini') }
function VerifyReports($runs) {
    foreach ($job in $runs) {
        if (!(Test-Path -LiteralPath $job.Report)) { throw ('No runtime report: ' + $job.Report) }
        $report = Get-Content -LiteralPath $job.Report -Raw
        # The guest closes eight seconds before the host. The host's final
        # roster therefore contains only itself; guest readiness proves the pair.
        if ($report -notmatch 'Level=GAME' -or $report -match 'ModuleError=|ActionError=') { throw $report }
        if ($job.Role -eq 'client' -and ($report -notmatch 'Ready=True' -or $report -notmatch 'Players=2')) { throw $report }
        if ($report -notmatch 'LauncherPreviews=True,True') { throw ('Missing launcher preview: ' + $job.Report) }
        if ($report -notmatch 'PlayerAppearance=Smoke-host:char_shirt02') { throw ('Changed outfit did not reach ' + $job.Role) }
        if ($job.Role -eq 'host' -and $report -notmatch 'LauncherColor=ABCDEF') { throw 'Changed car color did not reach mod configuration.' }
        Write-Output ('PASS ' + $job.Role + ': previews without F10 and live outfit propagation')
    }
    Write-Output ('Runtime reports: ' + $Run)
}
if ($VerifyOnly) {
    $reports = @('host', 'client') | ForEach-Object { [pscustomobject]@{ Role = $_; Report = Join-Path $local ($Run + '-' + $_ + '-report.txt') } }
    VerifyReports $reports
    return
}
$previous = @{}
$jobs = @()
foreach ($config in $configs) {
    $previous[$config] = if (Test-Path -LiteralPath $config) { [IO.File]::ReadAllBytes($config) } else { $null }
}
try {
    if ($ColdCache) {
        foreach ($config in $configs) {
            $cache = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $config) 'cache'))
            if (!$cache.StartsWith(([IO.Path]::GetFullPath($local) + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Preview cache escaped isolated test copies.' }
            if (Test-Path -LiteralPath $cache) {
                $backupName = 'cache.pre-' + $Run
                if (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $cache) $backupName)) { throw 'Use a fresh cold-cache run name.' }
                Rename-Item -LiteralPath $cache -NewName $backupName
            }
        }
    }
    foreach ($config in $configs) {
        "[Coop]`r`nApparence=char_shirt21`r`nCouleurVoiture=123456`r`nApercuTenues=0`r`n" | Set-Content -LiteralPath $config -Encoding ASCII
    }
    $invoke = @{Run=$Run;Duration=60}
    if ($BuildDirectory) { $invoke.BuildDirectory = $BuildDirectory }
    $jobs = @(& (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') @invoke | ForEach-Object { $_ | ConvertFrom-Json })
    $deadline = (Get-Date).AddSeconds(200)
    $changed = $false
    while ((Get-Date) -lt $deadline) {
        $hostPreview = Join-Path (Split-Path -Parent $configs[0]) 'cache\perso.mesh'
        if (!$changed -and (Test-Path -LiteralPath $hostPreview)) {
            "[Coop]`r`nApparence=char_shirt02`r`nCouleurVoiture=ABCDEF`r`nApercuTenues=0`r`n" | Set-Content -LiteralPath $configs[0] -Encoding ASCII
            $changed = $true
        }
        $finished = @($jobs | Where-Object { Test-Path -LiteralPath $_.Report })
        if ($finished.Count -eq 2) { break }
        Start-Sleep -Seconds 2
    }
    if (!$changed) { throw 'Character preview was not exported without opening F10.' }
    VerifyReports $jobs
} finally {
    if ($jobs) {
        foreach ($job in $jobs) {
            $process = Get-Process -Id $job.Id -ErrorAction SilentlyContinue
            if ($process -and $process.Path -in @((Join-Path $local 'game\mysummercar.exe'), (Join-Path $local 'game-client\mysummercar.exe'))) { Stop-Process -Id $job.Id -Force }
        }
    }
    foreach ($config in $configs) {
        if ($null -ne $previous[$config]) { [IO.File]::WriteAllBytes($config, $previous[$config]) }
        elseif (Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config }
    }
}
