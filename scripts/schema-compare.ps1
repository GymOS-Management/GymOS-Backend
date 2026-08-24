<#
.SYNOPSIS
    Compares the GymOS.Database project against a live database. READ-ONLY.

.DESCRIPTION
    Builds the database project into a dacpac and asks SqlPackage what would have to change for
    the target to match it. It produces a report and a script; it never executes either, and it
    has no code path that can.

    THIS IS NOT A DEPLOYMENT TOOL. EF Core migrations are the only mechanism that changes a GymOS
    database, in every environment. See database/README.md for the division of responsibility and
    for why publishing this project would be actively dangerous.

.PARAMETER ConnectionString
    The target. Pass it in; nothing here reads or writes a stored credential. A read-only login is
    sufficient and is what you should use — the comparison only reads catalog metadata.

.PARAMETER OutputDirectory
    Where the report and script land. Defaults to artifacts/schema-compare, which is gitignored.

.EXAMPLE
    $env:ConnectionStrings__Database = '<uat connection string>'
    ./scripts/schema-compare.ps1 -ConnectionString $env:ConnectionStrings__Database
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ConnectionString,

    [string] $OutputDirectory = 'artifacts/schema-compare'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $project = 'database/GymOS.Database/GymOS.Database.sqlproj'
    $dacpac  = 'database/GymOS.Database/bin/Release/GymOS.Database.dacpac'

    Write-Host '==> Building the database project' -ForegroundColor Cyan
    dotnet build $project --configuration Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Database project build failed.' }
    if (-not (Test-Path $dacpac)) { throw "Expected a dacpac at $dacpac and found none." }

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $report = Join-Path $OutputDirectory 'drift-report.xml'
    $script = Join-Path $OutputDirectory 'would-apply.sql'

    # ── Comparison options, each one load-bearing ──────────────────────────────────────────────
    #
    # IgnorePartitionSchemes: pf_audit_logs_monthly gains a boundary every month at runtime, so its
    #   range list is state rather than schema and can never match the repository. Without this the
    #   report is noise every single run. See database/Storage/pf_audit_logs_monthly.sql.
    #
    # DropObjectsNotInSource=False: the target legitimately holds objects this project does not and
    #   must not declare — platform.__EFMigrationsHistory, the gymos_app and gymos_migrator
    #   principals, and one platform.invoice_number_seq_<year> sequence per year created on demand
    #   by next_invoice_number. Dropping those sequences would restart invoice numbering at 1 and
    #   break a legally consecutive series. This stays False. It is not a preference.
    #
    # BlockOnPossibleDataLoss: belt and braces. Nothing here executes anything, but a generated
    #   script that someone runs by hand should refuse rather than truncate.
    $options = @(
        '/p:IgnorePartitionSchemes=True'
        '/p:DropObjectsNotInSource=False'
        '/p:BlockOnPossibleDataLoss=True'
        '/p:IgnorePermissions=True'
        '/p:IgnoreRoleMembership=True'
        '/p:IgnoreUserSettingsObjects=True'
        '/p:ExcludeObjectTypes=Users;Logins;RoleMembership;Permissions;Credentials;DatabaseScopedCredentials'
    )

    Write-Host '==> Generating drift report (read-only)' -ForegroundColor Cyan
    dotnet sqlpackage /Action:DeployReport `
        /SourceFile:$dacpac `
        /TargetConnectionString:$ConnectionString `
        /OutputPath:$report `
        @options
    if ($LASTEXITCODE -ne 0) { throw 'DeployReport failed.' }

    Write-Host '==> Generating the script that WOULD be applied (not executing it)' -ForegroundColor Cyan
    dotnet sqlpackage /Action:Script `
        /SourceFile:$dacpac `
        /TargetConnectionString:$ConnectionString `
        /OutputPath:$script `
        @options
    if ($LASTEXITCODE -ne 0) { throw 'Script generation failed.' }

    # An empty report has no <Operations> element at all.
    $xml = [xml](Get-Content $report -Raw)
    $operations = $xml.DeploymentReport.Operations
    Write-Host ''
    if ($null -eq $operations) {
        Write-Host 'NO DRIFT. The database matches the repository schema.' -ForegroundColor Green
    }
    else {
        Write-Host 'DRIFT DETECTED. Review before doing anything:' -ForegroundColor Yellow
        foreach ($op in $operations.Operation) {
            foreach ($item in $op.Item) {
                Write-Host ("  {0,-8} {1} {2}" -f $op.Name, $item.Type, $item.Value)
            }
        }
        Write-Host ''
        Write-Host "  report: $report"
        Write-Host "  script: $script  (NOT executed)"
        Write-Host ''
        Write-Host 'Drift means the database and the migrations disagree. The fix is an EF migration,' -ForegroundColor Yellow
        Write-Host 'not this script. Do not run it against UAT or production.' -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
