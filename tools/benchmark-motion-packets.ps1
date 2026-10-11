param([ValidateRange(1,1000000)][int]$Frames = 20000, [ValidateRange(1,100)][int]$Rounds = 4)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core') { throw 'Run with pwsh (PowerShell 7+) for the allocation counter.' }
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $root '.local\motion-packet-benchmark'
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$assembly = Join-Path $scratch 'MotionPacketBenchmark.dll'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Core') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /langversion:5 /optimize+ /target:library ('/out:' + $assembly) $sources (Join-Path $PSScriptRoot 'MotionPacketBenchmark.cs')
if ($LASTEXITCODE) { throw 'Packet benchmark compile failed.' }
Add-Type -Path $assembly
[MotionPacketBenchmark]::Run($Frames, $Rounds) | Tee-Object -FilePath (Join-Path $scratch 'results.txt')
