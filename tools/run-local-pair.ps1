param([string]$Run = ('pair-' + (Get-Date -Format 'yyyyMMdd-HHmmss')), [int]$Port = 27942, [switch]$Actions, [int]$Duration = 20, [switch]$MeshProof, [int]$Width = 640, [int]$Height = 480, [int]$FrameLimit = -1, [switch]$HostOnly, [switch]$NativeBaseline, [switch]$ImportSave, [switch]$AllocationProbe)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localDir = Join-Path $root '.local'
if ($Run -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$') { throw 'Run must be a simple folder name.' }
foreach ($role in @('host','client')) { if (Test-Path -LiteralPath (Join-Path $localDir ($Run + '-' + $role))) { throw 'Choose a fresh Run name: a previous profile already exists.' } }
if ($Width -lt 640 -or $Height -lt 480 -or $Width -gt 7680 -or $Height -gt 4320) { throw 'Invalid test resolution.' }
if ($NativeBaseline -and (!$HostOnly -or $Actions -or $MeshProof)) { throw 'NativeBaseline requires HostOnly and cannot run multiplayer actions.' }
$testRoles = if ($HostOnly) { @('host') } else { @('host','client') }
foreach ($role in $testRoles) {
    $folder = if ($role -eq 'host') { 'game' } else { 'game-client' }
    $gameDir = Join-Path $localDir $folder
    $exe = Join-Path $gameDir 'mysummercar.exe'
    $pidFile = Join-Path $localDir ('pair-' + $role + '.pid')
    if (Test-Path -LiteralPath $pidFile) {
        $gameProcess = Get-Process -Id ([int](Get-Content -LiteralPath $pidFile)) -ErrorAction SilentlyContinue
        if ($gameProcess -and $gameProcess.Path -eq $exe) { Stop-Process -Id $gameProcess.Id; $gameProcess.WaitForExit() }
    }
    Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.dll') -Destination (Join-Path $gameDir 'Mods\SummerCoop.dll') -Force
    Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination (Join-Path $gameDir 'Mods\References\SummerCoop.Core.dll') -Force
    $profile = Join-Path $localDir ($Run + '-' + $role)
    $report = Join-Path $localDir ($Run + '-' + $role + '-report.txt')
    $log = Join-Path $gameDir ($Run + '-' + $role + '-log.txt')
    $connection = if ($role -eq 'host') { '--summercoop-host-lan ' + $Port } else { '--summercoop-join-lan 127.0.0.1 --summercoop-port ' + $Port }
    $arguments = '-screen-fullscreen 0 -screen-width ' + $Width + ' -screen-height ' + $Height + ' -logFile "' + $log + '" ' + $connection + ' --summercoop-profile "' + $profile + '" --summercoop-test-report "' + $report + '" --summercoop-test-pair ' + $(if ($HostOnly) { 'no' } else { 'yes' })
    if ($FrameLimit -gt 0) { $arguments += ' --summercoop-test-fps-limit ' + $FrameLimit }
    if ($NativeBaseline) { $arguments += ' --summercoop-test-native-only yes' }
    if ($ImportSave -and $role -eq 'host') { $arguments += ' --summercoop-test-import-save yes' }
    if ($AllocationProbe) { $arguments += ' --summercoop-test-allocations yes' }
    if ($Actions) { $arguments += ' --summercoop-test-actions yes' }
    $arguments += ' --summercoop-test-duration ' + $Duration
    if ($MeshProof) { $arguments += ' --summercoop-test-mesh-proof "' + (Join-Path $localDir ($Run + '-mesh-proof.bin')) + '"' }
    $gameProcess = Start-Process -FilePath $exe -WorkingDirectory $gameDir -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $gameProcess.Id | Set-Content -LiteralPath $pidFile
    Write-Output ($role + ' PID ' + $gameProcess.Id + ' profile ' + $profile)
    if ($role -eq 'host' -and !$NativeBaseline) {
        $listenDeadline = (Get-Date).AddSeconds(300)
        do {
            $gameProcess.Refresh()
            if ($gameProcess.HasExited) { throw 'Test host exited before opening its port.' }
            $listenerReady = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $gameProcess.Id).Count -gt 0
            if (!$listenerReady) { Start-Sleep -Milliseconds 500 }
        } while (!$listenerReady -and (Get-Date) -lt $listenDeadline)
        if (!$listenerReady) { throw 'Test host has not opened its port; inspect its log before retrying.' }
    }
}
