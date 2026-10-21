param([string]$GamePath = $env:SUMMERCOOP_GAME_PATH, [switch]$SkipTests, [switch]$SkipVendorCopy)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$GamePath) { $GamePath = 'E:\SteamLibrary\steamapps\common\My Summer Car' }
$managed = (Resolve-Path -LiteralPath (Join-Path $GamePath 'mysummercar_Data\Managed')).Path
$required = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','ES2.dll','MSCLoader.dll','0Harmony.dll')
foreach ($name in $required) { if (!(Test-Path -LiteralPath (Join-Path $managed $name))) { throw ('Missing MSC reference: ' + $name) } }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkRoot = Join-Path (Split-Path -Parent $dotnet) 'sdk'
$sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Roslyn\bincore\csc.dll') } | Sort-Object { [version]($_.Name.Split('-')[0]) } -Descending | Select-Object -First 1
if (!$sdk) { throw 'Install .NET SDK 8 or newer (Roslyn C# compiler).' }
$build = Join-Path $root 'build'; New-Item -ItemType Directory -Path $build -Force | Out-Null
$bcl = @('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$core = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Core') -Filter '*.cs' | ForEach-Object FullName)
& $framework /nologo /noconfig /nostdlib+ /langversion:5 /target:library /optimize+ ('/out:' + (Join-Path $build 'SummerCoop.Core.dll')) $bcl $core
if ($LASTEXITCODE) { throw 'Core build failed' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'vendor\MWCoop\src\MWCoop') -Filter '*.cs' -Recurse | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'src\MSC') -Filter '*.cs' | ForEach-Object FullName)
$sources += @('NativeAssembly.cs','NativeActionReader.cs') | ForEach-Object { Join-Path $root ('src\Game\' + $_) }
$refs = $required | ForEach-Object { '-r:' + (Join-Path $managed $_) }
$refs += '-r:' + (Join-Path $build 'SummerCoop.Core.dll')
$upstream = Join-Path $root 'vendor\MWCoop'
$buildText = Get-Content -LiteralPath (Join-Path $upstream 'build.cmd') -Raw
$resources = @([regex]::Matches($buildText, '-resource:([^\s,]+),([^\s]+)') | ForEach-Object {
    $resourcePath = Join-Path $upstream $_.Groups[1].Value
    if ($_.Groups[2].Value -eq 'MWCoop.logo-titre.png') { $resourcePath = Join-Path $root 'launcher\logo-titre.png' }
    '-resource:' + $resourcePath + ',' + $_.Groups[2].Value
})
& $dotnet (Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll') -nologo -noconfig -nostdlib -target:library -optimize -langversion:7.3 '-nowarn:1701,1702,0108,0618,0169,0414,0649,0219' ('-out:' + (Join-Path $build 'SummerCoop.dll')) $refs $resources $sources
if ($LASTEXITCODE) { throw 'MWCoop MSC adapter build failed' }
if (!$SkipTests) {
    $temp = Join-Path $build 'test-temp'; New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $oldTemp = $env:TEMP; $oldTmp = $env:TMP
    try {
        $env:TEMP = $temp; $env:TMP = $temp
        $tests = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object FullName)
        & $framework /nologo /langversion:5 /target:exe ('/out:' + (Join-Path $build 'SummerCoop.Tests.exe')) ('/reference:' + (Join-Path $build 'SummerCoop.Core.dll')) $tests
        if ($LASTEXITCODE) { throw 'Core tests build failed' }
        & (Join-Path $build 'SummerCoop.Tests.exe') | Tee-Object -FilePath (Join-Path $build 'test-results.txt')
        if ($LASTEXITCODE) { throw 'Core tests failed' }
        & (Join-Path $PSScriptRoot 'test-msc-adapter.ps1') -GamePath $GamePath
    } finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp }
}
# Isolated QA can keep its two outputs in build while a running game has the
# convenience vendor DLL mapped. Release/default builds still update it.
if (!$SkipVendorCopy) {
    $vendorBin = Join-Path $root 'vendor\bin'
    New-Item -ItemType Directory -Path $vendorBin -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $build 'SummerCoop.dll') -Destination (Join-Path $vendorBin 'SummerCoop.dll') -Force
}
Write-Output ('MWCoop engine for My Summer Car built: ' + (Join-Path $build 'SummerCoop.dll'))
