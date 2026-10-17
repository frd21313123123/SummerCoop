param(
    [string]$PackagePath = '',
    [string]$OutputPath = '',
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (!$Version) {
    $modelFile = Join-Path $root 'src\Core\Model.cs'
    if (Test-Path -LiteralPath $modelFile) {
        $model = Get-Content -LiteralPath $modelFile -Raw
        if ($model -match 'ModVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)"') {
            $Version = $Matches[1]
        }
    }
    if (!$Version) { $Version = '0.4.1' }
}

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
while ($parts.Count -lt 3) { $parts += '0' }
$mwvNum = "$($parts[0]),$($parts[1]),$($parts[2]),0"
$versionHeader = @"
#define MWV_NUM $mwvNum
#define MWV_STR "$Version"
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

$batch = @"
@echo off
call "$vcvars" >nul || exit /b 1
cd /d "$launcherDir"
rc /nologo /i "$buildDir" /i "$launcherDir" /fo "$resFile" launcher.rc || exit /b 1
cl /nologo /O2 /MT /W3 /EHsc /utf-8 /std:c++17 /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS /Fo"$buildDir\\" launcher.cpp "$resFile" /Fe"$OutputPath" /link /SUBSYSTEM:WINDOWS user32.lib gdi32.lib gdiplus.lib winhttp.lib comdlg32.lib shell32.lib ole32.lib advapi32.lib shlwapi.lib ws2_32.lib winmm.lib oleaut32.lib uuid.lib delayimp.lib /DELAYLOAD:winhttp.dll /DELAYLOAD:winmm.dll || exit /b 1
"@

$batch | Set-Content -LiteralPath $cmdScript -Encoding ascii
& $env:ComSpec /d /c $cmdScript
if ($LASTEXITCODE -ne 0) {
    throw "Native launcher compilation failed with exit code $LASTEXITCODE"
}

$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
("$hash  " + [IO.Path]::GetFileName($OutputPath)) | Set-Content -LiteralPath ($OutputPath + '.sha256') -Encoding ASCII

# Ensure SummerCoop-Launcher folder stays updated
$standaloneDir = Join-Path $root 'SummerCoop-Launcher'
$standalonePath = Join-Path $standaloneDir 'SummerCoop-Launcher.exe'
if ($OutputPath -ne $standalonePath) {
    New-Item -ItemType Directory -Path $standaloneDir -Force | Out-Null
    try {
        Copy-Item -LiteralPath $OutputPath -Destination $standalonePath -Force -ErrorAction Stop
        ("$hash  SummerCoop-Launcher.exe") | Set-Content -LiteralPath ($standalonePath + '.sha256') -Encoding ASCII
    } catch {}
}

# Keep dist alias updated when dist exists
$distDir = Join-Path $root 'dist'
$distPath = Join-Path $distDir 'SummerCoop-Launcher.exe'
if ($OutputPath -ne $distPath -and (Test-Path -LiteralPath $distDir)) {
    try {
        Copy-Item -LiteralPath $OutputPath -Destination $distPath -Force -ErrorAction Stop
        ("$hash  SummerCoop-Launcher.exe") | Set-Content -LiteralPath ($distPath + '.sha256') -Encoding ASCII
    } catch {}
}

Write-Output "Successfully built native launcher: $OutputPath"
