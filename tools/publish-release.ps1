param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Commit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Commit -notmatch '^[0-9a-f]{40}$') { throw 'A GitHub repository and full commit SHA are required.' }
if (!$env:GITHUB_TOKEN) { throw 'GITHUB_TOKEN with contents:write is required.' }
$root = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
$assets = @(
    "SummerCoop-Launcher/SummerCoop-Launcher.exe",
    "SummerCoop-Launcher/SummerCoop-Launcher.exe.sha256",
    "dist/SummerCoop-Launcher-$Version.exe",
    "dist/SummerCoop-Launcher-$Version.exe.sha256",
    "dist/SummerCoop-$Version.zip",
    "dist/SummerCoop-$Version-source.zip"
) | ForEach-Object { Join-Path $root $_ }
foreach ($file in $assets) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release asset: $file" } }
foreach ($exe in @($assets[0], $assets[2])) {
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $checksum = (Get-Content -LiteralPath ($exe + '.sha256') -Raw).Trim()
    if ($checksum -ne ($hash + '  ' + [IO.Path]::GetFileName($exe))) { throw "Release checksum does not match: $exe" }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -ne $Version) { throw 'EXE version does not match release tag.' }
}
$headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'SummerCoop-release' }
$api = "https://api.github.com/repos/$Repository"
function Api([string]$method, [string]$path, $body = $null) {
    $argsMap = @{ Uri = "$api/$path"; Method = $method; Headers = $headers }
    if ($null -ne $body) { $argsMap.ContentType = 'application/json'; $argsMap.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 10)) }
    Invoke-RestMethod @argsMap
}
$release = $null
try { $release = Api GET "releases/tags/$tag" }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if ($release -and !$release.draft) {
    # A completed release is never rewritten. Reruns verify the same bytes.
    foreach ($file in $assets) {
        $name = [IO.Path]::GetFileName($file)
        $remote = @($release.assets | Where-Object name -eq $name)
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remote.Count -ne 1 -or $remote[0].digest -ne $digest) { throw "Published $tag differs from this build. Publish a new build number." }
    }
    Write-Output "Release already published and verified: $($release.html_url)"
    return
}
if (!$release) {
    $release = Api POST 'releases' @{
        tag_name = $tag; target_commitish = $Commit; name = "SummerCoop $Version"
        draft = $true; prerelease = $Version.Contains('-'); generate_release_notes = $true
    }
} elseif ($release.target_commitish -ne $Commit) { throw 'Existing draft belongs to another commit.' }
foreach ($file in $assets) {
    $name = [IO.Path]::GetFileName($file)
    foreach ($old in @($release.assets | Where-Object name -eq $name)) { Api DELETE "releases/assets/$($old.id)" | Out-Null }
    $upload = $release.upload_url.Split('{')[0] + '?name=' + [Uri]::EscapeDataString($name)
    $asset = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -InFile $file -ContentType 'application/octet-stream'
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.size -ne (Get-Item -LiteralPath $file).Length -or $asset.digest -ne $digest) { throw "Uploaded release asset failed verification: $name" }
}
# Drafts are invisible to launchers until every asset has uploaded successfully.
$release = Api PATCH "releases/$($release.id)" @{ draft = $false; make_latest = $(if ($Version.Contains('-')) { 'false' } else { 'true' }) }
Write-Output "Published $($release.html_url)"
if ($env:GITHUB_STEP_SUMMARY) { "Release: [$tag]($($release.html_url))" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 }
