param([Parameter(Mandatory=$true)][string]$LauncherPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$run = Join-Path $root ('build/update-discovery-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$repo = 'https://github.com/frd21313123123/SummerCoop'
$tag = 'v0.5.4.100'
$sha = 'a' * 64
$results = [Collections.Generic.List[string]]::new()
$body = ConvertTo-Json -InputObject @(@{tag_name=$tag; draft=$false; prerelease=$false; assets=@(@{browser_download_url="$repo/releases/download/$tag/SummerCoop-Launcher.exe";digest="sha256:$sha"})}) -Depth 6 -Compress
function Check([string]$name, [hashtable]$extra, [string[]]$patterns) {
    $directory = Join-Path $run $name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $fixture = @{cache=(Join-Path $directory 'cache'); code='403'; remaining='0'; reset=([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()+3600).ToString(); latest="$repo/releases/tag/$tag"; checksumUrl="$repo/releases/download/$tag/SummerCoop-Launcher.exe.sha256"; checksum="$sha  SummerCoop-Launcher.exe";body='API rate limit exceeded'}
    foreach ($entry in $extra.GetEnumerator()) { $fixture[$entry.Key] = $entry.Value }
    $inputFile = Join-Path $directory 'input.json'; $report = Join-Path $directory 'report.txt'
    $fixture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inputFile -Encoding UTF8
    $process = Start-Process -FilePath $LauncherPath -ArgumentList ('/test-update-discovery "' + $inputFile + '" "' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(20000)) { $process.Kill(); throw "Timeout: $name" }
    if ($process.ExitCode) { throw "Discovery fixture failed: $name" }
    $text = Get-Content -LiteralPath $report -Raw
    foreach ($pattern in $patterns) { if ($text -notmatch $pattern) { throw "$name missing $pattern`n$text" } }
    Write-Output "PASS $name"
    $results.Add("PASS $name")
    if ($name -eq 'quota-does-not-block-stable') {
        $process = Start-Process -FilePath $LauncherPath -ArgumentList ('/test-update-discovery "' + $inputFile + '" "' + $report + '"') -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(20000) -or $process.ExitCode) { throw 'Restarted cache check failed' }
        $text = Get-Content -LiteralPath $report -Raw
        if ($text -notmatch 'limited=1 calls=0 repeated=0' -or $text -notmatch 'stable=1') { throw "Restart ignored persisted cooldown: $text" }
        Write-Output 'PASS quota cooldown survives process restart'; $results.Add('PASS quota cooldown survives process restart')
    }
}
Check 'quota-does-not-block-stable' @{} @('limited=1 calls=1 repeated=0', 'stable=1 version=0.5.4.100', 'matchesSelf=1 mismatchedSelf=0')
Check 'secondary-limit-retry-after' @{code='429'; remaining='9'; reset='0'; retryAfter='90'} @('limited=1 calls=1 repeated=0', 'stable=1')
Check 'retry-after-http-date' @{code='429'; remaining='9'; reset='0'; retryAfter=([DateTime]::UtcNow.AddMinutes(2).ToString('R',[Globalization.CultureInfo]::InvariantCulture))} @('limited=1 calls=1 repeated=0', 'stable=1')
Check 'secondary-403-is-a-limit' @{remaining='9';reset='0';body='You have exceeded a secondary rate limit'} @('limited=1 calls=1 repeated=0')
Check 'forbidden-is-not-a-quota' @{remaining='12';reset='0';body='Access denied'} @('limited=0', 'stable=1')
Check 'conditional-304-uses-cache' @{code='304';seed='yes';body=$body} @('api=1 second=1 limited=0 calls=2 repeated=0', 'etag="fixture-etag"', 'cache=1')
Check 'fresh-cache-avoids-second-query' @{code='200';body=$body} @('api=1 second=1 limited=0 calls=1 repeated=0', 'cache=1')
Check 'empty-release-list-is-cached' @{code='200';body="[ `r`n ]"} @('api=0 second=0 limited=0 calls=1 repeated=0', 'cache=1')
Check 'foreign-redirect-rejected' @{latest='https://example.invalid/releases/tag/v9.0.0'} @('stable=0')
Check 'preview-is-not-a-stable-update' @{latest="$repo/releases/tag/v0.5.4-nightly.1"} @('stable=0')
Check 'release-is-pinned-before-checksum' @{checksumUrl="$repo/releases/download/v0.5.4.101/SummerCoop-Launcher.exe.sha256"} @('stable=0')
Check 'bad-checksum-rejected' @{checksum='not-a-hash'} @('stable=0')
Check 'api-offline-stable-still-works' @{code='0';remaining='';reset='0';body=''} @('limited=0', 'stable=1')
$results | Set-Content -LiteralPath (Join-Path $root 'build/update-discovery-tests/test-results.txt') -Encoding UTF8
Write-Output "PASS update discovery fixtures: $run"
