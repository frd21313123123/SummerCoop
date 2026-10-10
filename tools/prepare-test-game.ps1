param([string]$GamePath = 'E:\SteamLibrary\steamapps\common\My Summer Car', [ValidatePattern('^game[a-z0-9-]*$')][string]$Name = 'game')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path (Join-Path $root '.local') $Name
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($fileName in @('mysummercar.exe','steam_api64.dll','steam_api.dll','winhttp.dll')) {
    Copy-Item -LiteralPath (Join-Path $GamePath $fileName) -Destination (Join-Path $destination $fileName) -Force
}
$dataDestination = Join-Path $destination 'mysummercar_Data'
New-Item -ItemType Directory -Path $dataDestination -Force | Out-Null
foreach ($directory in @('Managed','Mono','Plugins','Resources')) {
    Copy-Item -LiteralPath (Join-Path $GamePath ('mysummercar_Data\' + $directory)) -Destination $dataDestination -Recurse -Force
}
Get-ChildItem -LiteralPath (Join-Path $GamePath 'mysummercar_Data') -File | Where-Object { $_.Name -notmatch 'BACKUP' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dataDestination $_.Name) -Force
}
$mods = Join-Path $destination 'Mods'
New-Item -ItemType Directory -Path (Join-Path $mods 'References'),(Join-Path $mods 'Assets') -Force | Out-Null
foreach ($asset in @('MSCLoader_Console','MSCLoader_Settings')) {
    Copy-Item -LiteralPath (Join-Path $GamePath ('Mods\Assets\' + $asset)) -Destination (Join-Path $mods 'Assets') -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.dll') -Destination $mods -Force
Copy-Item -LiteralPath (Join-Path $root 'build\SummerCoop.Core.dll') -Destination (Join-Path $mods 'References') -Force
@'
[General]
enabled = true
target_assembly = mysummercar_Data\Managed\MSCLoader.Preloader.dll
redirect_output_log = true
ignore_disable_switch = true
[MSCLoader]
mods = GF
skipIntro = true
skipConfigScreen = true
'@ | Set-Content -LiteralPath (Join-Path $destination 'doorstop_config.ini') -Encoding ascii
'516750' | Set-Content -LiteralPath (Join-Path $destination 'steam_appid.txt') -Encoding ascii
Write-Output $destination
