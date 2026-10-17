param([string]$LauncherPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'build\release-notes-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
if (!$LauncherPath) {
    $LauncherPath = Join-Path $testDir 'SummerCoop-Launcher.exe'
    & (Join-Path $PSScriptRoot 'build-launcher.ps1') -OutputPath $LauncherPath
}
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$results = [Collections.Generic.List[string]]::new()
$fixture = Join-Path $testDir ([Guid]::NewGuid().ToString('N') + ' git fixture')
$fragmentDir = Join-Path $fixture 'docs\changelog'
New-Item -ItemType Directory -Path $fragmentDir -Force | Out-Null
function PutText([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false)) }
function FixtureGit([string[]]$gitArguments) {
    $null = & git.exe -C $fixture @gitArguments
    if ($LASTEXITCODE) { throw ('Fixture git command failed: ' + ($gitArguments -join ' ')) }
}
function Generate([string]$version, [string]$previous = '') {
    $outputPath = Join-Path $testDir ('notes-' + $version + '.md')
    $null = & (Join-Path $PSScriptRoot 'release-notes.ps1') -Version $version -Repository 'fixture/SummerCoop' -RepositoryPath $fixture -PreviousTag $previous -OutputPath $outputPath
    [IO.File]::ReadAllText($outputPath, [Text.Encoding]::UTF8)
}
function Assert([bool]$condition, [string]$name) {
    if (!$condition) { throw $name }
    $results.Add('PASS ' + $name)
}
function ReadNotes([string]$body) {
    $json = Join-Path $testDir 'releases.json'
    $report = Join-Path $testDir 'native-notes.txt'
    $release = @{tag_name='v0.4.8.59'; published_at='2026-10-18T00:00:00Z'; body=$body; assets=@(); draft=$false; prerelease=$false}
    PutText $json (ConvertTo-Json -InputObject @($release) -Depth 8)
    $process = Start-Process -FilePath $LauncherPath -ArgumentList ('/test-notes "' + $json + '" "' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(15000)) { $process.Kill(); throw 'Native changelog reader timed out.' }
    if ($process.ExitCode) { throw 'Native changelog reader failed.' }
    [IO.File]::ReadAllText($report, [Text.Encoding]::UTF8)
}
function Section([string]$report, [string]$name) {
    $match = [regex]::Match($report, '(?ms)^\[' + [regex]::Escape($name) + '\]\r?\n(.*?)(?=^\[|\z)')
    if (!$match.Success) { throw ('Missing native report section ' + $name) }
    $match.Groups[1].Value.Trim().Replace("`r`n", "`n")
}
FixtureGit @('init', '-q', '-b', 'main')
FixtureGit @('config', 'user.name', 'SummerCoop tests')
FixtureGit @('config', 'user.email', 'tests@example.invalid')
FixtureGit @('config', 'commit.gpgSign', 'false')
PutText (Join-Path $fragmentDir 'past.md') "## ru`n- Старые изменения.`n`n## en`n- Old changes."
FixtureGit @('add', '.')
FixtureGit @('commit', '-q', '-m', 'Baseline')
FixtureGit @('tag', 'v0.4.8.58')
PutText (Join-Path $fragmentDir 'current.md') "## ru`n### Добавлено`n- Русская заметка {{version}}.`n- Второй пункт.`n`n## en`n### Added`n- English note {{version}}.`n- Second item."
FixtureGit @('add', '.')
FixtureGit @('commit', '-q', '-m', 'Add release notes')
$body = Generate '0.4.8.59' 'v0.4.8.58'
Assert ($body.Contains('Русская заметка 0.4.8.59.') -and $body.Contains('English note 0.4.8.59.')) 'generate readable UTF-8 notes and resolve the build number'
Assert (!$body.Contains('Старые изменения') -and !$body.Contains('Old changes')) 'include only fragments since the previous release'
Assert ($body.Contains('compare/v0.4.8.58...v0.4.8.59')) 'link to the exact release comparison'
Assert ([regex]::Matches($body, '(?m)^---$').Count -eq 2) 'retain the language separators understood by older launchers'
Assert ([regex]::Match($body, '(?s)## Français\s+(.*?)\n\n---').Groups[1].Value.Contains('English note')) 'fall back to English when a French fragment is absent'
$native = ReadNotes $body
Assert ((Section $native 'ru').Contains('Русская заметка 0.4.8.59.') -and !(Section $native 'ru').Contains('English note')) 'native launcher reads the generated Russian section'
Assert ((Section $native 'en').Contains('English note 0.4.8.59.') -and !(Section $native 'en').Contains('Русская')) 'native launcher reads the generated English section'
Assert ((Section $native 'preview-ru') -eq "Русская заметка 0.4.8.59.`nВторой пункт.") 'home preview shows change bullets rather than headings or comparison links'
Assert (!(Section $native 'ru').Contains('https://') -and !(Section $native 'ru').Contains('##')) 'native rendering removes Markdown link syntax and heading markers'
FixtureGit @('tag', 'v0.4.8.59')
PutText (Join-Path $fixture 'README.md') 'Another change'
FixtureGit @('add', '.')
FixtureGit @('commit', '-q', '-m', 'Fix launcher startup')
$fallback = Generate '0.4.8.60' 'v0.4.8.59'
Assert ($fallback.Contains('- Fix launcher startup (') -and !$fallback.Contains('English note') -and !$fallback.Contains('Add release notes (')) 'automatically list only new commits when no fragment was added'
FixtureGit @('tag', 'v0.4.8.60')
$rebuild = Generate '0.4.8.61' 'v0.4.8.60'
Assert ($rebuild.Contains('Повторная сборка тех же исходников') -and !$rebuild.Contains('Fix launcher startup (')) 'explain a rebuild of an unchanged source revision'
$first = Generate '0.4.8.62'
Assert ($first.Contains('Старые изменения') -and $first.Contains('Русская заметка')) 'support an initial release with no predecessor'
$legacy = ReadNotes "- Note française.`n---`n- English legacy note.`n---`n- Старая русская заметка."
Assert ((Section $legacy 'ru').Contains('Старая русская заметка.') -and (Section $legacy 'en').Contains('English legacy note.')) 'preserve the legacy FR EN RU release format'
$named = ReadNotes "## Русский`n### Исправлено`n- Новый пункт.`n---`n- Ещё пункт.`n## English`n- English change.`n## Français`n- Correction française."
Assert ((Section $named 'ru').Contains('Ещё пункт.') -and !(Section $named 'ru').Contains('English change')) 'recognize named languages in any order and allow separators inside a section'
$single = ReadNotes "## English`n### Added`n- **Readable** [link](https://example.invalid/test).`n<!-- hidden metadata -->`n![image](https://example.invalid/image.png)"
Assert ((Section $single 'ru').Contains('Readable link.') -and !(Section $single 'ru').Contains('hidden metadata')) 'fall back to available notes and remove hidden Markdown metadata'
Assert ((Section $single 'preview-en') -eq 'Readable link.') 'keep images out of the home preview'
$empty = ReadNotes ''
Assert ((Section $empty 'ru').Contains('описание изменений не опубликовано')) 'show an explanation for a release with no description'
PutText (Join-Path $fragmentDir 'invalid.md') "## en`n- Missing Russian section."
FixtureGit @('add', '.')
FixtureGit @('commit', '-q', '-m', 'Invalid fragment')
$rejected = $false
try { $null = Generate '0.4.8.63' 'v0.4.8.60' } catch { $rejected = $_.Exception.Message -like '*needs nonempty*' }
Assert $rejected 'reject an incomplete changelog instead of publishing empty localized notes'
$results.Add("ALL $($results.Count) RELEASE NOTE TESTS PASSED")
$results | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
