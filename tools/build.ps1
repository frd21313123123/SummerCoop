param([string]$GamePath = $env:SUMMERCOOP_GAME_PATH, [switch]$TestsOnly, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is missing.' }
$buildDir = Join-Path $root 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$core = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Core') -Filter '*.cs' | ForEach-Object FullName)
# Standalone protocol tests run on hosted Windows without any game binaries.
# The actual mod always compiles against the game's Mono BCL and assemblies.
$compilerOptions = @('/nologo','/langversion:5')
$bcl = @()
if (!$TestsOnly) {
    if (!$GamePath) { $GamePath = 'E:\SteamLibrary\steamapps\common\My Summer Car' }
    $managed = Join-Path $GamePath 'mysummercar_Data\Managed'
    foreach ($required in @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','UnityEngine.UI.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','MSCLoader.dll','0Harmony.dll','ES2.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $managed $required))) { throw ('Missing game reference ' + $required + '. Specify -GamePath for a game with MSCLoader installed.') }
    }
    $compilerOptions += @('/noconfig','/nostdlib+')
    $bcl = @('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
}
& $compiler $compilerOptions /target:library /optimize+ ('/out:' + (Join-Path $buildDir 'SummerCoop.Core.dll')) $bcl $core
if ($LASTEXITCODE) { throw 'Core build failed' }
if (!$TestsOnly) {
    $gameSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Game') -Filter '*.cs' -Recurse | ForEach-Object FullName)
    $references = @('UnityEngine.dll','UnityEngine.UI.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','MSCLoader.dll','0Harmony.dll','ES2.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
    & $compiler /nologo /noconfig /nostdlib+ /langversion:5 /target:library /optimize+ ('/out:' + (Join-Path $buildDir 'SummerCoop.dll')) $bcl $references ('/reference:' + (Join-Path $buildDir 'SummerCoop.Core.dll')) $gameSources
    if ($LASTEXITCODE) { throw 'Game mod build failed' }
}
$testsDir = Join-Path $root 'tests'
if ((Test-Path -LiteralPath $testsDir) -and !$SkipTests) {
    $tests = @(Get-ChildItem -LiteralPath $testsDir -Filter '*.cs' | ForEach-Object FullName)
    if ($tests.Count) {
        & $compiler /nologo /langversion:5 /target:exe ('/out:' + (Join-Path $buildDir 'SummerCoop.Tests.exe')) ('/reference:' + (Join-Path $buildDir 'SummerCoop.Core.dll')) $tests
        if ($LASTEXITCODE) { throw 'Tests build failed' }
        & (Join-Path $buildDir 'SummerCoop.Tests.exe') | Tee-Object -FilePath (Join-Path $buildDir 'test-results.txt')
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
}
Write-Output "Build completed: $buildDir"
