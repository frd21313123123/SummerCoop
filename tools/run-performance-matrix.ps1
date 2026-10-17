param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,26}$')][string]$Run,
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$SeedProfile,
    [ValidateSet('Candidate','Vanilla','Soak')][string[]]$Phases = @('Candidate','Vanilla','Soak'),
    [ValidateRange(1,3)][int]$Repeats = 3,
    [int]$Port = 28400,
    [string[]]$CompetingProcessNames = @('cs2')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
$BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
$SeedProfile = (Resolve-Path -LiteralPath $SeedProfile).Path
$evidence = [Collections.Generic.List[object]]::new()
foreach ($phase in $Phases) {
    $count = if ($phase -eq 'Soak') { 1 } else { $Repeats }
    for ($trial = 1; $trial -le $count; $trial++) {
        if (@(Get-Process mysummercar -ErrorAction SilentlyContinue).Count) { throw 'Close other game processes before a performance trial.' }
        foreach ($processName in $CompetingProcessNames) {
            if (@(Get-Process -Name $processName -ErrorAction SilentlyContinue).Count) { throw ('Competing game is running: ' + $processName + '. Close it before performance measurements; this script never stops it.') }
        }
        $name = $Run + '-' + $phase.ToLowerInvariant() + '-' + $trial
        if ($phase -eq 'Vanilla') {
            & (Join-Path $PSScriptRoot 'run-msc-baseline.ps1') -Run $name -Duration 600 -Warmup 60 -SeedProfile $SeedProfile
            $reports = @(Join-Path $local ($name + '-report.json'))
        } else {
            $players = if ($phase -eq 'Soak') { 4 } else { 2 }
            $duration = if ($phase -eq 'Soak') { 1800 } else { 600 }
            $width = if ($phase -eq 'Soak') { 640 } else { 2560 }
            $height = if ($phase -eq 'Soak') { 480 } else { 1440 }
            $jobs = @(& (Join-Path $PSScriptRoot 'run-mwcoop-pair.ps1') -Run $name -Performance -Duration $duration -Warmup 60 -Players $players -Width $width -Height $height -Port $Port -BuildDirectory $BuildDirectory -SeedProfile $SeedProfile -ExtraClientGames @('game-assembly-host','game-assembly-client') | ForEach-Object { $_ | ConvertFrom-Json })
            $Port++
            $jobs | ConvertTo-Json -Compress
            $jobs | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $local ($name + '-launches.json')) -Encoding UTF8
            $deadline = (Get-Date).AddSeconds($duration + 800)
            do {
                $running = @($jobs | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue })
                if ($running.Count -eq 0) { break }
                if ((Get-Date) -ge $deadline) { throw ('Performance run exceeded supervisor deadline: ' + $name) }
                Start-Sleep -Seconds 2
            } while ($true)
            foreach ($job in $jobs) {
                foreach ($dll in @('SummerCoop.dll','SummerCoop.Core.dll')) {
                    $installed = if ($dll -eq 'SummerCoop.Core.dll') { Join-Path $job.GameDirectory ('Mods\References\' + $dll) } else { Join-Path $job.GameDirectory ('Mods\' + $dll) }
                    if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $BuildDirectory $dll) -Algorithm SHA256).Hash) { throw ('Measured DLL changed: ' + $installed) }
                }
                $runtime = Get-Content -LiteralPath (Join-Path $job.Profile 'mwcoop\logs\mwcoop.log') -Raw
                if ($runtime -match '\bERREUR\b|MSC assembly unavailable after retry') { throw ('Runtime errors invalidate performance evidence: ' + $job.Profile) }
                if ((Get-Content -LiteralPath $job.Log -Raw) -match 'SummerCoop background IO:') { throw ('Background IO failed during the trial: ' + $job.Log) }
            }
            $reports = @($jobs | ForEach-Object { [IO.Path]::ChangeExtension($_.Report, '.json') })
        }
        foreach ($report in $reports) {
            if (!(Test-Path -LiteralPath $report)) { throw ('Missing performance report: ' + $report) }
            $data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            if (!$data.valid -or $data.overflow) { throw ('Invalid measurement: ' + $report + ' ' + $data.failure) }
            $expectedSeconds = if ($phase -eq 'Soak') { 1800 } else { 600 }
            $expectedPlayers = if ($phase -eq 'Vanilla') { 1 } elseif ($phase -eq 'Soak') { 4 } else { 2 }
            $expectedWidth = if ($phase -eq 'Soak') { 640 } else { 2560 }
            $expectedHeight = if ($phase -eq 'Soak') { 480 } else { 1440 }
            if ($data.seconds -lt $expectedSeconds -or $data.requestedSeconds -ne $expectedSeconds -or $data.warmupSeconds -ne 60 -or $data.players -ne $expectedPlayers -or $data.width -ne $expectedWidth -or $data.height -ne $expectedHeight) { throw ('Measurement conditions differ from the matrix: ' + $report) }
            if ($phase -ne 'Vanilla' -and $data.netVersion -ne 1046) { throw ('Unexpected network protocol: ' + $report) }
            $evidence.Add([pscustomobject]@{Phase=$phase;Trial=$trial;Report=$report;AverageFps=$data.averageFps;P95ms=$data.p95Ms;P99ms=$data.p99Ms;ModP95ms=$data.modP95Ms})
        }
        $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $local ($Run + '-matrix.json')) -Encoding UTF8
        Write-Output ('COMPLETE ' + $phase + ' trial ' + $trial)
    }
}
$reports = @($evidence | ForEach-Object Report)
& (Join-Path $PSScriptRoot 'compare-performance.ps1') -Reports $reports -Output (Join-Path $local ($Run + '-summary.json'))
