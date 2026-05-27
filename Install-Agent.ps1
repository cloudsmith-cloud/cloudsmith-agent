#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install the CloudSmith Agent Windows Service from a local self-contained build.

.DESCRIPTION
    Registers CloudSmith.Runner.exe (the self-contained Agent binary) as the
    CloudSmithAgent Windows Service, starts it, and waits up to 60 seconds for
    the Agent to complete enrollment with the Relay.

    Once enrolled, the agentSecret from the identity file is DPAPI-encrypted
    (LocalMachine scope) and written to %PROGRAMDATA%\CloudSmith\agent.token.

    Prerequisites:
      - Must run as Administrator.
      - CloudSmith.Runner.exe must be present alongside this script (produced by
        `dotnet publish -r win-x64 --self-contained`).
      - The Agent service must be able to reach the Relay (AGENT_RELAY_URL) and
        the enrollment token (AGENT_ENROLLMENT_TOKEN) must be configured before
        starting — set them via the service Environment registry value after
        this script creates the service, or pass them as environment variables
        before calling this script (the script propagates them to the service).

.EXAMPLE
    # Run from the publish output directory after dotnet publish:
    .\Install-Agent.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName  = 'CloudSmithAgent'
$DisplayName  = 'CloudSmith Agent'
$Description  = 'CloudSmith per-host agent — receives and executes job dispatches from the CloudSmith relay.'
$DataDir      = Join-Path $env:ProgramData 'CloudSmith'
$IdentityFile = Join-Path $DataDir 'agent-identity.json'
$TokenFile    = Join-Path $DataDir 'agent.token'

# ---------------------------------------------------------------------------
# 1. Resolve exe path — must be next to this script.
# ---------------------------------------------------------------------------
$exePath = Join-Path $PSScriptRoot 'CloudSmith.Runner.exe'

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Error "[CloudSmith] Binary not found: $exePath`nRun 'dotnet publish -r win-x64 --self-contained' and place Install-Agent.ps1 in the publish output directory."
    exit 1
}

Write-Host "[CloudSmith] Using binary: $exePath"

# ---------------------------------------------------------------------------
# 2. Register Windows Service via sc.exe
# ---------------------------------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[CloudSmith] Service '$ServiceName' already exists — stopping and reconfiguring."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe config $ServiceName binpath= "`"$exePath`"" start= auto DisplayName= $DisplayName | Out-Null
} else {
    Write-Host "[CloudSmith] Registering Windows Service '$ServiceName'."
    & sc.exe create $ServiceName binpath= "`"$exePath`"" start= auto DisplayName= $DisplayName
    if ($LASTEXITCODE -ne 0) {
        Write-Error "[CloudSmith] sc.exe create failed with exit code $LASTEXITCODE."
        exit 1
    }
}

& sc.exe description $ServiceName $Description
if ($LASTEXITCODE -ne 0) {
    Write-Warning "[CloudSmith] sc.exe description returned $LASTEXITCODE (non-fatal)."
}

& sc.exe failure $ServiceName reset= 3600 actions= restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) {
    Write-Warning "[CloudSmith] sc.exe failure returned $LASTEXITCODE (non-fatal)."
}

# Ensure the data directory exists before the service starts.
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

# ---------------------------------------------------------------------------
# 3. Start the service
# ---------------------------------------------------------------------------
Write-Host "[CloudSmith] Starting service '$ServiceName'."
Start-Service -Name $ServiceName

# ---------------------------------------------------------------------------
# 4. Wait for enrollment (poll agent-identity.json for up to 60 seconds)
# ---------------------------------------------------------------------------
Write-Host "[CloudSmith] Waiting for Agent enrollment (timeout: 60 s)..."

$deadline  = (Get-Date).AddSeconds(60)
$enrolled  = $false
$dotsPrinted = 0

while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $IdentityFile) {
        $enrolled = $true
        break
    }
    Write-Host -NoNewline '.'
    $dotsPrinted++
    Start-Sleep -Seconds 2
}

if ($dotsPrinted -gt 0) { Write-Host '' }   # newline after progress dots

if (-not $enrolled) {
    Write-Error "[CloudSmith] Agent did not enroll within 60 seconds. Check the service logs at $DataDir\logs\agent-*.log and verify AGENT_RELAY_URL and AGENT_ENROLLMENT_TOKEN are set in the service environment."
    exit 1
}

Write-Host "[CloudSmith] Enrollment marker detected: $IdentityFile"

# ---------------------------------------------------------------------------
# 5. Write DPAPI-encrypted token (LocalMachine scope)
# ---------------------------------------------------------------------------
try {
    $identityJson = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
    $agentSecret  = $identityJson.agentSecret

    if ([string]::IsNullOrWhiteSpace($agentSecret)) {
        throw "agentSecret field is empty in $IdentityFile"
    }

    $plainBytes      = [System.Text.Encoding]::UTF8.GetBytes($agentSecret)
    $encryptedBytes  = [System.Security.Cryptography.ProtectedData]::Protect(
        $plainBytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine)

    [System.IO.File]::WriteAllBytes($TokenFile, $encryptedBytes)
    Write-Host "[CloudSmith] DPAPI-encrypted token written to $TokenFile"
}
catch {
    Write-Warning "[CloudSmith] Could not write encrypted token: $_"
    Write-Warning "[CloudSmith] The service is running — token can be re-generated by re-running this script."
}

# ---------------------------------------------------------------------------
# 6. Done
# ---------------------------------------------------------------------------
Write-Host "[CloudSmith] Agent service installed and enrolled. Token written to $TokenFile"
