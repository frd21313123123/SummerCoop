param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Run,
    [string]$BuildDirectory = '', [string]$LateRun = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'build' }
$cases = @('native-references','payment','contents','guest-pick','host-pick','concurrent-pick','empty','concurrent-purchase',
    'all-bags-empty','spawned-objects','filters-created','filter-installed','filter-tightened','filter-unscrewed','filter-removed','fuel-price-reference')
$reports = @{}; $extracts = @{}
$rows = @('SummerCoop 0.4.8 - native MSC checkout / bag verification', ('Run: ' + $Run), '')
foreach ($role in @('host','client')) {
    $report = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '-report.txt')) -Encoding UTF8)
    foreach ($line in @('Level=GAME','ModVersion=0.4.8','NetVersion=1048','TestCursorUnlocked=True','TestAudioMuted=True')) {
        if ($report -notcontains $line) { throw ($role + ': missing ' + $line) }
    }
    foreach ($case in $cases) {
        if (@($report | Where-Object { $_ -match ('^Regression=shopping-' + [regex]::Escape($case) + ';Pass=True;') }).Count -ne 1) { throw ($role + ': failed/missing ' + $case) }
    }
    if ($role -eq 'host' -and @($report | Where-Object { $_ -match '^Regression=shopping-cart;Pass=True;' }).Count -ne 1) { throw 'Observer cart was not preserved.' }
    if ($report -match '^(ActionError|ModuleError)=|;Pass=False;') { throw ($role + ': native probe/module failure.') }
    $log = Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-' + $role + '/mwcoop/logs/mwcoop.log')) -Raw
    if ($log -match 'ERREUR message (Purchase|Consume|Part|Bolt|Save)|ERREUR crochet|ERREUR MSC compatibility|MSC shopping probe:') { throw ($role + ': synchronization exception.') }
    $extracts[$role] = @([regex]::Matches($log, 'MSC shop: extract [^\r\n]+') | ForEach-Object Value)
    $reports[$role] = $report
    $game = if ($role -eq 'host') { 'game' } else { 'game-client' }
    foreach ($file in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested = (Get-FileHash -LiteralPath (Join-Path $root ('.local/' + $game + '/Mods/' + $file)) -Algorithm SHA256).Hash
        $expected = (Get-FileHash -LiteralPath (Join-Path $BuildDirectory ([IO.Path]::GetFileName($file))) -Algorithm SHA256).Hash
        if ($tested -ne $expected) { throw ('Tested DLL differs from its saved build: ' + $file) }
        $rows += $role + ' tested SHA256 ' + $tested + ' ' + $file
    }
    $rows += @('', ($role + ':')) + @($report | Where-Object { $_ -match '^Regression=shopping-|^ShoppingWorldItems=|^ModMvid=' })
}
if ($extracts.host.Count -ne 11 -or ($extracts.host -join "`n") -ne ($extracts.client -join "`n")) { throw 'Extracted product order or native identities diverged.' }
foreach ($case in @('contents','guest-pick','host-pick','concurrent-pick','empty','spawned-objects','filter-installed','filter-tightened','filter-unscrewed','filter-removed','fuel-price-reference')) {
    $pattern = '^Regression=shopping-' + $case + ';'
    $a = @($reports.host | Where-Object { $_ -match $pattern })[0]
    $b = @($reports.client | Where-Object { $_ -match $pattern })[0]
    if ($a -ne $b) { throw ('Host/guest state differs: ' + $case) }
}
if ($LateRun) {
    $late = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $LateRun + '-client-report.txt')) -Encoding UTF8)
    foreach ($line in @('Level=GAME','Ready=True','SaveReceived=True','ModVersion=0.4.8','NetVersion=1048')) { if ($late -notcontains $line) { throw ('Late join: missing ' + $line) } }
    $world = @($late | Where-Object { $_ -like 'ShoppingWorldItems=*' })[0]
    if ($world -ne @($reports.host | Where-Object { $_ -like 'ShoppingWorldItems=*' })[0]) { throw 'Late join lost or duplicated native items.' }
    $lateLog = Get-Content -LiteralPath (Join-Path $root ('.local/' + $LateRun + '-client/mwcoop/logs/mwcoop.log')) -Raw
    if ($lateLog -match 'ERREUR message (Purchase|Consume|Save)|ERREUR MSC compatibility') { throw 'Late-join synchronization failure.' }
    $rows += @('', 'Late join: PASS - verified save, no missing/duplicate item identities.', $world)
}
$before = @(Get-Content -LiteralPath (Join-Path $root ('.local/' + $Run + '-original-before.json')) -Raw | ConvertFrom-Json)
foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) { throw 'An original single-player save changed.' } }
$rows += @('', 'Native extraction order:') + $extracts.host + @('', 'Original single-player saves changed: 0',
    'Scope: actual native MSC processes over local UDP on a hidden desktop. No WAN/two-account Steam or full playthrough claim.')
$output = Join-Path $root 'docs/SHOPPING-048-RESULTS.txt'
$rows | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output $output
