param(
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,48}$')][string]$Run,
    [ValidatePattern('^game[a-z0-9-]*$')][string]$Game = 'game-assembly-host',
    [ValidateRange(10,3600)][int]$Duration = 600, [ValidateRange(0,600)][int]$Warmup = 60,
    [ValidateRange(320,7680)][int]$Width = 2560, [ValidateRange(240,4320)][int]$Height = 1440,
    [Parameter(Mandatory=$true)][string]$SeedProfile
)
$ErrorActionPreference = 'Stop'
if (!$Run) { $Run = 'vanilla-' + (Get-Date -Format yyyyMMdd-HHmmss) }
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root '.local'
$gameDir = (Resolve-Path -LiteralPath (Join-Path $local $Game)).Path
if (!$gameDir.StartsWith(([IO.Path]::GetFullPath($local) + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Baseline game must be an isolated .local copy.' }
$exe = Join-Path $gameDir 'mysummercar.exe'
if (@(Get-Process mysummercar -ErrorAction SilentlyContinue | Where-Object Path -eq $exe).Count) { throw 'Isolated game copy is already running.' }
$profile = Join-Path $local $Run
if (Test-Path -LiteralPath $profile) { throw 'Use a fresh run name.' }
$managed = Join-Path $gameDir 'mysummercar_Data\Managed'
$build = Join-Path $root 'build\passive-probe'
New-Item -ItemType Directory -Path $build,$profile -Force | Out-Null
Get-ChildItem -LiteralPath $SeedProfile -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $profile }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$refs = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','MSCLoader.dll','ES2.dll','PlayMaker.dll','0Harmony.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$probe = Join-Path $build 'SummerCoopPerformanceProbe.dll'
& $compiler /nologo /noconfig /nostdlib+ /langversion:5 /target:library /optimize+ ('/out:' + $probe) $refs (Join-Path $PSScriptRoot 'StandaloneFrameProbe.cs') (Join-Path $root 'src\Core\FrameMeasurements.cs')
if ($LASTEXITCODE) { throw 'Passive probe compilation failed.' }
$mods = Join-Path $gameDir 'Mods'
$otherMods = @(Get-ChildItem -LiteralPath $mods -Filter '*.dll' -File | Where-Object Name -ne 'SummerCoop.dll')
if ($otherMods.Count) { throw ('Unexpected mods in isolated baseline: ' + ($otherMods.Name -join ', ')) }
$originalMod = Join-Path $mods 'SummerCoop.dll'
$backup = Join-Path $profile 'disabled-SummerCoop.dll'
$installedProbe = Join-Path $mods 'SummerCoopPerformanceProbe.dll'
$pidFile = Join-Path $local ($Run + '.pid')
$report = Join-Path $local ($Run + '-report.txt')
$arguments = '-nosound -screen-fullscreen 0 -screen-width ' + $Width + ' -screen-height ' + $Height + ' -logFile "' + (Join-Path $local ($Run + '-game.log')) + '" --summercoop-profile "' + $profile + '" --summercoop-test-report "' + $report + '" --summercoop-test-duration ' + $Duration + ' --summercoop-test-warmup ' + $Warmup
$helper = Join-Path $root 'build\TestDesktopHost.exe'
& $compiler /nologo /target:winexe ('/out:' + $helper) (Join-Path $PSScriptRoot 'TestDesktopHost.cs')
if ($LASTEXITCODE) { throw 'Desktop helper compilation failed.' }
try {
    if (Test-Path -LiteralPath $originalMod) { Move-Item -LiteralPath $originalMod -Destination $backup }
    Copy-Item -LiteralPath $probe -Destination $installedProbe
    $encoded = @($exe,$arguments,$gameDir,('SummerCoopBaseline-' + $Run),$pidFile,[string]($Duration + $Warmup + 660)) | ForEach-Object { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($_)) }
    $supervisor = Start-Process -FilePath $helper -WorkingDirectory $root -ArgumentList $encoded -WindowStyle Hidden -PassThru
    while (!$supervisor.HasExited) { Start-Sleep -Seconds 2; $supervisor.Refresh() }
    if (!(Test-Path -LiteralPath $report)) { throw ('Baseline report missing; supervisor exit ' + $supervisor.ExitCode) }
    Get-Content -LiteralPath $report
} finally {
    if (Test-Path -LiteralPath $pidFile) {
        $gameProcess = Get-Process -Id ([int](Get-Content -LiteralPath $pidFile)) -ErrorAction SilentlyContinue
        if ($gameProcess -and $gameProcess.Path -eq $exe) { Stop-Process -Id $gameProcess.Id -Force; $gameProcess.WaitForExit(10000) | Out-Null }
    }
    if (Test-Path -LiteralPath $installedProbe) { Remove-Item -LiteralPath $installedProbe }
    if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $originalMod }
}
