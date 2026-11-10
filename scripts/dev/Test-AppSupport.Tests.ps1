$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Test-AppSupport.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DevServices.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}

$defaultUri = ConvertTo-TestAppBaseUri -BaseUrl 'http://localhost:7094' -ParameterName 'FunctionBaseUrl'
Assert-True ($defaultUri.Port -eq 7094) 'the compatibility default URL keeps its port'
$isolatedUri = ConvertTo-TestAppBaseUri -BaseUrl 'http://127.0.0.1:17094' -ParameterName 'FunctionBaseUrl'
Assert-True ($isolatedUri.Port -eq 17094) 'an isolated runtime URL exposes its own listener port'

$badUrlRejected = $false
try { ConvertTo-TestAppBaseUri -BaseUrl 'file:///tmp/not-http' -ParameterName 'FunctionBaseUrl' | Out-Null }
catch { $badUrlRejected = $true }
Assert-True $badUrlRejected 'non-HTTP base URLs are rejected'
foreach ($badBase in @('/tmp/local-path', 'relative/path', 'https://', 'https://user:pass@example.test/',
    'https://example.test/?q=1', 'https://example.test/#part')) {
    $rejected = $false
    try { ConvertTo-TestAppBaseUri -BaseUrl $badBase -ParameterName 'FunctionBaseUrl' | Out-Null }
    catch { $rejected = $true }
    Assert-True $rejected "invalid base URL is rejected: $badBase"
}

$readLedger = ConvertFrom-TestAppMigrationLedgerResult -Result ([pscustomobject]@{
    ExitCode = 0; Output = "001_init.sql`n002_settings.sql"
})
Assert-True ($readLedger.Status -eq 'Read' -and $readLedger.Applied.Count -eq 2) 'successful ledger reads return applied migrations'
$freshLedger = ConvertFrom-TestAppMigrationLedgerResult -Result ([pscustomobject]@{
    ExitCode = 1; Output = 'ERROR: relation "schema_migrations" does not exist'
})
Assert-True ($freshLedger.Status -eq 'Fresh' -and $freshLedger.Applied.Count -eq 0) 'a missing ledger table is treated as a fresh database'
$failedLedger = ConvertFrom-TestAppMigrationLedgerResult -Result ([pscustomobject]@{
    ExitCode = 2; Output = 'connection refused'
})
Assert-True ($failedLedger.Status -eq 'Failed') 'database command failures do not become an empty ledger'
$missingDatabase = ConvertFrom-TestAppMigrationLedgerResult -Result ([pscustomobject]@{
    ExitCode = 2; Output = 'FATAL: database does not exist'
})
Assert-True ($missingDatabase.Status -eq 'Failed') 'a missing database does not masquerade as a fresh database'
Assert-True (Test-TestAppPasswordlessPostgresHost -HostName 'localhost') 'passwordless auth permits localhost'
Assert-True (Test-TestAppPasswordlessPostgresHost -HostName '::1') 'passwordless auth permits IPv6 loopback'
Assert-True (-not (Test-TestAppPasswordlessPostgresHost -HostName 'database.example')) 'passwordless host psql rejects a non-loopback host'
Assert-True (Test-TestAppPasswordlessPostgresHost -HostName 'database-service' -ViaContainer) 'local container socket auth can omit PGPASSWORD'

$offlineCalls = [System.Collections.Generic.List[object]]::new()
$offlineRequest = {
    param($Uri, $Method, $Headers, $Body, $ContentType)
    $offlineCalls.Add($Method)
    throw 'Offline path must not call GitHub.'
}.GetNewClosure()
$offlineResult = Invoke-TestAppFeedbackSmoke -Offline -Token 'synthetic-test-token' `
    -Owner 'test-owner' -Repository 'test-repo' -Request $offlineRequest
Assert-True ($offlineResult.Status -eq 'Skipped') 'offline feedback smoke is reported as skipped'
Assert-True ($offlineCalls.Count -eq 0) 'offline mode makes no external request'

$fakeCalls = [System.Collections.Generic.List[object]]::new()
$fakeRequest = {
    param($Uri, $Method, $Headers, $Body, $ContentType)
    $fakeCalls.Add([pscustomobject]@{ Uri = $Uri; Method = $Method })
    if ($Method -eq 'POST') { return [pscustomobject]@{ number = 123 } }
    return [pscustomobject]@{ state = 'closed' }
}.GetNewClosure()
$fakeResult = Invoke-TestAppFeedbackSmoke -Token 'synthetic-test-token' -Owner 'test-owner' `
    -Repository 'test-repo' -Request $fakeRequest
Assert-True ($fakeResult.Status -eq 'Passed') 'fake create-and-close cycle passes'
Assert-True ($fakeCalls.Count -eq 2) 'fake cycle has exactly create and cleanup requests'
Assert-True ($fakeCalls[0].Method -eq 'POST' -and $fakeCalls[1].Method -eq 'PATCH') 'fake cycle closes the created issue'

$missingConfigCalls = [System.Collections.Generic.List[object]]::new()
$missingConfigRequest = {
    param($Uri, $Method, $Headers, $Body, $ContentType)
    $missingConfigCalls.Add($Method)
    throw 'Missing configuration must not call GitHub.'
}.GetNewClosure()
$missingConfigResult = Invoke-TestAppFeedbackSmoke -Owner 'test-owner' -Repository 'test-repo' -Request $missingConfigRequest
Assert-True ($missingConfigResult.Status -eq 'Failed') 'missing configuration fails instead of claiming success'
Assert-True ($missingConfigCalls.Count -eq 0) 'missing configuration makes no request'

$fakeDockerCalls = [System.Collections.Generic.List[object]]::new()
$fakeDockerState = [pscustomobject]@{ ExitCode = 0 }
$fakeDockerRunner = {
    param($FileName, $Arguments, $PgPassword)
    $fakeDockerCalls.Add([pscustomobject]@{
        FileName = $FileName; Arguments = @($Arguments); PasswordPresent = -not [string]::IsNullOrEmpty($PgPassword)
    })
    return [pscustomobject]@{ ExitCode = $fakeDockerState.ExitCode; Output = '1' }
}.GetNewClosure()

    $trustResult = Invoke-Psql -ContainerName 'owned-test-db' -Database 'postgres' -User 'postgres' `
        -Password '' -Sql 'SELECT 1;' -Tuples -ProcessRunner $fakeDockerRunner
    Assert-True ($trustResult.ExitCode -eq 0) 'passwordless Docker psql command succeeds with a fake runner'
    $trustArgs = @($fakeDockerCalls[0].Arguments)
    Assert-True ($trustArgs -notcontains '-e') 'passwordless trust mode does not set or expose a password argument'

    $null = Invoke-Psql -ContainerName 'owned-test-db' -Database 'postgres' -User 'postgres' `
        -Password 'synthetic-password' -Sql 'SELECT 1;' -Tuples -ProcessRunner $fakeDockerRunner
    $passwordArgs = @($fakeDockerCalls[1].Arguments)
    Assert-True ($passwordArgs -contains '-e') 'password-auth mode passes PGPASSWORD only through docker exec environment'
    $environmentArgument = $passwordArgs[[array]::IndexOf($passwordArgs, '-e') + 1]
    Assert-True ($environmentArgument -eq 'PGPASSWORD') 'Docker receives only the PGPASSWORD variable name'
    Assert-True ($passwordArgs -notcontains 'synthetic-password') 'the password is absent from Docker arguments'
    Assert-True ($fakeDockerCalls[1].PasswordPresent) 'the child runner receives a password through its environment parameter'

    $fakeDockerState.ExitCode = 17
    $failedQuery = Invoke-Psql -ContainerName 'owned-test-db' -Database 'postgres' -User 'postgres' `
        -Password '' -Sql 'SELECT 1;' -ProcessRunner $fakeDockerRunner
    Assert-True ($failedQuery.ExitCode -eq 17) 'Docker/psql failures preserve their exit code'
    $failedState = ConvertFrom-TestAppMigrationLedgerResult -Result ([pscustomobject]@{
        ExitCode = $failedQuery.ExitCode; Output = 'synthetic query failure'
    })
    Assert-True ($failedState.Status -eq 'Failed') 'a failing database command cannot become pending migrations'

$childProbe = 'if ($env:PGPASSWORD) { "ENV_PRESENT" } else { "ENV_ABSENT" }'
$credentialResult = Invoke-DevChildProcess -FileName (Get-Command pwsh).Source `
    -Arguments @('-NoProfile', '-Command', $childProbe) -PgPassword 'synthetic-password'
Assert-True ($credentialResult.ExitCode -eq 0 -and $credentialResult.Output -eq 'ENV_PRESENT') `
    'child process receives PGPASSWORD without a command-line value'
$trustResult = Invoke-DevChildProcess -FileName (Get-Command pwsh).Source `
    -Arguments @('-NoProfile', '-Command', $childProbe) -PgPassword ''
Assert-True ($trustResult.ExitCode -eq 0 -and $trustResult.Output -eq 'ENV_ABSENT') `
    'passwordless child process does not inherit PGPASSWORD'

Write-Host 'Test-AppSupport fake tests passed.'
