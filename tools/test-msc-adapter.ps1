param([string]$GamePath = 'E:\SteamLibrary\steamapps\common\My Summer Car', [switch]$NoSockets)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $GamePath 'mysummercar_Data\Managed'
$dir = Join-Path $root 'build\msc-tests'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
$libraries = @('UnityEngine.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','ES2.dll','0Harmony.dll','MSCLoader.dll')
foreach ($name in $libraries) { Copy-Item -LiteralPath (Join-Path $managed $name) -Destination $dir -Force }
foreach ($name in @('SummerCoop.dll','SummerCoop.Core.dll')) { Copy-Item -LiteralPath (Join-Path $root ('build\' + $name)) -Destination $dir -Force }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$refs = @('SummerCoop.dll','SummerCoop.Core.dll','UnityEngine.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll') | ForEach-Object { '/reference:' + (Join-Path $dir $_) }
$exe = Join-Path $dir 'MscAdapterTests.exe'
& $compiler /nologo /langversion:5 /target:exe ('/out:' + $exe) $refs (Join-Path $root 'tests\MSC\MscAdapterTests.cs')
if ($LASTEXITCODE) { throw 'MSC adapter tests compilation failed' }
$argsList = @(); if ($NoSockets) { $argsList += '--no-sockets' }
& $exe $argsList | Tee-Object -FilePath (Join-Path $dir 'test-results.txt')
if ($LASTEXITCODE) { throw 'MSC adapter tests failed' }
