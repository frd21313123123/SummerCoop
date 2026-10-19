param(
    [string[]]$Paths = @(),
    [string]$ReportPath = '',
    [switch]$UpdateSignatures
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Paths.Count) {
    $Paths = @(
        (Join-Path $root 'SummerCoop-Launcher\SummerCoop-Launcher.exe'),
        (Join-Path $root 'build\SummerCoop.dll'),
        (Join-Path $root 'build\SummerCoop.Core.dll')
    )
}
if (!$ReportPath) { $ReportPath = Join-Path $root 'build\defender-tests\report.json' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null

# Inspect the actual published files. Never rebuild, execute a scanned payload,
# change Defender settings, restore quarantine, or add exclusions here.
$platform = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
$scanner = Get-ChildItem -LiteralPath $platform -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+-\d+$' } |
    Sort-Object { [version]($_.Name.Split('-')[0]) } -Descending |
    ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$scanner) { $scanner = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe' }
$report = [ordered]@{
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    scanner = $scanner
    status = 'incomplete'
    signatureUpdate = $null
    defender = $null
    results = @()
    error = $null
}
try {
    if (!(Test-Path -LiteralPath $scanner)) { throw 'Microsoft Defender scanner is unavailable. No scan was performed.' }
    if ($UpdateSignatures) {
        $output = @(& $scanner -SignatureUpdate 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
        $report.signatureUpdate = @{ exitCode = $code; output = $output }
        if ($code -ne 0) { throw "Defender signature update failed ($code)." }
    }
    try {
        $status = Get-CimInstance -Namespace 'root/Microsoft/Windows/Defender' -ClassName MSFT_MpComputerStatus
        $report.defender = $status | Select-Object AMProductVersion, AMEngineVersion, AntivirusSignatureVersion,
            AntivirusSignatureLastUpdated, AntivirusEnabled, RealTimeProtectionEnabled
    } catch {
        # Scanning itself is authoritative; metadata can be inaccessible under
        # restricted accounts. Record that limitation rather than invent values.
        $report.defender = @{ metadataError = $_.Exception.Message }
    }
    if ($report.defender.AntivirusEnabled -eq $false) { throw 'Microsoft Defender Antivirus is disabled.' }
    foreach ($path in $Paths) {
        $full = [IO.Path]::GetFullPath($path)
        $result = [ordered]@{ path = $full; sha256 = $null; version = $null; signature = $null; exitCode = $null; status = 'error'; output = @() }
        try {
            $file = Get-Item -LiteralPath $full
            if ($file.PSIsContainer) { throw 'Specify individual EXE/DLL files so every scanned file has a recorded hash.' }
            $result.sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
            $result.version = $file.VersionInfo.ProductVersion
            $result.signature = [string](Get-AuthenticodeSignature -LiteralPath $full).Status
            # Custom scan without remediation: ignores exclusions and reports
            # detections on stdout; does not quarantine or delete the target.
            $result.output = @(& $scanner -Scan -ScanType 3 -File $full -DisableRemediation 2>&1 | ForEach-Object { "$_" })
            $result.exitCode = $LASTEXITCODE
            if ($result.exitCode -eq 0) {
                $after = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($after -ne $result.sha256) { throw 'File changed during scanning.' }
                $result.status = 'clean'
            } else { $result.status = 'detection-or-scan-error' }
        } catch { $result.output += $_.Exception.Message }
        $report.results += $result
        Write-Output ("{0}: {1}" -f $result.status, $full)
        $result.output | Write-Output
    }
    if (@($report.results | Where-Object status -ne 'clean').Count) {
        throw 'Defender did not confirm every file clean. Inspect the report; detection and scan failures both block release.'
    }
    $report.status = 'clean'
} catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    throw
} finally {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Output "Defender report: $ReportPath"
}
