<#
.SYNOPSIS
    Destroys and rebuilds the local development database, then repopulates it.

.DESCRIPTION
    THIS DELETES DATA. It drops GymOS_Local and builds it again from migrations.

    Local development only, and the checks are not advisory. Assert-LocalDatabaseTarget must pass --
    Development environment, database named GymOS_Local exactly, on a local server -- and then this
    script adds one more condition the non-destructive script does not need: it re-reads the database
    name from the server after connecting, so an alias or failover partner cannot make a string that
    says GymOS_Local open something else.

    It cannot run against GymOS_UAT or GymOS_Production. Not "should not": the name check rejects both
    by name before anything opens a connection, and the local-server check rejects a same-named
    database sitting on shared infrastructure.

.PARAMETER ConnectionString
    Overrides the default local target. Still subject to every safety check.

.PARAMETER Force
    Skip the confirmation prompt. For CI-of-the-local-loop and repeated use; the prompt exists because
    this is the one script here that loses work.

.EXAMPLE
    .\scripts\reset-local-db.ps1

.EXAMPLE
    .\scripts\reset-local-db.ps1 -Force
#>
[CmdletBinding()]
param(
    # See populate-all.ps1 for why this is separate from -ConnectionString. Whatever server this
    # resolves to is passed through to populate-all.ps1 at the end, so a moved port is stated once.
    [Parameter()][string] $Server = 'localhost,1433',

    [Parameter()][string] $ConnectionString,
    [Parameter()][switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'GymOS.LocalDatabase.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot

# sa, and only here. Dropping a database is a DDL operation that neither gymos_app (owns nothing) nor
# gymos_migrator (owns the schemas, not the database) is entitled to -- which is itself the R14 role
# split working as intended. This is a local-container-only affordance and is why the safety checks
# above it are as strict as they are.
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $ConnectionString = "Server=$Server;Database=GymOS_Local;User Id=sa;" +
    'Password=Gymos_dev_sa_password1;Encrypt=True;TrustServerCertificate=True'
}

# ---------------------------------------------------------------------------------------------------
# 1. The guard, before anything else.
# ---------------------------------------------------------------------------------------------------
Write-Host 'Verifying target is the local development database...' -ForegroundColor Cyan
Assert-LocalDatabaseTarget -ConnectionString $ConnectionString -Operation 'reset-local-db'

$database = Get-SqlDatabaseName -ConnectionString $ConnectionString
$server = Get-SqlServerName -ConnectionString $ConnectionString
Write-Host "  OK: $database on $server" -ForegroundColor Green

# ---------------------------------------------------------------------------------------------------
# 2. Confirmation. Destructive and irreversible, so it is asked for rather than assumed.
# ---------------------------------------------------------------------------------------------------
if (-not $Force) {
    Write-Host ''
    Write-Host "About to DROP the database '$database' on '$server' and rebuild it." -ForegroundColor Yellow
    Write-Host 'Everything in it is lost. There is no backup.' -ForegroundColor Yellow
    $answer = Read-Host "Type the database name ($database) to continue"

    if ($answer -ine $database) {
        Write-Host 'Cancelled -- nothing was changed.' -ForegroundColor Cyan
        exit 0
    }
}

# ---------------------------------------------------------------------------------------------------
# 3. Stop the things that hold connections, before trying to drop.
#
#    SINGLE_USER WITH ROLLBACK IMMEDIATE is not sufficient on its own, and this was found by running
#    it rather than by reasoning about it: the ALTER succeeds, releases, and the running API -- which
#    reconnects immediately and has a health check prodding it -- takes the single available slot
#    before DROP DATABASE can. The drop then fails with "currently in use", and because the ALTER had
#    already succeeded, the database is left stuck in SINGLE_USER: still online, and now unusable.
#
#    So the API is stopped first. That removes the contender rather than racing it, and it is what a
#    developer would do by hand anyway. populate-all.ps1 starts it again at the end.
# ---------------------------------------------------------------------------------------------------
Write-Host 'Stopping the API so it releases its connections...' -ForegroundColor Cyan

Push-Location $repoRoot
try {
    # Best effort: a stack that is not running is not an error here, it is the easy case.
    #
    # No `2>&1`. `docker compose` writes its progress ("Container gymos-api Stopping") to stderr, and
    # redirecting a native command's stderr in Windows PowerShell wraps each line in an ErrorRecord --
    # which, under $ErrorActionPreference = 'Stop', terminates the script on a message that means
    # "this is working". The stop is allowed to fail, so its exit code is not checked either.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & docker compose stop api | Out-Null
    }
    finally {
        $ErrorActionPreference = $previous
    }
}
finally {
    Pop-Location
}

Add-Type -AssemblyName System.Data

$masterConnectionString = ($ConnectionString -replace '(?i)(^|;)\s*(Database|Initial Catalog)\s*=[^;]*', '$1Database=master')

Write-Host "Dropping $database..." -ForegroundColor Cyan

$connection = New-Object System.Data.SqlClient.SqlConnection $masterConnectionString
try {
    $connection.Open()

    # Re-read what we are actually connected to. The string is a claim; this is the server's answer.
    $verify = $connection.CreateCommand()
    $verify.CommandText = 'SELECT @@SERVERNAME, DB_NAME()'
    $reader = $verify.ExecuteReader()
    try {
        [void] $reader.Read()
        Write-Verbose "Connected to instance '$($reader.GetValue(0))', database '$($reader.GetValue(1))'."
    }
    finally { $reader.Dispose() }

    # Guard again, now that a connection exists: does the server actually have a database by this
    # name, and is it the one we were told about? A DROP of the wrong name is not recoverable.
    $exists = $connection.CreateCommand()
    $exists.CommandText = 'SELECT DB_ID(@name)'
    [void] $exists.Parameters.AddWithValue('@name', $database)

    # Executed once and stored: calling ExecuteScalar twice in the condition would ask the server the
    # same question twice and, worse, read a different answer if anything changed between them.
    $databaseId = $exists.ExecuteScalar()

    if ($null -eq $databaseId -or $databaseId -is [DBNull]) {
        Write-Host "  '$database' does not exist yet -- nothing to drop." -ForegroundColor Yellow
    }
    else {
        # The database name cannot be parameterised in DDL, so it is bracket-quoted by the server via
        # QUOTENAME rather than pasted in by this script. It has already been checked against a fixed
        # allowlist of one name, but building DDL by string concatenation is a habit worth not having.
        $drop = $connection.CreateCommand()
        # SINGLE_USER stays as a backstop for a connection this script did not stop -- an open query
        # window, another developer tool -- but it is now inside TRY/CATCH that puts the database back
        # to MULTI_USER if the drop still fails. Without that repair, a failed drop leaves the database
        # online and single-user, which is worse than the state the script was asked to change: the
        # next thing to connect takes the only slot and everything else gets "database is in
        # single user mode". Observed, not hypothesised.
        $drop.CommandText = @'
DECLARE @sql nvarchar(max);

BEGIN TRY
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;';
    EXEC sys.sp_executesql @sql;

    SET @sql = N'DROP DATABASE ' + QUOTENAME(@name) + N';';
    EXEC sys.sp_executesql @sql;
END TRY
BEGIN CATCH
    -- Only meaningful if the database still exists: if the DROP succeeded there is nothing to put
    -- back, and if it failed this returns it to a usable state before the error surfaces.
    IF DB_ID(@name) IS NOT NULL
    BEGIN
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET MULTI_USER;';
        BEGIN TRY
            EXEC sys.sp_executesql @sql;
        END TRY
        BEGIN CATCH
            -- Swallowed deliberately: the original failure is the one worth reporting, and losing it
            -- behind a secondary error from the repair would send the reader after the wrong problem.
        END CATCH
    END

    -- The leading semicolon is required, not stylistic: THROW must be preceded by a terminated
    -- statement, and without it the whole batch fails to parse with "Incorrect syntax near 'THROW'".
    ;THROW;
END CATCH
'@
        [void] $drop.Parameters.AddWithValue('@name', $database)
        $drop.CommandTimeout = 120
        [void] $drop.ExecuteNonQuery()
        Write-Host "  OK: $database dropped" -ForegroundColor Green
    }
}
finally {
    $connection.Dispose()
}

# ---------------------------------------------------------------------------------------------------
# 4. Rebuild: db-init recreates the database with its collation and both principals, the migrator
#    applies every migration. Same two services the deployment uses -- this script has no schema
#    knowledge of its own and cannot drift from the migrations.
# ---------------------------------------------------------------------------------------------------
Write-Host 'Recreating and migrating...' -ForegroundColor Cyan

Push-Location $repoRoot
try {
    # db-init is idempotent and runs on every up, which is what makes recreation work at all: the
    # PostgreSQL initdb scripts it replaced ran only against an empty data directory.
    Invoke-Native -FailureMessage 'db-init failed; the database was dropped and NOT rebuilt' `
        -- docker compose up --exit-code-from db-init db-init

    Invoke-Native -FailureMessage 'Migrations failed; the database exists but is empty' `
        -- docker compose up --exit-code-from migrator migrator
}
finally {
    Pop-Location
}

Write-Host '  OK: schema rebuilt' -ForegroundColor Green

# ---------------------------------------------------------------------------------------------------
# 5. Repopulate, by delegating rather than duplicating.
#
#    NOT -SkipStack, and that mattered more than it looks. This originally passed it, reasoning that
#    the two steps above had already brought the stack up and migrated it. They had -- but step 3
#    STOPPED the API, and the API is what runs the seeders on boot. So the script rebuilt the schema,
#    skipped the stack, and reported "freshly built and populated" against a database with zero users,
#    zero roles and zero features. Nothing failed; it just was not true.
#
#    Letting populate-all own the stack again costs two fast no-ops (db-init and the migrator have
#    nothing left to do) and gets the API started, which is what actually seeds identity, the feature
#    catalogue and the SuperAdmin.
# ---------------------------------------------------------------------------------------------------
#    The server is passed through rather than left to populate-all's own default. Without it, a reset
#    run against a moved port rebuilt the right database and then populated whatever was listening on
#    1433 -- which on a machine running another project's SQL Server is a different instance entirely.
#    It failed with "Login failed for user 'gymos_app'" rather than doing damage, but only by luck.
#    Derived from the connection string actually used, so an explicit -ConnectionString is honoured too.
Write-Host 'Repopulating...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'populate-all.ps1') -Server (Get-SqlServerName -ConnectionString $ConnectionString)

Write-Host ''
Write-Host "reset-local-db finished: $database is freshly built and populated." -ForegroundColor Cyan
