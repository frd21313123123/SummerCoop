param(
    [Parameter(Mandatory = $true)][string]$WorldDirectory,
    [ValidateRange(1,100)][int]$Rounds = 6
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core') { throw 'Run with pwsh (PowerShell 7+) for the allocation counter.' }
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $root '.local\delta-container-benchmark'
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$codec = [IO.File]::ReadAllText((Join-Path $root 'src\Core\Codec.cs'))
$needle = 'else if (n > 0) world.Entities = new Dictionary<string, Entity>(n, StringComparer.Ordinal);'
if (!$codec.Contains($needle)) { throw 'Expected dictionary allocation branch missing; update the replay instrument.' }
# Only the offline helper uses this switch to recreate the old growing dictionary.
# Never overwrite the Core DLL used by the game or tests.
$instrumented = $codec.Replace($needle, 'else if (n > 0 && !DeltaContainerBenchmark.Growing) world.Entities = new Dictionary<string, Entity>(n, StringComparer.Ordinal);')
$codecPath = Join-Path $scratch 'Codec.cs'
[IO.File]::WriteAllText($codecPath, $instrumented)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Core') -Filter '*.cs' | Where-Object Name -ne 'Codec.cs' | ForEach-Object FullName)
$assembly = Join-Path $scratch 'DeltaContainerBenchmark.dll'
& $compiler /nologo /langversion:5 /optimize+ /target:library ('/out:' + $assembly) $sources $codecPath (Join-Path $PSScriptRoot 'DeltaContainerBenchmark.cs')
if ($LASTEXITCODE) { throw 'Replay helper compile failed' }
Add-Type -Path $assembly
[DeltaContainerBenchmark]::Run([IO.Path]::GetFullPath($WorldDirectory), $Rounds) | Tee-Object -FilePath (Join-Path $scratch 'results.txt')
