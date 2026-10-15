$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'build\installer-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is missing.' }
# The launcher embeds this shared installation engine; the interface, the
# automatic update before a launch and the --install command all use it.
$testExe = Join-Path $testDir 'SummerCoop.InstallerTests.exe'
& $compiler /nologo /langversion:5 /target:exe ('/out:' + $testExe) (Join-Path $root 'tools\Installer\InstallerCore.cs') (Join-Path $root 'tests\Installer\InstallerTests.cs')
if ($LASTEXITCODE) { throw 'Installer tests build failed.' }
& $testExe | Tee-Object -FilePath (Join-Path $testDir 'test-results.txt')
if ($LASTEXITCODE) { throw 'Installer tests failed.' }
