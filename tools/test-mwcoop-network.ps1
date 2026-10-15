param([switch]$NoSockets)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'build\mwcoop-network-tests'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$exe = Join-Path $dir 'MWCoopNetworkTests.exe'
$sources = @('vendor\MWCoop\src\MWCoop\Net\Transport.cs','vendor\MWCoop\src\MWCoop\Net\Codec.cs','vendor\MWCoop\src\MWCoop\Net\VehicleSnapshot.cs','vendor\MWCoop\src\MWCoop\Net\SupportPose.cs','tests\MSC\MscAdapterTests.cs','tests\MSC\UnityValueTypes.cs') | ForEach-Object { Join-Path $root $_ }
& $compiler /nologo /langversion:5 /define:MWCOOP_NETWORK_ONLY /target:exe ('/out:' + $exe) $sources
if ($LASTEXITCODE) { throw 'MWCoop network test build failed' }
$testArgs = @(); if ($NoSockets) { $testArgs += '--no-sockets' }
& $exe $testArgs | Tee-Object -FilePath (Join-Path $dir 'test-results.txt')
if ($LASTEXITCODE) { throw 'MWCoop network tests failed' }
