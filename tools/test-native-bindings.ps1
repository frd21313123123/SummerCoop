param([string]$GamePath = 'E:\SteamLibrary\steamapps\common\My Summer Car')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-msc-adapter.ps1') -GamePath $GamePath
return
$root = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $GamePath 'mysummercar_Data\Managed'
$testDir = Join-Path $root 'build\native-bindings'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
foreach ($name in @('PlayMaker.dll','UnityEngine.dll','UnityEngine.UI.dll','Assembly-CSharp-firstpass.dll','Assembly-CSharp.dll','ES2.dll','0Harmony.dll','MSCLoader.dll')) {
    Copy-Item -LiteralPath (Join-Path $managed $name) -Destination (Join-Path $testDir $name) -Force
}
Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination $testDir -Force
Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.dll') -Destination $testDir -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$exe = Join-Path $testDir 'NativeBindingsTests.exe'
& $compiler /nologo /langversion:5 /target:exe ('/out:' + $exe) ('/reference:' + (Join-Path $testDir 'SummerCoop.Core.dll')) ('/reference:' + (Join-Path $testDir 'SummerCoop.dll')) ('/reference:' + (Join-Path $testDir 'PlayMaker.dll')) ('/reference:' + (Join-Path $testDir 'UnityEngine.dll')) ('/reference:' + (Join-Path $testDir 'Assembly-CSharp.dll')) ('/reference:' + (Join-Path $testDir 'Assembly-CSharp-firstpass.dll')) (Join-Path $root 'tests\NativeBindings\NativeBindingsTests.cs')
if ($LASTEXITCODE) { throw 'Native binding test build failed' }
& $exe | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
if ($LASTEXITCODE) { throw 'Native binding checks failed' }
