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
$results.Add('ALL 14 NATIVE UPDATE TESTS PASSED')
$results | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
