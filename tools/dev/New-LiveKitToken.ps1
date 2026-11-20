<#
.SYNOPSIS
    Creates a LiveKit access token for local testing (Phase 0 Tauri spike).

.DESCRIPTION
    Uses the LiveKit CLI container image, so nothing has to be installed.
    Local server (default): the API secret is read from the AppHost's user secrets
    (Parameters:livekit-api-secret), which Aspire generates on first run.
    LiveKit Cloud: pass -ApiKey and set the LIVEKIT_API_SECRET environment variable first.
    The secret is never written to disk or printed.

.EXAMPLE
    ./tools/dev/New-LiveKitToken.ps1 -Identity windows-desktop

.EXAMPLE
    $env:LIVEKIT_API_SECRET = '<secret from the LiveKit Cloud project>'
    ./tools/dev/New-LiveKitToken.ps1 -Identity linux-desktop -ApiKey APIxxxx -ValidFor 48h
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')]
    [string] $Identity,

    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')]
    [string] $Room = 'spike',

    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')]
    [string] $ApiKey = 'backroom-dev',

    # At most 48 hours.
    [ValidatePattern('^([1-9]|[1-3][0-9]|4[0-8])h$|^[1-9][0-9]?m$')]
    [string] $ValidFor = '24h',

    [ValidatePattern('^livekit/livekit-cli(:[\w.-]+)?(@sha256:[0-9a-f]{64})?$')]
    [string] $CliImage = 'livekit/livekit-cli:v2.19.2'
)

$ErrorActionPreference = 'Stop'

$callerSecret = $env:LIVEKIT_API_SECRET
$secret = $callerSecret
if (-not $secret) {
    $appHost = Join-Path $PSScriptRoot '../../backend/src/Aspire/Backroom.AppHost'
    $line = dotnet user-secrets list --project $appHost | Where-Object { $_ -like 'Parameters:livekit-api-secret*' }
    if (-not $line) {
        throw 'No LiveKit secret found. Run the AppHost once, or set LIVEKIT_API_SECRET for LiveKit Cloud.'
    }
    $secret = ($line -split '=', 2)[1].Trim()
}

$env:LIVEKIT_API_SECRET = $secret
try {
    # The CLI writes informational lines to stderr; Windows PowerShell 5.1 would treat them as errors under 'Stop'.
    $ErrorActionPreference = 'Continue'
    $output = docker run --rm -e LIVEKIT_API_SECRET $CliImage token create `
        --api-key $ApiKey --join --room $Room --identity $Identity --valid-for $ValidFor 2>&1 |
        ForEach-Object { "$_" }
}
finally {
    $ErrorActionPreference = 'Stop'
    # Leave a secret the caller set (LiveKit Cloud) in place; remove only the one read from user secrets.
    if (-not $callerSecret) {
        Remove-Item Env:LIVEKIT_API_SECRET -ErrorAction SilentlyContinue
    }
}

$token = $output | Where-Object { $_ -match '^Access token:\s*(\S+)' } | ForEach-Object { $Matches[1] }
if (-not $token) {
    throw "Could not read a token from the LiveKit CLI output:`n$($output -join "`n")"
}

$token
