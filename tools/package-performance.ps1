param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [ValidatePattern('^[a-zA-Z0-9_-]*$')][string]$PerformanceRun = '',
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$FunctionalRun,
    [string[]]$AdditionalFunctionalRuns = @(),
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ReconnectRun = 'opt-v20-reconnect',
    [switch]$PartialEvidence,
    [ValidatePattern('^[a-zA-Z0-9._-]+$')][string]$Name = 'SummerCoop-0.4.2-performance-mwcoop-20261017-v22'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
$BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
$destination = Join-Path $root ('dist\' + $Name)
$archive = $destination + '.zip'
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $archive)) { throw 'Use a fresh package name; existing artifacts are preserved.' }

# Packaging never builds a DLL: it must contain the exact measured candidate.
$sources = @(Get-Content -LiteralPath (Join-Path $BuildDirectory 'sources.sha256.json') -Raw | ConvertFrom-Json)
if (!$sources.Count) { throw 'The frozen source manifest is empty.' }
foreach ($source in $sources) {
    if ((Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash -ne $source.Hash) { throw ('Source changed since the tested build: ' + $source.Path) }
}
$dlls = @(Get-Content -LiteralPath (Join-Path $BuildDirectory 'hashes.json') -Raw | ConvertFrom-Json)
if ($dlls.Count -ne 2) { throw 'Expected both frozen DLLs.' }
foreach ($dll in $dlls) {
    if ((Get-FileHash -LiteralPath $dll.Path -Algorithm SHA256).Hash -ne $dll.Hash) { throw ('Frozen DLL changed: ' + $dll.Path) }
}
$functional = @(Get-Content -LiteralPath (Join-Path $local ($FunctionalRun + '-verification.json')) -Raw | ConvertFrom-Json)
$primaryReports = @($functional | ForEach-Object Report)
if (!$primaryReports.Count) { throw 'Native checks for the packaged candidate are missing.' }
foreach ($run in $AdditionalFunctionalRuns) {
    if ($run -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid functional evidence name' }
    $functional += @(Get-Content -LiteralPath (Join-Path $local ($run + '-verification.json')) -Raw | ConvertFrom-Json)
}
if (!$functional.Count -or @($functional | Where-Object { !$_.Passed }).Count) { throw 'Functional evidence is incomplete.' }
$matrix = @()
if (!$PartialEvidence) {
    if (!$PerformanceRun) { throw 'Provide a completed performance matrix, or explicitly package partial evidence.' }
    $matrix = @(Get-Content -LiteralPath (Join-Path $local ($PerformanceRun + '-matrix.json')) -Raw | ConvertFrom-Json)
    if (@($matrix | Where-Object Phase -eq 'Candidate').Count -ne 6 -or @($matrix | Where-Object Phase -eq 'Vanilla').Count -ne 3 -or @($matrix | Where-Object Phase -eq 'Soak').Count -ne 4) { throw 'Expected three pair trials, three vanilla trials and four soak reports.' }
    $soak = Get-Content -LiteralPath (Join-Path $local ($PerformanceRun + '-soak-1-soak-check.json')) -Raw | ConvertFrom-Json
    if (!$soak.PartAndBoltStateMatches -or @($soak.Roles).Count -ne 4) { throw 'Soak integrity evidence is incomplete.' }
    if (@($soak.Roles | Where-Object { $_.SynchronizationErrors -or $_.DroppedLogs -or $_.HeapMinimaRiseEveryWindow -or $_.WaitingRisesEveryWindow -or $_.IoRisesEveryWindow -or $_.SnapshotsRiseEveryWindow }).Count) { throw 'Soak guardrails failed; use partial evidence and disclose the limitation.' }
}
foreach ($row in $matrix) {
    $report = Get-Content -LiteralPath $row.Report -Raw | ConvertFrom-Json
    if (!$report.valid -or $report.overflow) { throw ('Invalid measurement: ' + $row.Report) }
}

$mods = Join-Path $destination 'Mods'
$references = Join-Path $mods 'References'
$docs = Join-Path $destination 'docs'
$evidence = Join-Path $destination 'evidence'
New-Item -ItemType Directory -Path $references,$docs,$evidence | Out-Null
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'SummerCoop.dll') -Destination $mods
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'SummerCoop.Core.dll') -Destination $references
foreach ($file in @('PERFORMANCE-MWCOOP.md','PERFORMANCE-MWCOOP-RESULTS.md')) {
    Copy-Item -LiteralPath (Join-Path $root ('docs\' + $file)) -Destination $docs
}
foreach ($file in @('LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination }
# Upstream has no explicit LICENSE. Retain its credits and provenance verbatim.
$upstreamDocs = Join-Path $destination 'vendor\MWCoop'
New-Item -ItemType Directory -Path $upstreamDocs | Out-Null
foreach ($file in @('README.md','UPSTREAM.md')) { Copy-Item -LiteralPath (Join-Path $root ('vendor\MWCoop\' + $file)) -Destination $upstreamDocs }

$reports = @($matrix | ForEach-Object Report)
$reports += @(Get-ChildItem -Path (Join-Path $local 'perf-baseline-b-*-report.json') -File | ForEach-Object FullName)
$reports += @(Get-ChildItem -Path (Join-Path $local 'perf-v14-candidate-1-*-report.json') -File | ForEach-Object FullName)
$reports += @(Get-ChildItem -Path (Join-Path $local 'perf-v16-candidate-1-*-report.json') -File | ForEach-Object FullName)
$reports += @(Get-ChildItem -Path (Join-Path $local 'perf-mount-diag-*-report.json') -File | ForEach-Object FullName)
if (@($reports | Where-Object { (Split-Path -Leaf $_) -like 'perf-baseline-b-*' }).Count -ne 6) { throw 'Baseline evidence is incomplete.' }
foreach ($path in $reports) {
    $prefix = Join-Path (Split-Path -Parent $path) ([IO.Path]::GetFileNameWithoutExtension($path))
    foreach ($suffix in @('.json','-frames.csv','-health.csv','-integrity.tsv')) {
        $file = $prefix + $suffix
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $evidence }
        elseif ($suffix -eq '.json' -or $suffix -eq '-frames.csv') { throw ('Missing primary evidence: ' + $file) }
    }
}
foreach ($row in $functional) { Copy-Item -LiteralPath $row.Report -Destination $evidence }
foreach ($run in $AdditionalFunctionalRuns) { Copy-Item -LiteralPath (Join-Path $local ($run + '-verification.json')) -Destination $evidence }
foreach ($pattern in @(
    ($FunctionalRun + '-verification.json'),($ReconnectRun + '*report.txt'),($ReconnectRun + '*verification.json'),
    'performance-baseline-summary.json','performance-collected-summary.json','performance-gc-association.json','performance-ownership-regression.txt','performance-save-startup-regression.txt','performance-failed-functional-checks.txt','performance-v22-build-tests.log','performance-v22-source-comparison.json','performance-v22-integrity-check.json','performance-v20-source-comparison.json','performance-v17-bulk-network-before.log',
    'perf-v14-summary.json','perf-v16-summary.json','perf-mount-diag-*-report.json',
    'preview-final20-cold-*-report.txt','opt-v9-final-activities-*-report.txt','opt-v12-final-vehicles-*-report.txt',
    'opt-final17-smoke-*-report.txt','opt-v14-reconnect*verification.json','opt-v16-asm-control-*-report.txt','opt-final18-workshop-*-report.txt','opt-v18-workshop-retry-workshop-*-report.txt','opt-v18-rest-regression-*-report.txt','opt-v20-garage-*-report.txt','opt-v20-platforms-*-report.txt','opt-final21-garage-*-report.txt',($FunctionalRun + '-*-report.txt'),($FunctionalRun + '-platforms-*-runtime.log')
)) {
    Get-ChildItem -Path (Join-Path $local $pattern) -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $evidence -Force }
}
if ($PerformanceRun) { Get-ChildItem -Path (Join-Path $local ($PerformanceRun + '-*.json')) -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $evidence -Force } }
$portableSources = foreach ($source in $sources) {
    [pscustomobject]@{Path=$source.Path.Substring($root.Length + 1).Replace('\','/');Sha256=$source.Hash}
}
$portableSources | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $destination 'sources.sha256.json') -Encoding UTF8
$targetModule = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $BuildDirectory 'SummerCoop.dll'))).ManifestModule.ModuleVersionId.ToString()
$nativeChecks = @($functional | ForEach-Object {
    $match = [regex]::Match((Get-Content -LiteralPath $_.Report -Raw),'(?m)^ModMvid=([^\r\n]+)')
    if (!$match.Success) { throw ('Native report has no module identity: ' + $_.Report) }
    if ($_.Report -in $primaryReports -and $match.Groups[1].Value -ne $targetModule) { throw ('Primary check belongs to another DLL: ' + $_.Report) }
    [pscustomobject]@{Case=$_.Case;Role=$_.Role;Players=$_.Players;Report=(Split-Path -Leaf $_.Report);ModuleId=$match.Groups[1].Value;MatchesPackagedModule=($match.Groups[1].Value -eq $targetModule)}
})
if (!$PartialEvidence -and @($nativeChecks | Where-Object { !$_.MatchesPackagedModule }).Count) { throw 'Full acceptance requires native reports from the packaged DLL.' }
$manifest = [pscustomobject]@{
    ModVersion='0.4.2';NetVersion=1046;Candidate=(Split-Path -Leaf $BuildDirectory)
    PerformanceRun=$PerformanceRun;FunctionalRun=$FunctionalRun;AdditionalFunctionalRuns=$AdditionalFunctionalRuns;VerifiedCases=@($functional | ForEach-Object Case | Select-Object -Unique);PartialEvidence=[bool]$PartialEvidence;PackagedAtUtc=[DateTime]::UtcNow.ToString('o')
    Dlls=@($dlls | ForEach-Object { [pscustomobject]@{File=(Split-Path -Leaf $_.Path);Sha256=$_.Hash} })
    NativeChecks=$nativeChecks
    Acceptance='Local comparative evidence; 90+ FPS with remote peers is not certified.'
    FunctionalAcceptance=$(if ($PartialEvidence) { 'Partial; failed and untested scenarios are disclosed in docs/PERFORMANCE-MWCOOP-RESULTS.md and evidence/performance-failed-functional-checks.txt.' } else { 'Automated reports included; manual multiplayer acceptance is separate.' })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding UTF8
@'
# SummerCoop 0.4.2 — оптимизация MWCoop / MSC

Протокол 1046, обе DLL из проверенного кандидата; хеши — manifest.json.
Результаты, условия и оставшиеся ограничения — docs/PERFORMANCE-MWCOOP-RESULTS.md.
Первичные кадры, JSON и отчёты сценариев — evidence/.
Степень завершения проверки указана в manifest.json (PartialEvidence) и отчёте.

Закройте игру. Сохраните копии прежних DLL. Скопируйте каталог Mods в папку
My Summer Car, объединив каталоги с установленным MSCLoader. На всех участниках
должны стоять обе одинаковые DLL. Формат сохранения не менялся.

Если запускаете через нативный SummerCoop Launcher, оставьте этот же каталог
Mods рядом с его EXE: лаунчер использует его для замены встроенных DLL.
Проверьте, что рядом с EXE нет старых SummerCoop.dll и SummerCoop.Core.dll,
которые имеют более высокий приоритет. В этот архив лаунчер не включён.

Целевые 90+ FPS требуют отдельной приёмки с одним игровым процессом на этом
ПК и удалёнными участниками. Локальные тесты не заменяют такую приёмку.
'@ | Set-Content -LiteralPath (Join-Path $destination 'README.md') -Encoding UTF8

$checksums = Get-ChildItem -LiteralPath $destination -File -Recurse | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.FullName.Substring($destination.Length + 1).Replace('\','/')
}
$checksums | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS') -Encoding ASCII
Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -CompressionLevel Optimal
((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path -Leaf $archive)) | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ASCII
Write-Output ('Package: ' + $archive)
