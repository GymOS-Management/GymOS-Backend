<#
.SYNOPSIS
    Brings the local development database up to a usable state: migrated, seeded, and populated.

.DESCRIPTION
    Local development only. Runs exclusively against GymOS_Local on a local server, and refuses
    everything else -- see Assert-LocalDatabaseTarget in GymOS.LocalDatabase.psm1 for the four
    conditions it checks and why each one is needed.

    Idempotent by construction rather than by intention. Nothing here truncates or deletes: the
    migrator applies only pending migrations, and the application's seeders are insert-only by design
    (FeatureCatalogSeeder adds newly declared keys and leaves an operator's edits alone;
    PlatformIdentitySeeder syncs roles and permissions; SuperAdminSeeder never resets an existing
    account's password). Running this twice is therefore a no-op the second time, not a duplicate.

    Use reset-local-db.ps1 when you want a genuinely empty database.

.PARAMETER ConnectionString
    Overrides the default local target. Still subject to every safety check -- passing a UAT or
    Production string here does not work, which is the point of it being checked rather than trusted.

.PARAMETER SkipStack
    Assume SQL Server and the migrator have already run. Useful when iterating and the stack is up.

.EXAMPLE
    .\scripts\populate-all.ps1

.EXAMPLE
    .\scripts\populate-all.ps1 -Verbose
#>
[CmdletBinding()]
param(
    # The SQL Server address, when the default local one is not where your sidecar is listening --
    # most often because 1433 was already taken and MSSQL_PORT moved it. Separate from
    # -ConnectionString because the port is the only thing that usually changes, and asking for a
    # whole connection string (with its password) to express "a different port" invites pasting a
    # real one from somewhere else.
    [Parameter()][string] $Server = 'localhost,1433',

    [Parameter()][string] $ConnectionString,
    [Parameter()][switch] $SkipStack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'GymOS.LocalDatabase.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot

# The local development target, matching docker-compose.override.yml's own string for the api service
# and appsettings.Development.json. Connects as gymos_app: it owns nothing and holds no DDL rights,
# which is deliberate even here -- populate writes rows, and rows are all it needs.
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $ConnectionString = "Server=$Server;Database=GymOS_Local;User Id=gymos_app;" +
    'Password=Gymos_dev_app_password1;Encrypt=True;TrustServerCertificate=True;Current Language=us_english'
}

# ---------------------------------------------------------------------------------------------------
# 1. Refuse to touch anything but the local database. Before every other step, including the ones
#    that only read -- there is no reason to connect to UAT even to count rows.
# ---------------------------------------------------------------------------------------------------
Write-Host 'Verifying target is the local development database...' -ForegroundColor Cyan
Assert-LocalDatabaseTarget -ConnectionString $ConnectionString -Operation 'populate-all'

$database = Get-SqlDatabaseName -ConnectionString $ConnectionString
$server = Get-SqlServerName -ConnectionString $ConnectionString
Write-Host "  OK: $database on $server" -ForegroundColor Green

# ---------------------------------------------------------------------------------------------------
# 2. The stack. db-init creates the database and both principals; migrator applies migrations and
#    exits 0. These are the deployment's own mechanisms, not a parallel path -- the API cannot migrate
#    in any environment, because the principal it connects as holds no DDL rights (R14).
# ---------------------------------------------------------------------------------------------------
if (-not $SkipStack) {
    Write-Host 'Bringing up SQL Server, db-init and the migrator...' -ForegroundColor Cyan

    Push-Location $repoRoot
    try {
        # db-init depends on mssql and is gated on its health check, so this one command covers both.
        Invoke-Native -FailureMessage 'Could not start SQL Server and db-init' `
            -- docker compose up -d db-init

        # The migrator is a one-shot container: --exit-code-from makes compose wait for it and hand
        # back its exit code, which is what turns a failed migration into a failed script rather than
        # a populate against a half-built schema.
        Invoke-Native -FailureMessage 'Migrations failed; nothing was populated' `
            -- docker compose up --exit-code-from migrator migrator
    }
    finally {
        Pop-Location
    }

    Write-Host '  OK: schema is current' -ForegroundColor Green
}
else {
    Write-Host 'Skipping stack startup (-SkipStack).' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------------------------------
# 3. Reachability, as its own step and with its own message. A failure here means the database is not
#    answering, which is a different problem from a migration failure and wants a different fix.
# ---------------------------------------------------------------------------------------------------
Write-Host 'Checking the database answers...' -ForegroundColor Cyan

Add-Type -AssemblyName System.Data
$connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = 'SELECT DB_NAME()'
    $actual = [string] $command.ExecuteScalar()

    # Belt and braces: the string said GymOS_Local, and the server agrees that is what we opened.
    # Checked because a connection string can be redirected by an alias or a failover partner, and at
    # that point the name in the string is a claim rather than a fact.
    if ($actual -ine $database) {
        throw "Connected to '$actual' but the connection string named '$database'. Refusing to continue."
    }

    Write-Host "  OK: connected to $actual" -ForegroundColor Green
}
finally {
    $connection.Dispose()
}

# ---------------------------------------------------------------------------------------------------
# 4. What the application seeds for itself on boot. Bringing the API up runs PlatformIdentitySeeder
#    (roles, permissions), FeatureCatalogSeeder (the feature catalogue) and SuperAdminSeeder -- all
#    insert-only, so this is the idempotent part and needs no guard of its own.
# ---------------------------------------------------------------------------------------------------
if (-not $SkipStack) {
    Write-Host 'Starting the API so its own seeders run...' -ForegroundColor Cyan

    Push-Location $repoRoot
    try {
        Invoke-Native -FailureMessage 'Could not start the API' -- docker compose up -d api
    }
    finally {
        Pop-Location
    }

    Write-Host '  OK: identity, feature catalogue and SuperAdmin seeded' -ForegroundColor Green
}

# ---------------------------------------------------------------------------------------------------
# 5. Demo data: thirteen gyms, three plans with prices, subscriptions in every lifecycle state,
#    invoices in every status, two feature overrides, and ninety days of daily_platform_stats so the
#    dashboards have a series to draw. Design: docs/superpowers/specs/2026-09-03-demo-data-seeder-design.md.
#
#    A seeder inside the application, not SQL from here: every one of those objects is an aggregate
#    with invariants enforced in code, and invoice numbers come from a stored procedure the app calls.
#    Run as a host mode -- Seed:DemoData=true makes the API boot, seed, and exit, exactly as
#    Database:MigrateOnly does for the migrator -- as the application principal, since it is all DML.
#
#    Idempotent in the strictest sense: it fills an EMPTY portfolio and otherwise writes nothing, so a
#    database that already has gyms or plans (yours, once you have created one by hand) is left alone.
#    `docker compose run` hands back the container's exit code, so a refused or failed seed fails here.
# ---------------------------------------------------------------------------------------------------
if (-not $SkipStack) {
    Write-Host 'Seeding demo data (Seed:DemoData host mode)...' -ForegroundColor Cyan
    Push-Location $repoRoot
    try {
        Invoke-Native -FailureMessage 'Demo data seeding failed; see the container output above' `
            -- docker compose run --rm --no-deps -e Seed__DemoData=true api
    }
    finally {
        Pop-Location
    }
    Write-Host '  OK: demo portfolio seeded, or left alone because gyms or plans already existed' -ForegroundColor Green
}
else {
    Write-Host 'Skipping demo data (-SkipStack). To seed by hand:' -ForegroundColor Yellow
    Write-Host '  docker compose run --rm --no-deps -e Seed__DemoData=true api' -ForegroundColor Yellow
}
Write-Host ''
Write-Host "populate-all finished against $database." -ForegroundColor Cyan
Write-Host '  Sign in at http://localhost:5080 -- admin@gymos.local / gymos-dev-admin-password' -ForegroundColor Cyan
