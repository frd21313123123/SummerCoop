param(
    [Parameter(Mandatory=$true)][string[]]$Reports,
    [string]$Output = '',
    [switch]$SeparateMachines
)
$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$rows = foreach ($pattern in $Reports) {
    foreach ($file in Get-ChildItem -Path $pattern -File) {
        $item = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if (!$item.valid -or $item.frames -lt 1 -or $item.overflow) { throw ('Incomplete/invalid measurement: ' + $file.FullName) }
        $values = [Collections.Generic.List[double]]::new()
        $modValues = [Collections.Generic.List[double]]::new()
        [double]$elapsed = 0; [int]$collections = 0; [int]$above33 = 0; [int]$above50 = 0; [int]$above100 = 0
        $stream = [IO.File]::OpenText((Join-Path $file.DirectoryName ($file.BaseName + '-frames.csv')))
        try {
            $header = $stream.ReadLine().Split(',')
            $column = [Array]::IndexOf($header, 'frame_ms')
            $modColumn = [Array]::IndexOf($header, 'mod_ms')
            $gcColumn = [Array]::IndexOf($header, 'gc_collections')
            if ($column -lt 0 -or $modColumn -lt 0 -or $gcColumn -lt 0) { throw 'Frame/mod/GC CSV column missing.' }
            while (!$stream.EndOfStream) {
                $line = $stream.ReadLine().Split(',')
                $value = [double]::Parse($line[$column], $invariant)
                if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -le 0) { throw ('Invalid frame duration: ' + $file.FullName) }
                $values.Add($value); $elapsed += $value
                $modValues.Add([double]::Parse($line[$modColumn], $invariant))
                $collections += [int]$line[$gcColumn]
                if ($value -gt 33) { $above33++ }; if ($value -gt 50) { $above50++ }; if ($value -gt 100) { $above100++ }
            }
        } finally { $stream.Dispose() }
        if ($values.Count -ne $item.frames) { throw ('CSV sample count mismatch: ' + $file.FullName) }
        $sorted = $values.ToArray(); [Array]::Sort($sorted)
        foreach ($metric in @(@('p50Ms',.5),@('p95Ms',.95),@('p99Ms',.99),@('maxMs',1))) {
            $percentile = $sorted[[Math]::Max(0,[int][Math]::Ceiling($sorted.Length * [double]$metric[1]) - 1)]
            if ([Math]::Abs($percentile - $item.($metric[0])) -gt .001) { throw ('CSV/JSON ' + $metric[0] + ' mismatch: ' + $file.FullName) }
        }
        $modSorted = $modValues.ToArray(); [Array]::Sort($modSorted)
        $modP95 = $modSorted[[Math]::Max(0,[int][Math]::Ceiling($modSorted.Length * .95) - 1)]
        if ([Math]::Abs($modP95 - $item.modP95Ms) -gt .001 -or [Math]::Abs($modSorted[-1] - $item.modMaxMs) -gt .001 -or [Math]::Abs($elapsed / 1000 - $item.seconds) -gt .001 -or [Math]::Abs($values.Count * 1000 / $elapsed - $item.averageFps) -gt .001 -or $collections -ne $item.gcCollections -or $above33 -ne $item.above33Ms -or $above50 -ne $item.above50Ms -or $above100 -ne $item.above100Ms) { throw ('CSV/JSON frame/mod/GC totals mismatch: ' + $file.FullName) }
        [pscustomobject]@{
            Report=$file.FullName; Role=$item.role; Players=$item.players; Width=$item.width; Height=$item.height
            Seconds=$item.seconds; Frames=$item.frames; AverageFps=$item.averageFps; P95ms=$item.p95Ms; P99ms=$item.p99Ms; MaxMs=$item.maxMs
            ModP95ms=$item.modP95Ms; ModMaxMs=$item.modMaxMs; Above50ms=$item.above50Ms; GC=$item.gcCollections
            WaitingPeak=$item.waitingPeak; IoPeak=$item.ioPeak; DiscoveryOldestSeconds=$item.discoveryOldestSeconds
            NumericTargetMet=($item.averageFps -ge 90 -and $item.p95Ms -le 11.1 -and $item.p99Ms -le 16.7 -and $item.modP95Ms -le 2 -and $item.modMaxMs -lt 50)
            SeparateMachines=[bool]$SeparateMachines
        }
    }
}
if (!$rows) { throw 'No reports matched.' }
$rows | Format-Table Role,Players,Seconds,AverageFps,P95ms,P99ms,MaxMs,ModP95ms,GC,NumericTargetMet -AutoSize
if ($Output) { @($rows) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Output -Encoding UTF8 }
if (!$SeparateMachines) { Write-Output 'Local concurrent game processes are comparative/stress evidence; they do not certify multiplayer FPS on separate PCs.' }
