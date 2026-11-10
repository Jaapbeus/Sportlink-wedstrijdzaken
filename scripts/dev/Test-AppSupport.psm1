function ConvertTo-TestAppBaseUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [Parameter(Mandatory)][string]$ParameterName
    )

    $parsed = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::RelativeOrAbsolute, [ref]$parsed) -or
        -not $parsed.IsAbsoluteUri -or $parsed.Scheme -notin @('http', 'https') -or
        [string]::IsNullOrWhiteSpace($parsed.Host) -or
        $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment) {
        throw "$ParameterName must be an absolute HTTP(S) base URL without user info, query, or fragment."
    }
    return $parsed
}

function ConvertFrom-TestAppMigrationLedgerResult {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Result)

    if ($Result.ExitCode -eq 0) {
        return [pscustomobject]@{
            Status = 'Read'
            Applied = @($Result.Output -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    }
    if ($Result.Output -match '(?i)(relation|table)\s+.*schema_migrations.*does not exist') {
        return [pscustomobject]@{ Status = 'Fresh'; Applied = @() }
    }
    return [pscustomobject]@{ Status = 'Failed'; Applied = @() }
}

function Test-TestAppPasswordlessPostgresHost {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HostName,
        [switch]$ViaContainer
    )

    if ($ViaContainer) { return $true }
    return $HostName -in @('localhost', '127.0.0.1', '::1')
}

function Invoke-TestAppFeedbackSmoke {
    [CmdletBinding()]
    param(
        [string]$Token,
        [string]$Owner,
        [string]$Repository,
        [switch]$Offline,
        [scriptblock]$Request = {
            param($Uri, $Method, $Headers, $Body, $ContentType)
            Invoke-RestMethod -Uri $Uri -Method $Method -Headers $Headers -Body $Body `
                -ContentType $ContentType -ErrorAction Stop
        }
    )

    if ($Offline) {
        return [pscustomobject]@{ Status = 'Skipped'; Message = 'External GitHub feedback smoke explicitly skipped (-Offline).' }
    }
    if ([string]::IsNullOrWhiteSpace($Token) -or [string]::IsNullOrWhiteSpace($Owner) -or
        [string]::IsNullOrWhiteSpace($Repository)) {
        return [pscustomobject]@{ Status = 'Failed'; Message = 'GitHub feedback smoke requires configured GitHub credentials and repository settings.' }
    }

    $headers = @{
        Authorization = "Bearer $Token"
        Accept = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent' = 'Test-App.ps1'
    }
    $repoUri = "https://api.github.com/repos/$Owner/$Repository/issues"
    $title = "[TEST] Feedback smoke test — $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    $payload = @{ title = $title; body = 'Temporary Test-App.ps1 feedback smoke test; close immediately.' } | ConvertTo-Json -Compress
    $issueNumber = $null

    try {
        $created = & $Request $repoUri 'POST' $headers $payload 'application/json'
        if ($null -eq $created.number -or "$($created.number)" -notmatch '^\d+$') {
            return [pscustomobject]@{ Status = 'Failed'; Message = 'GitHub feedback smoke did not return a valid issue number.' }
        }
        $issueNumber = [long]$created.number
        $closePayload = '{"state":"closed"}'
        & $Request "$repoUri/$issueNumber" 'PATCH' $headers $closePayload 'application/json' | Out-Null
        return [pscustomobject]@{ Status = 'Passed'; Message = 'GitHub feedback smoke issue created and closed.' }
    } catch {
        return [pscustomobject]@{ Status = 'Failed'; Message = 'GitHub feedback smoke request or cleanup failed.' }
    }
}

Export-ModuleMember -Function ConvertTo-TestAppBaseUri, ConvertFrom-TestAppMigrationLedgerResult, `
    Test-TestAppPasswordlessPostgresHost, Invoke-TestAppFeedbackSmoke
