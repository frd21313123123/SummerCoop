param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$cases=@('revive-standing','revive-crouch','revive-drive','revive-from-driver','log-no-auto-split','log-axe-split',
 'carrier-host-add','carrier-guest-take','carrier-guest-add','carrier-host-take','lamp-on','lamp-off',
 'island-bucket-fill','island-dipper-water','firework-fuse','firework-flight','firework-explosion',
 'fluids-host-fill','fluids-guest-fill','host-sleeps-alone','guest-sleeps-alone','sleep-together',
 'fuel-driver-owner','pub-guest-payment','pub-host-payment','pub-guest-products','pub-host-products','store-guest-payment')
$reports=@{}
$rows=@('SummerCoop 0.4.1 - native regression verification',('Run: '+$Run),('Recorded: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')),'')
foreach($role in @('host','client')) {
    $report=@(Get-Content -LiteralPath (Join-Path $root ('.local/'+$Run+'-'+$role+'-report.txt')) -Encoding UTF8)
    foreach($required in @('Level=GAME','ModVersion=0.4.1','NetVersion=1045','TestCursorUnlocked=True','TestAudioMuted=True')) { if($report -notcontains $required) { throw ($role+': missing '+$required) } }
    if($role -eq 'client' -and ($report -notcontains 'Ready=True' -or $report -notcontains 'SaveReceived=True' -or $report -notcontains 'Players=2')) { throw 'Incomplete guest connection.' }
    $expected=@($cases); if($role -eq 'host') {$expected+='store-other-cart'}
    foreach($case in $expected) { if(@($report | Where-Object {$_ -match ('^Regression='+[regex]::Escape($case)+';Pass=True;')}).Count -ne 1) { throw ($role+': failed/missing '+$case) } }
    if($report -match '^(ActionError|ModuleError)=|;Pass=False;') { throw ($role+': native probe/module failure.') }
    $log=Get-Content -LiteralPath (Join-Path $root ('.local/'+$Run+'-'+$role+'/mwcoop/logs/mwcoop.log')) -Raw
    if($log -match 'ERREUR message (Home|Fluid|Purchase|World)|ERREUR crochet|MSC regression probe:|MSC compatibility:') {throw ($role+': synchronization exception.')}
    $native=Get-Content -LiteralPath (Join-Path $root ('.local/'+$Run+'-'+$role+'-game.log')) -Raw
    if($native -match 'Invalid AABB|transform.position.*NaN|transform.rotation.*NaN') {throw ($role+': invalid native physics.')}
    # Preserve engine/loader diagnostics as well: passing the selected coop
    # cases must not be mistaken for an entirely clean native Unity log.
    $diagnostics=@($native -split "`r?`n" | Where-Object {$_ -match '^\s*(?:System\.)?[A-Za-z.]+Exception:'} | ForEach-Object {$_.Trim()})
    $rows+=@(($role+' native engine/loader diagnostic lines: '+$diagnostics.Count))+@($diagnostics | Sort-Object -Unique | ForEach-Object {'  '+$_})
    $game=Join-Path $root $(if($role -eq 'host'){'.local/game'}else{'.local/game-client'})
    foreach($dll in @('SummerCoop.dll','References/SummerCoop.Core.dll')) {
        $tested=(Get-FileHash -LiteralPath (Join-Path $game ('Mods/'+$dll)) -Algorithm SHA256).Hash
        if($tested -ne (Get-FileHash -LiteralPath (Join-Path $root ('build/'+[IO.Path]::GetFileName($dll))) -Algorithm SHA256).Hash) {throw ('Tested build changed: '+$dll)}
        $rows+=($role+' SHA256 '+$tested+' '+$dll)
    }
    $reports[$role]=$report
    $rows+=@('',($role+' report:'))+@($report | Where-Object {$_ -match '^(Engine|Level|ModVersion|NetVersion|ModMvid|TestCursorUnlocked|TestAudioMuted|TestCursorActionsSuppressed|Ready|Players|SaveReceived|Regression|FrameP95ms|FrameMaxms)='})
}
foreach($case in @('fluids-host-fill','fluids-guest-fill')) {
    $values=@{}
    foreach($role in @('host','client')) {
        $line=@($reports[$role] | Where-Object {$_ -match ('^Regression='+$case+';')})[0]
        $values[$role]=@(($line -replace '^.*Values=','').TrimEnd(',').Split(',') | ForEach-Object {[double]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)})
    }
    if($values.host.Count -ne 6 -or $values.client.Count -ne 6) {throw 'Missing fluid reservoirs.'}
    for($i=0;$i -lt 6;$i++) {if([Math]::Abs($values.host[$i]-$values.client[$i]) -gt .005) {throw ($case+': fluid mismatch '+$i)}}
}
foreach($case in @('pub-guest-products','pub-host-products')) {
    $a=@($reports.host | Where-Object {$_ -match ('^Regression='+$case+';')})[0]
    $b=@($reports.client | Where-Object {$_ -match ('^Regression='+$case+';')})[0]
    if(($a -replace '^.*Ids=','') -ne ($b -replace '^.*Ids=','')) {throw ('Purchased item identities diverged: '+$case)}
}
foreach($case in @('firework-flight','firework-explosion')) {
    $poses=@{}
    foreach($role in @('host','client')) {
        $line=@($reports[$role] | Where-Object {$_ -match ('^Regression='+$case+';')})[0]
        if($line -notmatch ';Position=\(([^)]+)\)') {throw ('Missing rocket position: '+$role)}
        $poses[$role]=@($Matches[1].Split(',') | ForEach-Object {[double]::Parse($_.Trim(),[Globalization.CultureInfo]::InvariantCulture)})
    }
    $distance=0; for($i=0;$i -lt 3;$i++) {$distance += [Math]::Pow($poses.host[$i]-$poses.client[$i],2)}
    if([Math]::Sqrt($distance) -gt 20) {throw ('Rocket stopped following the host: '+$case)}
}
$fuel=@(); foreach($role in @('host','client')) {
    $line=@($reports[$role] | Where-Object {$_ -match '^Regression=fuel-driver-owner;'})[0]
    if($line -notmatch ';Fuel=([^;]+)') {throw 'Missing driven vehicle fuel.'}
    $fuel += [double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)
}
if([Math]::Abs($fuel[0]-$fuel[1]) -gt .005) {throw 'Driven vehicle fuel diverged.'}
foreach($case in @('host-sleeps-alone','guest-sleeps-alone','sleep-together')) {
    $hours=@(); foreach($role in @('host','client')) { $line=@($reports[$role] | Where-Object {$_ -match ('^Regression='+$case+';')})[0]; if($line -notmatch 'Hour=([0-9]+);State=([^;]+)') {throw 'Missing clock observation.'}; $hours+=($Matches[1]+'/'+$Matches[2]) }
    if($hours[0] -ne $hours[1]) {throw ('Clock/lighting states diverged: '+$case)}
}
$before=@(Get-Content -LiteralPath (Join-Path $root ('.local/'+$Run+'-original-before.json')) -Raw | ConvertFrom-Json)
foreach($file in $before) {if((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) {throw 'An original single-player save changed.'}}
$rows+=@('',('Original saves checked: '+$before.Count+'; changed: 0'),'')
foreach($test in @('build/test-results.txt','build/msc-tests/test-results.txt','build/mwcoop-network-tests/test-results.txt','build/launcher-tests/test-results.txt','build/installer-tests/test-results.txt')) {
    $summary=@(Get-Content -LiteralPath (Join-Path $root $test) | Where-Object {$_ -match '^ALL .* PASSED$|^PASS launcher GUI:'})
    if($summary.Count -eq 0) {throw ('Missing automated result: '+$test)}
    $rows+=($test+': '+($summary -join ' | '))
}
$rows+=@('','Scope/fixture limits: see REGRESSIONS-041.md. Two local native MSC processes over UDP; no two-account Steam/WAN test.','No full assembled-engine thermal/leak endurance test and no claim of error-free gameplay in every scenario.')
$output=Join-Path $root 'docs/REGRESSIONS-041-RESULTS.txt'
$rows | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output $output
