param([Parameter(Mandatory=$true)][string]$GamePath, [switch]$Uninstall, [string]$ProfilePath = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car\SummerCoop\world'))
$ErrorActionPreference = 'Stop'
$gameDir = (Resolve-Path -LiteralPath $GamePath).Path
if (!(Test-Path -LiteralPath (Join-Path $gameDir 'mysummercar.exe'))) { throw 'This is not a My Summer Car directory.' }
if (!(Test-Path -LiteralPath (Join-Path $gameDir 'mysummercar_Data\Managed\MSCLoader.dll'))) { throw 'Install MSCLoader first.' }
if (Get-Process mysummercar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $gameDir 'mysummercar.exe') }) { throw 'Close this game instance first.' }
$source = Join-Path $PSScriptRoot 'Mods'
$profileBackup = $null
$dllBackup = $null
if (!$Uninstall) {
    foreach ($relative in @('SummerCoop.dll','References\SummerCoop.Core.dll','References\SummerCoop.ProfileUpdate.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $source $relative))) { throw 'Run the installer from the complete packaged release directory.' }
    }
    if (Test-Path -LiteralPath (Join-Path $ProfilePath 'world.scw')) {
        $oldMod = Join-Path $gameDir 'Mods\SummerCoop.dll'
        $oldCore = Join-Path $gameDir 'Mods\References\SummerCoop.Core.dll'
        if (!(Test-Path -LiteralPath $oldMod) -or !(Test-Path -LiteralPath $oldCore)) { throw 'Existing host world needs its previous DLLs for a verified update. Restore them from backup first.' }
        $backup = Join-Path $gameDir ('SummerCoop-disabled-mods\update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory -Path $backup | Out-Null
        Copy-Item -LiteralPath $oldMod -Destination (Join-Path $backup 'SummerCoop.dll')
        Copy-Item -LiteralPath $oldCore -Destination (Join-Path $backup 'SummerCoop.Core.dll')
        $dllBackup = $backup
        $migrationOutput = @(& (Join-Path $source 'References\SummerCoop.ProfileUpdate.exe') $gameDir $ProfilePath $oldMod $oldCore $source --same-schema-v2)
        if ($LASTEXITCODE) { throw 'Profile update was refused; the installed DLLs were not changed.' }
        $migrationOutput | Write-Output
        foreach ($line in $migrationOutput) {
            if ($line.StartsWith('Backup: ')) {
                $candidateBackup = [IO.Path]::GetFullPath($line.Substring(8))
                $resolvedProfile = [IO.Path]::GetFullPath($ProfilePath).TrimEnd('\') + '\'
                if (!$candidateBackup.StartsWith($resolvedProfile, [StringComparison]::OrdinalIgnoreCase)) { throw 'Profile backup path is outside the selected profile.' }
                $profileBackup = $candidateBackup
            }
        }
        Write-Output ('Previous DLLs: ' + $backup)
    }
}
try { foreach ($relative in @('SummerCoop.dll','References\SummerCoop.Core.dll')) {
    $destination = Join-Path (Join-Path $gameDir 'Mods') $relative
    if ($Uninstall) {
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
    } else {
        $file = Join-Path $source $relative
        if (!(Test-Path -LiteralPath $file)) { throw 'Run the installer from the packaged release directory.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination ($destination + '.before-summercoop') -Force }
        Copy-Item -LiteralPath $file -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) { throw 'Installed checksum mismatch.' }
    }
} } catch {
    if ($dllBackup) {
        Copy-Item -LiteralPath (Join-Path $dllBackup 'SummerCoop.dll') -Destination (Join-Path $gameDir 'Mods\SummerCoop.dll') -Force
        Copy-Item -LiteralPath (Join-Path $dllBackup 'SummerCoop.Core.dll') -Destination (Join-Path $gameDir 'Mods\References\SummerCoop.Core.dll') -Force
    }
    if ($profileBackup) {
        foreach ($name in @('world.scw','world.scw.bak','world.scj','world.scj.bak')) {
            $savedFile = Join-Path $profileBackup $name
            if (Test-Path -LiteralPath $savedFile) { Copy-Item -LiteralPath $savedFile -Destination (Join-Path $ProfilePath $name) -Force }
        }
    }
    throw
}
Write-Output 'SummerCoop files updated. Original single-player saves were not modified.'
