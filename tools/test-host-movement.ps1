param([string]$Run = ('host-walk-' + (Get-Date -Format 'yyyyMMdd-HHmmss')), [int]$Port = 27982, [string]$ProfileSource, [string]$OldPairPath, [string]$LoaderSourceGame)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localRoot = [IO.Path]::GetFullPath((Join-Path $root '.local'))
if ($Run -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$' -or $Port -lt 1024 -or $Port -gt 65535) { throw 'Invalid test name or port' }
if (!$ProfileSource) { $ProfileSource = Join-Path $localRoot 'setup-015-final-real-after' }
if (!(Test-Path -LiteralPath (Join-Path $ProfileSource 'world.scw'))) { throw 'A compatible copied host profile is required' }
$shared = [IO.Path]::GetFullPath((Join-Path $localRoot $Run))
if (Test-Path -LiteralPath $shared) { throw 'Choose a fresh test name' }
$roles = @(@{Name='host'; Game=(Join-Path $localRoot 'game')}, @{Name='client'; Game=(Join-Path $localRoot 'game-client')})
foreach ($role in $roles) {
    $role.Game = [IO.Path]::GetFullPath($role.Game)
    if (!$role.Game.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture path escaped .local' }
    if (!(Test-Path -LiteralPath (Join-Path $role.Game 'mysummercar.exe'))) { throw 'Existing isolated game copies are required' }
    if (Get-CimInstance Win32_Process -Filter "name='mysummercar.exe'" | Where-Object ExecutablePath -eq (Join-Path $role.Game 'mysummercar.exe')) { throw 'An isolated fixture is already in use; it was not stopped' }
    $role.Profile = Join-Path $localRoot ($Run + '-' + $role.Name)
    if (Test-Path -LiteralPath $role.Profile) { throw 'Test profile already exists' }
}
New-Item -ItemType Directory -Path $shared | Out-Null
$build = Join-Path $root 'build\runtime-tests'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$managed = Join-Path $roles[0].Game 'mysummercar_Data\Managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$refs = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','PlayMaker.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','Assembly-UnityScript-firstpass.dll','Boo.Lang.dll','MSCLoader.dll','0Harmony.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$harness = Join-Path $build 'HostMovementHarness.dll'
& $compiler /nologo /noconfig /nostdlib+ /langversion:5 /target:library /optimize+ ('/out:' + $harness) $refs ('/reference:' + (Join-Path $root 'build\SummerCoop.dll')) ('/reference:' + (Join-Path $root 'build\SummerCoop.Core.dll')) (Join-Path $root 'tests\Runtime\HostMovementHarness.cs')
if ($LASTEXITCODE) { throw 'Movement harness compilation failed' }
if ($OldPairPath) {
    Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination $build -Force
    & $compiler /nologo /langversion:5 /target:exe ('/out:' + (Join-Path $build 'SummerCoop.ProfileUpdate.exe')) ('/reference:' + (Join-Path $root 'build\SummerCoop.Core.dll')) (Join-Path $root 'tools\MigrateProfile.cs')
    if ($LASTEXITCODE) { throw 'Fixture migration helper compilation failed' }
}
$expected = @{}
foreach ($name in @('SummerCoop.dll','SummerCoop.Core.dll')) { $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $root ('build\' + $name))).Hash }
$originalPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'
$original = @{}
Get-ChildItem -LiteralPath $originalPath -File | ForEach-Object { $original[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
$processes = @()
function Start-Fixture($role) {
    $connection = if ($role.Name -eq 'host') { '--summercoop-host-lan ' + $Port } else { '--summercoop-join-lan 127.0.0.1 --summercoop-port ' + $Port }
    $log = Join-Path $shared ($role.Name + '-game.log')
    $report = Join-Path $shared ($role.Name + '-integration.txt')
    $args = '-screen-fullscreen 0 -screen-width 640 -screen-height 480 -logFile "' + $log + '" ' + $connection + ' --summercoop-profile "' + $role.Profile + '" --summercoop-test-report "' + $report + '" --summercoop-test-pair yes --summercoop-test-duration 180 --summercoop-test-fps-limit 60 --host-movement-test yes --host-movement-shared "' + $shared + '"'
    $process = Start-Process -FilePath (Join-Path $role.Game 'mysummercar.exe') -WorkingDirectory $role.Game -ArgumentList $args -WindowStyle Hidden -PassThru
    Write-Host ($role.Name + ': isolated game started, PID ' + $process.Id)
    return $process
}
try {
    foreach ($role in $roles) {
        if ($LoaderSourceGame) {
            foreach ($file in @('MSCLoader.dll','MSCLoader.Preloader.dll','0Harmony.dll')) {
                Copy-Item -LiteralPath (Join-Path $LoaderSourceGame ('mysummercar_Data\Managed\' + $file)) -Destination (Join-Path $role.Game ('mysummercar_Data\Managed\' + $file)) -Force
            }
        }
        New-Item -ItemType Directory -Path $role.Profile | Out-Null
        if ($role.Name -eq 'host') {
            $world = Join-Path $role.Profile 'world'
            New-Item -ItemType Directory -Path $world | Out-Null
            Get-ChildItem -LiteralPath $ProfileSource -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $world }
            if ($OldPairPath) {
                & (Join-Path $build 'SummerCoop.ProfileUpdate.exe') $role.Game $world (Join-Path $OldPairPath 'SummerCoop.dll') (Join-Path $OldPairPath 'SummerCoop.Core.dll') (Join-Path $root 'build') --same-schema-v2
                if ($LASTEXITCODE) { throw 'Copied host profile migration failed' }
            }
        }
        Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.dll') -Destination (Join-Path $role.Game 'Mods\SummerCoop.dll') -Force
        Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination (Join-Path $role.Game 'Mods\References\SummerCoop.Core.dll') -Force
        Copy-Item -LiteralPath $harness -Destination (Join-Path $role.Game 'Mods\HostMovementHarness.dll') -Force
    }
    $hostProcess = Start-Fixture $roles[0]
    $processes += $hostProcess | Where-Object { $_ -is [Diagnostics.Process] }
    $hostProcess = $processes[0]
    $limit = (Get-Date).AddSeconds(180)
    while (!(Test-Path -LiteralPath (Join-Path $shared 'baseline-ready'))) {
        $hostProcess.Refresh()
        if ($hostProcess.HasExited -or (Test-Path -LiteralPath (Join-Path $shared 'host-result'))) { throw 'Host did not pass the pre-join movement baseline; inspect its proof and game log' }
        if ((Get-Date) -gt $limit) { throw 'Host baseline timed out' }
        Start-Sleep -Milliseconds 500
    }
    Write-Output 'Native host movement before guest: PASS; starting actual game client'
    $clientOutput = Start-Fixture $roles[1]
    $processes += $clientOutput | Where-Object { $_ -is [Diagnostics.Process] }
    $limit = (Get-Date).AddSeconds(180)
    while (!(Test-Path -LiteralPath (Join-Path $shared 'host-result')) -or !(Test-Path -LiteralPath (Join-Path $shared 'client-result'))) {
        foreach ($process in $processes) { $process.Refresh(); if ($process.HasExited) { throw 'A fixture exited before the complete movement proof' } }
        if ((Get-Date) -gt $limit) { throw 'Guest/host movement scenario timed out' }
        Start-Sleep -Milliseconds 500
    }
    foreach ($name in @('host-result','client-result')) { if ((Get-Content -LiteralPath (Join-Path $shared $name) -Raw).Trim() -ne 'PASS') { throw ($name + ' failed') } }
    foreach ($role in $roles) {
        Get-Content -LiteralPath (Join-Path $role.Profile 'host-movement-proof.txt')
        foreach ($name in $expected.Keys) {
            $file = Join-Path $role.Game $(if ($name -eq 'SummerCoop.dll') { 'Mods\SummerCoop.dll' } else { 'Mods\References\SummerCoop.Core.dll' })
            if ((Get-FileHash -LiteralPath $file).Hash -ne $expected[$name]) { throw 'Production DLL was changed by the test' }
        }
    }
    Write-Output 'HOST_MOVEMENT_WITH_LOADED_REAL_GAME_CLIENT=PASS'
}
finally {
    foreach ($process in $processes) {
        $process.Refresh()
        if (!$process.HasExited -and !$process.WaitForExit(20000)) {
            # Only PIDs created by this script, and only inside the verified fixtures.
            $process.Refresh()
            if (!$process.HasExited -and ($roles | Where-Object { $process.MainModule.FileName -eq (Join-Path $_.Game 'mysummercar.exe') })) { Stop-Process -Id $process.Id; $process.WaitForExit() }
        }
    }
    foreach ($role in $roles) {
        $testDll = [IO.Path]::GetFullPath((Join-Path $role.Game 'Mods\HostMovementHarness.dll'))
        if (!$testDll.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Harness cleanup escaped workspace fixtures' }
        if (Test-Path -LiteralPath $testDll) { Remove-Item -LiteralPath $testDll }
    }
    foreach ($file in $original.Keys) { if ((Get-FileHash -LiteralPath $file).Hash -ne $original[$file]) { throw 'An original single-player save changed during the test' } }
    Write-Output 'ORIGINAL_SINGLEPLAYER_FILES=UNCHANGED; only own fixture processes closed'
}
