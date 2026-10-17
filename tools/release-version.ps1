param([string]$BuildNumber = $env:GITHUB_RUN_NUMBER)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$model = Get-Content -LiteralPath (Join-Path $root 'src\Core\Model.cs') -Raw
if ($model -notmatch 'ModVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)(-[0-9A-Za-z.-]+)?"') { throw 'Cannot read the mod version.' }
$base = $Matches[1]; $suffix = $Matches[2]
$number = 0
if (![int]::TryParse($BuildNumber, [ref]$number) -or $number -lt 1 -or $number -gt 65535) { throw 'Build number must be between 1 and 65535.' }
$version = "$base.$number$suffix"
if ($env:GITHUB_OUTPUT) {
    @("VERSION=$version", "TAG=v$version", "PRERELEASE=$([string]($suffix.Length -gt 0))") |
        Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}
Write-Output $version
