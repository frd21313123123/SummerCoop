param(
    [string]$PackagePath = '',
    [string]$OutputPath = '',
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$model = Get-Content -LiteralPath (Join-Path $root 'src\Core\Model.cs') -Raw
if ($model -notmatch 'ModVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)"') { throw 'Cannot read the mod version.' }
$modVersion = $Matches[1]
if (!$Version) { $Version = $modVersion }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid launcher version.' }

if (!$OutputPath) {
    $OutputPath = Join-Path $root 'SummerCoop-Launcher\SummerCoop-Launcher.exe'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDir = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$buildDir = Join-Path $root 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

# Ensure mod DLLs are present for PE resources
$modDll = Join-Path $buildDir 'SummerCoop.dll'
if (!(Test-Path -LiteralPath $modDll)) {
    $vendorDll = Join-Path $root 'vendor\bin\SummerCoop.dll'
    if (Test-Path -LiteralPath $vendorDll) {
        Copy-Item -LiteralPath $vendorDll -Destination $modDll -Force
    }
}
$coreDll = Join-Path $buildDir 'SummerCoop.Core.dll'
if (!(Test-Path -LiteralPath $coreDll)) {
    & (Join-Path $root 'tools\build.ps1') -TestsOnly
}

# Generate mwversion.h
$parts = $Version.Split('-')[0].Split('.')
while ($parts.Count -lt 4) { $parts += '0' }
foreach ($part in $parts) { if ([long]$part -gt 65535) { throw 'Windows version components cannot exceed 65535.' } }
$mwvNum = $parts -join ','
$versionHeader = @"
#define MWV_NUM $mwvNum
#define MWV_STR "$Version"
#define MWV_MOD_STR "$modVersion"
"@
$versionHeader | Set-Content -LiteralPath (Join-Path $buildDir 'mwversion.h') -Encoding ascii
$versionHeader | Set-Content -LiteralPath (Join-Path (Join-Path $root 'launcher') 'mwversion.h') -Encoding ascii

# Locate Visual Studio Build Tools
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) {
    throw 'vswhere.exe not found. Visual Studio C++ build tools are required.'
}
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) {
    throw 'Visual Studio C++ build tools (MSVC x64) not found.'
}
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
if (!(Test-Path -LiteralPath $vcvars)) {
    throw "vcvars64.bat not found at $vcvars"
}

# Compile native launcher
$launcherDir = Join-Path $root 'launcher'
$resFile = Join-Path $buildDir 'launcher.res'
$cmdScript = Join-Path $buildDir 'compile-launcher.cmd'
$intermediateExe = Join-Path $buildDir 'SummerCoop-Launcher.exe'

$batch = @"
@echo off
call "$vcvars" >nul || exit /b 1
cd /d "$launcherDir"
rc /nologo /i "$buildDir" /i "$launcherDir" /fo "$resFile" launcher.rc || exit /b 1
cl /nologo /O2 /MT /W3 /EHsc /utf-8 /std:c++17 /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS /Fo"$buildDir\\" launcher.cpp "$resFile" /Fe"$intermediateExe" /link /SUBSYSTEM:WINDOWS user32.lib gdi32.lib gdiplus.lib winhttp.lib comdlg32.lib shell32.lib ole32.lib advapi32.lib shlwapi.lib ws2_32.lib winmm.lib oleaut32.lib uuid.lib delayimp.lib /DELAYLOAD:winhttp.dll /DELAYLOAD:winmm.dll || exit /b 1
"@

$batch | Set-Content -LiteralPath $cmdScript -Encoding ascii
& $env:ComSpec /d /c $cmdScript
if ($LASTEXITCODE -ne 0) {
    throw "Native launcher compilation failed with exit code $LASTEXITCODE"
}

$deployExe = {
    param([string]$target)
    if (!$target) { return }
    $dir = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $old = $target + '.old'
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
    try {
        Copy-Item -LiteralPath $intermediateExe -Destination $target -Force -ErrorAction Stop
    } catch [IO.IOException] {
        Move-Item -LiteralPath $target -Destination $old -Force -ErrorAction Stop
        try { Copy-Item -LiteralPath $intermediateExe -Destination $target -Force -ErrorAction Stop }
        catch { Move-Item -LiteralPath $old -Destination $target -Force; throw }
        Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $target) {
        $h = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        ("$h  " + [IO.Path]::GetFileName($target)) | Set-Content -LiteralPath ($target + '.sha256') -Encoding ASCII
    }
}

& $deployExe $OutputPath

# Ensure SummerCoop-Launcher folder stays updated
$standaloneDir = Join-Path $root 'SummerCoop-Launcher'
$standalonePath = Join-Path $standaloneDir 'SummerCoop-Launcher.exe'
if ($OutputPath -ne $standalonePath) {
    & $deployExe $standalonePath
}

# Keep the dist backup updated as well.
$distDir = Join-Path $root 'dist'
$distPath = Join-Path $distDir 'SummerCoop-Launcher.exe'
if ($OutputPath -ne $distPath) {
    & $deployExe $distPath
}

Write-Output "Successfully built native launcher: $OutputPath"
