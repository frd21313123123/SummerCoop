param([string]$LauncherPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'build\native-update-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
if (!$LauncherPath) {
    $LauncherPath = Join-Path $testDir 'SummerCoop-Launcher.exe'
    & (Join-Path $PSScriptRoot 'build-launcher.ps1') -OutputPath $LauncherPath
}
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$results = [Collections.Generic.List[string]]::new()
function Release($version, $asset, $draft = $false, $pre = $false) {
    # Match GitHub's field order, including a nested author and draft before tag_name.
    [ordered]@{
        html_url = "https://github.com/frd21313123123/SummerCoop/releases/tag/v$version"
        author = @{ login = 'fixture'; id = 1 }
        draft = $draft
        tag_name = "v$version"
        prerelease = $pre
        published_at = '2026-10-16T00:00:00Z'
        assets = @(@{ browser_download_url = "https://github.com/frd21313123123/SummerCoop/releases/download/v$version/$asset" })
        body = 'Notes with {braces}, "quoted words", and a backslash \.'
    }
}
function Check($name, $releases, $local, $patterns, $absent = '') {
    $inputPath = Join-Path $testDir 'releases.json'
    $outputPath = Join-Path $testDir 'check.txt'
    ConvertTo-Json -InputObject @($releases) -Depth 8 | Set-Content -LiteralPath $inputPath -Encoding UTF8
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath }
    $arguments = '/testmaj "' + $inputPath + '" "' + $outputPath + '" "' + $local + '"'
    $process = Start-Process -FilePath $LauncherPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(15000)) { $process.Kill(); throw "Timed out: $name" }
    if ($process.ExitCode -ne 0) { throw "Launcher failed: $name" }
    $output = Get-Content -LiteralPath $outputPath -Raw -Encoding UTF8
    foreach ($pattern in $patterns) {
        if ($output -notmatch $pattern) { throw "$name missing $pattern`n$output" }
    }
    if ($absent -and $output -match $absent) { throw "$name unexpectedly contains $absent`n$output" }
    $results.Add("PASS $name")
}
$current = Release '0.4.2' 'SummerCoop-Launcher-0.4.2.exe'
$new = Release '0.4.10' 'SummerCoop-Launcher-0.4.10.exe'
$draft = Release '9.0.0' 'SummerCoop-Launcher-9.0.0.exe' $true
$nightly9 = Release '0.5.0-nightly.9' 'SummerCoop-Launcher-0.5.0-nightly.9.exe' $false $true
$nightly10 = Release '0.5.0-nightly.10' 'SummerCoop-Launcher-0.5.0-nightly.10.exe' $false $true
Check 'newer numeric version and exact release page' @($current, $new) '0.4.2' @('stable : 0\.4\.10 a installer', 'state=3 offline=5 page=https://github.com/frd21313123123/SummerCoop/releases/tag/v0.4.10')
Check 'current version' @($current) '0.4.2' @('stable : 0\.4\.2 rien', 'state=2 offline=5')
Check 'newer local development version' @($current) '0.4.3' @('stable : 0\.4\.2 rien', 'state=2 offline=5')
Check 'build metadata does not offer downgrade' @($current) 'v0.4.2+local' @('stable : 0\.4\.2 rien', 'state=2 offline=5')
Check 'draft cannot leak to neighboring release' @($current, $draft, $new) '0.4.2' @('stable : 0\.4\.10 a installer') '9\.0\.0'
Check 'stable excludes previews and nightly sorts numerically' @($nightly9, $new, $nightly10) '0.4.2' @('stable : 0\.4\.10 a installer', 'nightly : 0\.5\.0-nightly\.10 a installer')
Check 'source archives cannot become updates' @((Release '9.0.0' 'SummerCoop-9.0.0-source.zip'), $new) '0.4.2' @('stable : 0\.4\.10 a installer')
Check 'mod ZIP is supported' @((Release '0.4.10' 'SummerCoop-0.4.10.zip')) '0.4.2' @('stable : 0\.4\.10 a installer')
Check 'unversioned launcher asset is supported' @((Release '0.4.10' 'SummerCoop-Launcher.exe')) '0.4.2' @('stable : 0\.4\.10 a installer')
Check 'empty release list and offline state' @() '0.4.2' @('stable :  rien', 'state=4 offline=5')
Check 'release without downloadable build' @((Release '0.4.10' 'SHA256.txt')) '0.4.2' @('stable :  rien', 'state=4 offline=5')
Check 'stable channel can return from nightly' @($current) '0.5.0-nightly.1' @('stable : 0\.4\.2 a installer', 'nightly : 0\.4\.2 rien')
Check 'launcher binary asset is selected for self-update' @($current, $new) '0.4.2' @('exe=https://github\.com/frd21313123123/SummerCoop/releases/download/v0\.4\.10/SummerCoop-Launcher-0\.4\.10\.exe')
Check 'unversioned launcher asset resolves as exe' @((Release '0.4.10' 'SummerCoop-Launcher.exe')) '0.4.2' @('exe=https://github\.com/frd21313123123/SummerCoop/releases/download/v0\.4\.10/SummerCoop-Launcher\.exe')
Check 'CI revision is newer than a local base version' @((Release '0.4.8.53' 'SummerCoop-Launcher.exe')) '0.4.8' @('stable : 0\.4\.8\.53 a installer')
Check 'CI revision comparison is numeric' @((Release '0.4.8.9' 'SummerCoop-Launcher.exe'), (Release '0.4.8.10' 'SummerCoop-Launcher.exe')) '0.4.8.9' @('stable : 0\.4\.8\.10 a installer')
Check 'no downgrade to an earlier CI revision' @((Release '0.4.8.9' 'SummerCoop-Launcher.exe')) '0.4.8.10' @('stable : 0\.4\.8\.9 rien')
$digest = 'a' * 64
$otherDigest = 'b' * 64
$boundAssets = Release '0.4.10' 'SummerCoop-Launcher.exe'
$boundAssets.assets = @(
    @{ browser_download_url = 'https://github.com/frd21313123123/SummerCoop/releases/download/v0.4.10/SummerCoop-Launcher.exe.sha256' },
    @{ browser_download_url = 'https://github.com/frd21313123123/SummerCoop/releases/download/v0.4.10/SummerCoop-Launcher-0.4.10.exe'; digest = "sha256:$otherDigest" },
    @{ browser_download_url = 'https://github.com/frd21313123123/SummerCoop/releases/download/v0.4.10/SummerCoop-Launcher.exe'; digest = "sha256:$digest" }
)
Check 'checksum and digest belong to the selected EXE' @($boundAssets) '0.4.2' @("sha=$digest checksum=https://github\.com/frd21313123123/SummerCoop/releases/download/v0\.4\.10/SummerCoop-Launcher\.exe\.sha256")
$foreign = Release '9.0.0' 'SummerCoop-Launcher.exe'
$foreign.assets[0].browser_download_url = 'https://example.invalid/SummerCoop-Launcher.exe'
Check 'foreign download host is rejected' @($foreign, $new) '0.4.2' @('stable : 0\.4\.10 a installer')
$nested = Release '0.4.10' 'SummerCoop-Launcher.exe'
$nested.author.draft = $true
$nested.author.prerelease = $true
Check 'nested metadata cannot hide a stable release' @($nested) '0.4.2' @('stable : 0\.4\.10 a installer')

function RunNative([string]$exe, [string]$arguments, [int]$expected = 0) {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$p.WaitForExit(20000)) { $p.Kill(); throw "Native update operation timed out: $arguments" }
    if ($p.ExitCode -ne $expected) { throw "Native update operation returned $($p.ExitCode), expected $expected" }
}
$candidate = Join-Path $testDir 'candidate.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $candidate -Force
# Trailing PE overlay changes the bytes, so replacement can be proved by hash.
$stream = [IO.File]::Open($candidate, [IO.FileMode]::Append)
try { $stream.WriteByte(42) } finally { $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
RunNative $LauncherPath ('/verify-update "' + $candidate + '" ' + $hash)
$results.Add('PASS genuine PE with matching SHA-256 is accepted')
RunNative $LauncherPath ('/verify-update "' + $candidate + '" ' + ('0' * 64)) 1
$results.Add('PASS mismatching SHA-256 is rejected')
RunNative $LauncherPath ('/verify-update "' + $candidate + '" invalid') 1
$results.Add('PASS malformed SHA-256 is rejected')
$invalid = Join-Path $testDir 'invalid.exe'
'Not an executable' | Set-Content -LiteralPath $invalid -Encoding ASCII
$invalidHash = (Get-FileHash -LiteralPath $invalid -Algorithm SHA256).Hash
RunNative $LauncherPath ('/verify-update "' + $invalid + '" ' + $invalidHash) 1
$results.Add('PASS non-PE download is rejected even with matching SHA-256')

function HandoffTest([string]$name, [bool]$locked, [string]$download = $candidate, [bool]$rollback = $false) {
    $dir = Join-Path $testDir ($name + ' space & Юникод !')
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $target = Join-Path $dir 'SummerCoop-Launcher.exe'
    $report = Join-Path $testDir ($name + '-probe.txt')
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    Copy-Item -LiteralPath $LauncherPath -Destination $target -Force
    $oldHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $settings = Join-Path $dir 'summercoop-launcher.ini'
    "[Lanceur]`r`nLangue=ru`r`nAutoUpdate=0`r`nCanal=stable`r`nSauvegarde=partie-2" | Set-Content -LiteralPath $settings -Encoding UTF8
    $iniHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
    $saveRoot = Join-Path $testDir ($name + '-worlds')
    $saveHashes = @{}
    foreach ($profile in @('host','partie-1','partie-2','guest')) {
        $saveDirectory = Join-Path $saveRoot $profile
        New-Item -ItemType Directory -Path $saveDirectory -Force | Out-Null
        $save = Join-Path $saveDirectory 'defaultES2File.txt'
        [IO.File]::WriteAllText($save, ('Saved world: ' + $profile))
        $saveHashes[$save] = (Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash
    }
    $downloadHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    $lock = $null
    try {
        if ($locked) { $lock = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
        RunNative $target ('/test-update-handoff "' + $download + '" ' + $downloadHash + ' "' + $report + '"')
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        while (!(Test-Path -LiteralPath $report) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath $report)) { throw "$name did not restart the launcher" }
        $expectedHash = $(if ($locked -or $rollback) { $oldHash } else { $downloadHash })
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expectedHash) { throw "$name left the wrong executable" }
        if ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -ne $iniHash) { throw "$name changed user settings" }
        foreach ($save in $saveHashes.Keys) {
            if ((Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash -ne $saveHashes[$save]) { throw "$name changed a saved world" }
        }
        if ((Get-Content -LiteralPath $report -Raw) -notmatch $expectedHash) { throw "$name restarted the wrong executable" }
        $unexpected = @(Get-ChildItem -LiteralPath $dir -File | Where-Object Name -notin @('SummerCoop-Launcher.exe','SummerCoop-Launcher.exe.sha256','summercoop-launcher.ini'))
        if ($unexpected.Count) { throw "$name left update helpers in the user folder" }
        $checksum = Get-Content -LiteralPath ($target + '.sha256') -Raw
        if ($checksum -notmatch $expectedHash) { throw "$name left a stale checksum" }
    } finally { if ($lock) { $lock.Dispose() } }
    $results.Add("PASS $name preserves settings, selected save, all world files, original path and clean standalone folder")
}
HandoffTest 'native replacement and restart' $false
HandoffTest 'locked EXE recovery' $true
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'MSVC is required for the failed-launch recovery probe.' }
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$probe = Join-Path $testDir 'exit-probe.exe'
$probeSource = Join-Path $root 'tests\Launcher\UpdateExitProbe.cpp'
$probeObject = Join-Path $testDir 'exit-probe.obj'
$probeBatch = Join-Path $testDir 'compile-exit-probe.cmd'
@"
@echo off
call "$vcvars" >nul || exit /b 1
cl /nologo /O1 /MT /Fo"$probeObject" /Fe"$probe" "$probeSource" /link /SUBSYSTEM:WINDOWS || exit /b 1
"@ | Set-Content -LiteralPath $probeBatch -Encoding ASCII
& $env:ComSpec /d /c $probeBatch
if ($LASTEXITCODE) { throw 'Failed-launch recovery probe compilation failed.' }
$stream = [IO.File]::Open($probe, [IO.FileMode]::Open)
try { $stream.SetLength(600000) } finally { $stream.Dispose() }
HandoffTest 'new process exits before startup acknowledgement' $false $probe $true
$payloadDir = Join-Path $testDir 'embedded payload with stale adjacent DLLs'
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
$payloadLauncher = Join-Path $payloadDir 'SummerCoop-Launcher.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $payloadLauncher -Force
foreach ($name in @('SummerCoop.dll', 'SummerCoop.Core.dll')) { 'obsolete external payload' | Set-Content -LiteralPath (Join-Path $payloadDir $name) -Encoding ASCII }
$fakeGame = Join-Path $payloadDir 'fake game'
New-Item -ItemType Directory -Path $fakeGame -Force | Out-Null
RunNative $payloadLauncher ('/test-mod-resources "' + $fakeGame + '"')
foreach ($pair in @(@('SummerCoop.dll','Mods\SummerCoop.dll'), @('SummerCoop.Core.dll','Mods\References\SummerCoop.Core.dll'))) {
    $expected = (Get-FileHash -LiteralPath (Join-Path $root ('build\' + $pair[0])) -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath (Join-Path $fakeGame $pair[1]) -Algorithm SHA256).Hash
    if ($expected -ne $actual) { throw 'Stale adjacent DLL replaced the embedded release payload.' }
}
$results.Add('PASS embedded release DLLs take priority over stale external DLLs')
$results.Add("ALL $($results.Count) NATIVE UPDATE TESTS PASSED")
$results | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
