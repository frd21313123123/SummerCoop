param([string]$Run = 'pair3', [int]$Port = 27942, [switch]$Actions, [int]$Duration = 20, [switch]$MeshProof)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localDir = Join-Path $root '.local'
foreach ($role in @('host','client')) {
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
    $arguments = '-screen-fullscreen 0 -screen-width 640 -screen-height 480 -logFile "' + $log + '" ' + $connection + ' --summercoop-profile "' + $profile + '" --summercoop-test-report "' + $report + '" --summercoop-test-pair yes'
    if ($Actions) { $arguments += ' --summercoop-test-actions yes' }
    $arguments += ' --summercoop-test-duration ' + $Duration
    if ($MeshProof) { $arguments += ' --summercoop-test-mesh-proof "' + (Join-Path $localDir ($Run + '-mesh-proof.bin')) + '"' }
    $gameProcess = Start-Process -FilePath $exe -WorkingDirectory $gameDir -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $gameProcess.Id | Set-Content -LiteralPath $pidFile
    Write-Output ($role + ' PID ' + $gameProcess.Id + ' profile ' + $profile)
    if ($role -eq 'host') { Start-Sleep -Seconds 3 }
}
