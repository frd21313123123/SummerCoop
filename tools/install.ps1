param([Parameter(Mandatory=$true)][string]$GamePath, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$gameDir = (Resolve-Path -LiteralPath $GamePath).Path
if (!(Test-Path -LiteralPath (Join-Path $gameDir 'mysummercar.exe'))) { throw 'This is not a My Summer Car directory.' }
if (!(Test-Path -LiteralPath (Join-Path $gameDir 'mysummercar_Data\Managed\MSCLoader.dll'))) { throw 'Install MSCLoader first.' }
if (Get-Process mysummercar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $gameDir 'mysummercar.exe') }) { throw 'Close this game instance first.' }
$source = Join-Path $PSScriptRoot 'Mods'
foreach ($relative in @('SummerCoop.dll','References\SummerCoop.Core.dll')) {
    $destination = Join-Path (Join-Path $gameDir 'Mods') $relative
    if ($Uninstall) {
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
    } else {
        $file = Join-Path $source $relative
        if (!(Test-Path -LiteralPath $file)) { throw 'Run the installer from the packaged release directory.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination ($destination + '.before-summercoop') -Force }
        Copy-Item -LiteralPath $file -Destination $destination -Force
    }
}
Write-Output 'SummerCoop files updated. Saves were not modified.'
