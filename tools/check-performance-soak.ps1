param([Parameter(Mandatory=$true)][string]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
$rows = [Collections.Generic.List[object]]::new()
$hashes = [Collections.Generic.List[string]]::new()
function RisesEveryWindow($windows, [string]$property) {
    for ($index = 1; $index -lt $windows.Count; $index++) {
        if ($windows[$index].$property -le $windows[$index - 1].$property + 1) { return $false }
    }
    return $true
}
foreach ($role in @('host','client','client2','client3')) {
    $prefix = Join-Path $local ($Run + '-' + $role + '-report')
    $data = Get-Content -LiteralPath ($prefix + '.json') -Raw | ConvertFrom-Json
    if (!$data.valid -or $data.players -ne 4 -or $data.requestedSeconds -lt 1800 -or $data.seconds -lt 1799) { throw ('Incomplete four-player soak: ' + $prefix) }
    $health = @(Import-Csv -LiteralPath ($prefix + '-health.csv'))
    if ($health.Count -lt 170) { throw ('Health telemetry incomplete: ' + $prefix) }
    $transport = @(0..5 | ForEach-Object { [pscustomobject]@{Samples=0;ReliableSum=0.0;WaitingSum=0.0;ReorderedSum=0.0;WaitingMaximum=0;WaitingLast=0} })
    $stream = [IO.File]::OpenText($prefix + '-frames.csv')
    try {
        $header = $stream.ReadLine().Split(',')
        $elapsedColumn = [Array]::IndexOf($header,'elapsed_ms')
        $reliableColumn = [Array]::IndexOf($header,'reliable_pending')
        $waitingColumn = [Array]::IndexOf($header,'reliable_waiting')
        $reorderedColumn = [Array]::IndexOf($header,'reordered_pending')
        if (@($elapsedColumn,$reliableColumn,$waitingColumn,$reorderedColumn) -contains -1) { throw 'Transport CSV columns missing.' }
        while (!$stream.EndOfStream) {
            $line = $stream.ReadLine().Split(',')
            $elapsed = [double]::Parse($line[$elapsedColumn],[Globalization.CultureInfo]::InvariantCulture)
            $bucket = $transport[[Math]::Min(5,[int][Math]::Floor($elapsed / 300000))]
            $bucket.Samples++; $bucket.ReliableSum += [int]$line[$reliableColumn]
            $waiting = [int]$line[$waitingColumn]
            $bucket.WaitingSum += $waiting; $bucket.ReorderedSum += [int]$line[$reorderedColumn]
            $bucket.WaitingMaximum = [Math]::Max($bucket.WaitingMaximum,$waiting); $bucket.WaitingLast = $waiting
        }
    } finally { $stream.Dispose() }
    $windows = foreach ($window in 0..5) {
        $samples = @($health | Where-Object { [double]$_.elapsed_s -ge $window * 300 -and [double]$_.elapsed_s -lt ($window + 1) * 300 })
        [pscustomobject]@{
            FromSeconds=$window * 300; Samples=$samples.Count
            HeapMinimumBytes=($samples | ForEach-Object { [long]$_.heap_bytes } | Measure-Object -Minimum).Minimum
            IoMaximum=($samples | ForEach-Object { [int]$_.io_pending } | Measure-Object -Maximum).Maximum
            SnapshotMaximum=($samples | ForEach-Object { [int]$_.snapshot_pending } | Measure-Object -Maximum).Maximum
            IoMean=($samples | ForEach-Object { [int]$_.io_pending } | Measure-Object -Average).Average
            SnapshotMean=($samples | ForEach-Object { [int]$_.snapshot_pending } | Measure-Object -Average).Average
            ReliableMean=$transport[$window].ReliableSum / [Math]::Max(1,$transport[$window].Samples)
            WaitingMean=$transport[$window].WaitingSum / [Math]::Max(1,$transport[$window].Samples)
            ReorderedMean=$transport[$window].ReorderedSum / [Math]::Max(1,$transport[$window].Samples)
            WaitingMaximum=$transport[$window].WaitingMaximum; WaitingLast=$transport[$window].WaitingLast
            DiscoveryOldestSeconds=($samples | ForEach-Object { [double]$_.discovery_age_s } | Measure-Object -Maximum).Maximum
        }
    }
    $increasing = $true
    for ($window = 1; $window -lt $windows.Count; $window++) {
        if ($windows[$window].HeapMinimumBytes -le $windows[$window - 1].HeapMinimumBytes + 1MB) { $increasing = $false }
    }
    $integrity = $prefix + '-integrity.tsv'
    $hash = (Get-FileHash -LiteralPath $integrity -Algorithm SHA256).Hash
    $hashes.Add($hash)
    $runtime = Get-Content -LiteralPath (Join-Path $local ($Run + '-' + $role + '\mwcoop\logs\mwcoop.log')) -Raw
    $errors = [regex]::Matches($runtime, '\bERREUR\b|MSC assembly unavailable after retry').Count
    $errors += [regex]::Matches((Get-Content -LiteralPath ($prefix.Replace('-report','') + '-game.log') -Raw), 'SummerCoop background IO:').Count
    $rows.Add([pscustomobject]@{
        Role=$role; Frames=$data.frames; Seconds=$data.seconds; GC=$data.gcCollections
        HeapStartBytes=$data.memoryStartBytes; HeapEndBytes=$data.memoryEndBytes
        HeapMinimaRiseEveryWindow=$increasing; Windows=@($windows)
        WaitingRisesEveryWindow=(RisesEveryWindow $windows 'WaitingMean')
        IoRisesEveryWindow=(RisesEveryWindow $windows 'IoMean')
        SnapshotsRiseEveryWindow=(RisesEveryWindow $windows 'SnapshotMean')
        WaitingPeak=$data.waitingPeak; IoPeak=$data.ioPeak; DroppedLogs=$data.droppedLogs
        IntegrityHash=$hash; IntegrityRows=@(Get-Content -LiteralPath $integrity).Count; SynchronizationErrors=$errors
    })
    if ($errors -gt 0 -or $data.droppedLogs -gt 0) { throw ('Soak errors recorded: ' + $prefix) }
}
$same = @($hashes | Select-Object -Unique).Count -eq 1
$result = [pscustomobject]@{Run=$Run;PartAndBoltStateMatches=$same;Roles=@($rows);Note='Heap minima by five-minute windows are a trend diagnostic, not a leak proof or a whole-world state hash.'}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $local ($Run + '-soak-check.json')) -Encoding UTF8
$rows | Format-Table Role,Frames,Seconds,GC,HeapMinimaRiseEveryWindow,WaitingPeak,IoPeak,IntegrityRows,SynchronizationErrors -AutoSize
if (!$same) { throw 'Part/bolt state differs between replicas; inspect integrity TSV files.' }
if (@($rows | Where-Object HeapMinimaRiseEveryWindow).Count) { throw 'Heap minima increased in every five-minute window; inspect the trend before accepting the soak.' }
if (@($rows | Where-Object { $_.WaitingRisesEveryWindow -or $_.IoRisesEveryWindow -or $_.SnapshotsRiseEveryWindow }).Count) { throw 'Background/transport queue averages increased in every five-minute window; inspect the queue trend.' }
Write-Output 'PASS completed 30-minute four-player soak, equal part/bolt state, no continuously rising heap minima'
