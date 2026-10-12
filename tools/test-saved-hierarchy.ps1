param([Parameter(Mandatory=$true)][string]$ProfileDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = [IO.Path]::GetFullPath($ProfileDirectory)
if (!(Test-Path -LiteralPath (Join-Path $source 'world.scw'))) { throw 'Profile must contain world.scw.' }
$testRoot = Join-Path $root 'build\saved-hierarchy'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$run = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
$copy = Join-Path $run 'source'
$output = Join-Path $run 'repaired'
New-Item -ItemType Directory -Path $copy -Force | Out-Null
$hashes = @{}
foreach ($file in Get-ChildItem -LiteralPath $source -File | Where-Object Name -Like 'world.sc*') {
    $hashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash
    Copy-Item -LiteralPath $file.FullName -Destination $copy
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$exe = Join-Path $testRoot 'TestSavedHierarchy.exe'
Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination $testRoot -Force
& $compiler /nologo /langversion:5 /target:exe ('/out:' + $exe) ('/reference:' + (Join-Path $testRoot 'SummerCoop.Core.dll')) (Join-Path $PSScriptRoot 'TestSavedHierarchy.cs')
if ($LASTEXITCODE) { throw 'Saved hierarchy verifier compilation failed.' }
& $exe $copy $output | Tee-Object -FilePath (Join-Path $run 'results.txt')
if ($LASTEXITCODE) { throw 'Saved hierarchy verification failed.' }
foreach ($file in $hashes.Keys) {
    if ((Get-FileHash -LiteralPath $file).Hash -ne $hashes[$file]) { throw ('Source checkpoint changed: ' + $file) }
}
Write-Output 'PASS source checkpoint files were left byte-identical; game was not started'
Write-Output ('Results: ' + (Join-Path $run 'results.txt'))
