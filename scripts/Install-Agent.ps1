#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install or update the CloudSmith Agent Windows Service.

.DESCRIPTION
    Downloads the self-contained CloudSmith Agent release zip from GitHub Releases,
    extracts it to C:\Program Files\CloudSmith\Agent, registers it as a Windows
    Service, and sets the required environment variables in the service registry key.

    The Agent service runs as LocalSystem, which has Hyper-V Administrator rights
    by default on Server 2025.

.PARAMETER RelayUrl
    Required. HTTP URL of the Relay LAN listener. Example: http://192.168.1.5:8080

.PARAMETER EnrollmentToken
    Required. Shared secret matching RELAY_AGENT_ENROLLMENT_TOKEN on the Relay.

.PARAMETER ClusterId
    Optional. Cluster identifier to include in inventory pushes. Default: "default"

.PARAMETER ScanIntervalSeconds
    Optional. Inventory scan interval in seconds. Default: 300

.PARAMETER ReleaseVersion
    Optional. GitHub release tag to download (e.g. "v0.1.0"). Defaults to "latest".

.PARAMETER InstallDir
    Optional. Installation directory. Default: C:\Program Files\CloudSmith\Agent

.EXAMPLE
    .\Install-Agent.ps1 -RelayUrl http://192.168.1.5:8080 -EnrollmentToken my-secret-token
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RelayUrl,

    [Parameter(Mandatory)]
    [string] $EnrollmentToken,

    [string] $ClusterId = 'default',

    [int] $ScanIntervalSeconds = 300,

    [string] $ReleaseVersion = 'latest',

    [string] $InstallDir = 'C:\Program Files\CloudSmith\Agent'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'CloudSmithAgent'
$BinaryName  = 'CloudSmith.Runner.exe'
$BinaryPath  = Join-Path $InstallDir $BinaryName
$DataDir     = 'C:\ProgramData\CloudSmith'

# ---------------------------------------------------------------------------
# 1. Determine download URL
# ---------------------------------------------------------------------------
Write-Host "CloudSmith Agent installer — version: $ReleaseVersion"
$repo = 'cloudsmith-cloud/cloudsmith-agent'

if ($ReleaseVersion -eq 'latest') {
    $apiUrl = "https://api.github.com/repos/$repo/releases/latest"
    $release = Invoke-RestMethod -Uri $apiUrl -UseBasicParsing
    $ReleaseVersion = $release.tag_name
}

$assetName = "cloudsmith-agent-win-x64-$ReleaseVersion.zip"
$downloadUrl = "https://github.com/$repo/releases/download/$ReleaseVersion/$assetName"

# ---------------------------------------------------------------------------
# 2. Download and extract
# ---------------------------------------------------------------------------
Write-Host "Downloading $assetName from $downloadUrl"
$tmpZip = Join-Path $env:TEMP "cloudsmith-agent-$ReleaseVersion.zip"
Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpZip -UseBasicParsing

if (Test-Path $InstallDir) {
    Write-Host "Removing existing installation at $InstallDir"
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Remove-Item -Path $InstallDir -Recurse -Force
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $DataDir    -Force | Out-Null

Write-Host "Extracting to $InstallDir"
Expand-Archive -Path $tmpZip -DestinationPath $InstallDir -Force
Remove-Item $tmpZip

if (-not (Test-Path $BinaryPath)) {
    throw "Binary not found at $BinaryPath after extraction. Check release zip structure."
}

# ---------------------------------------------------------------------------
# 3. Register Windows Service
# ---------------------------------------------------------------------------
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existingSvc) {
    Write-Host "Service '$ServiceName' already exists — updating binary path"
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe config $ServiceName binPath= "`"$BinaryPath`""
} else {
    Write-Host "Creating Windows Service '$ServiceName'"
    New-Service `
        -Name        $ServiceName `
        -BinaryPathName $BinaryPath `
        -DisplayName 'CloudSmith Agent' `
        -Description 'CloudSmith Agent — per-host Hyper-V inventory and health monitoring.' `
        -StartupType Automatic
}

# ---------------------------------------------------------------------------
# 4. Set environment variables in the service registry key
# (sc.exe Environment is supported on Server 2019+ via registry injection)
# ---------------------------------------------------------------------------
Write-Host "Setting service environment variables"
$svcRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$envMultiSz = @(
    "AGENT_RELAY_URL=$RelayUrl",
    "AGENT_ENROLLMENT_TOKEN=$EnrollmentToken",
    "AGENT_CLUSTER_ID=$ClusterId",
    "AGENT_SCAN_INTERVAL_SECONDS=$ScanIntervalSeconds",
    "AGENT_IDENTITY_PATH=$DataDir\agent-identity.json"
)
Set-ItemProperty -Path $svcRegPath -Name 'Environment' -Value $envMultiSz -Type MultiString

# ---------------------------------------------------------------------------
# 5. Start the service
# ---------------------------------------------------------------------------
Write-Host "Starting $ServiceName"
Start-Service -Name $ServiceName

$svc = Get-Service -Name $ServiceName
Write-Host "Service status: $($svc.Status)"

if ($svc.Status -ne 'Running') {
    Write-Warning "Service did not reach Running state — check Event Viewer (Application log) for errors."
} else {
    Write-Host "CloudSmith Agent installed and running."
    Write-Host "  Relay URL:   $RelayUrl"
    Write-Host "  Cluster:     $ClusterId"
    Write-Host "  Identity:    $DataDir\agent-identity.json"
}
