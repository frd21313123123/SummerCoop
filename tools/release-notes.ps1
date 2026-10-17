param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Commit = 'HEAD',
    [string]$PreviousTag = '',
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'A GitHub repository is required.' }
if ($PreviousTag -and $PreviousTag -notmatch '^v?[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid previous release tag.' }
$RepositoryPath = [IO.Path]::GetFullPath($RepositoryPath)
function GitText([string[]]$gitArguments) {
    $lines = @(& git.exe -C $RepositoryPath -c core.quotepath=false -c i18n.logOutputEncoding=utf-8 @gitArguments)
    if ($LASTEXITCODE) { throw ('Git failed: ' + ($gitArguments -join ' ')) }
    $lines -join "`n"
}
$previousEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $head = (GitText @('rev-parse', '--verify', ($Commit + '^{commit}'))).Trim()
    if ($PreviousTag) {
        $null = GitText @('rev-parse', '--verify', ($PreviousTag + '^{commit}'))
        $null = GitText @('merge-base', '--is-ancestor', $PreviousTag, $head)
    }
    if ($PreviousTag) {
        $changed = GitText @('diff', '--name-only', '--diff-filter=AM', ($PreviousTag + '..' + $head), '--', 'docs/changelog/*.md')
    } else {
        $changed = GitText @('ls-tree', '-r', '--name-only', $head, '--', 'docs/changelog/')
    }
    $sections = @{ fr = [Collections.Generic.List[string]]::new(); en = [Collections.Generic.List[string]]::new(); ru = [Collections.Generic.List[string]]::new() }
    foreach ($path in @($changed -split "`n" | Where-Object { $_ -match '^docs/changelog/[^/]+\.md$' -and $_ -notmatch '(?i)/README\.md$' } | Sort-Object -Unique)) {
        $fragment = GitText @('show', ($head + ':' + $path))
        $languages = @{}
        foreach ($match in [regex]::Matches($fragment, '(?ims)^##[ \t]+(fr|en|ru)[ \t]*\r?\n(.*?)(?=^##[ \t]+(?:fr|en|ru)[ \t]*(?:\r?\n|$)|\z)')) {
            $language = $match.Groups[1].Value.ToLowerInvariant()
            if ($languages.ContainsKey($language)) { throw "Duplicate language in $path : $language" }
            $languages[$language] = $match.Groups[2].Value.Trim().Replace('{{version}}', $Version)
        }
        if (!$languages.ru -or !$languages.en) { throw "Changelog fragment needs nonempty ## ru and ## en sections: $path" }
        foreach ($language in @('fr', 'en', 'ru')) {
            $text = $(if ($languages[$language]) { $languages[$language] } else { $languages.en })
            $sections[$language].Add($text)
        }
    }
    if (!$sections.en.Count) {
        $range = $(if ($PreviousTag) { $PreviousTag + '..' + $head } else { $head })
        $commits = GitText @('log', '--no-merges', '--format=%s (%h)', '-30', $range)
        if ($commits.Trim()) {
            $bullets = @($commits -split "`n" | ForEach-Object { '- ' + $_ }) -join "`n"
            $sections.fr.Add("### Changements du code`n`n$bullets")
            $sections.en.Add("### Code changes`n`n$bullets")
            $sections.ru.Add("### Изменения в коде`n`n$bullets")
        } else {
            $sections.fr.Add('- Nouvelle compilation des mêmes sources. Aucun changement du code du jeu.')
            $sections.en.Add('- Rebuilt the same source revision. No game code changes.')
            $sections.ru.Add('- Повторная сборка тех же исходников. Изменений игрового кода нет.')
        }
    }
    $url = $(if ($PreviousTag) { "https://github.com/$Repository/compare/$PreviousTag...v$Version" } else { "https://github.com/$Repository/commits/$head" })
    $titles = @{ fr = 'Français'; en = 'English'; ru = 'Русский' }
    $links = @{ fr = 'Toutes les modifications'; en = 'Full changelog'; ru = 'Все изменения' }
    $bodies = foreach ($language in @('fr', 'en', 'ru')) {
        # Preserve FR --- EN --- RU for launchers released before this generator.
        "## $($titles[$language])`n`n" + ($sections[$language] -join "`n`n") + "`n`n[$($links[$language])]($url)"
    }
    $body = ($bodies -join "`n`n---`n`n") + "`n"
    if ($body.Length -gt 100000) { throw 'Release notes are too long; shorten the changelog fragments.' }
    if ($OutputPath) {
        $OutputPath = [IO.Path]::GetFullPath($OutputPath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
        [IO.File]::WriteAllText($OutputPath, $body, [Text.UTF8Encoding]::new($false))
    }
    $body
} finally { [Console]::OutputEncoding = $previousEncoding }
