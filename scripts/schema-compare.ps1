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
    sufficient and is what you should use -- the comparison only reads catalog metadata.

    With -AccessToken, pass a connection string carrying NO credential at all, e.g.
    'Server=tcp:gymos-uat.database.windows.net,1433;Database=GymOS_UAT;Encrypt=True'.

.PARAMETER AccessToken
    A Microsoft Entra access token for https://database.windows.net/, used instead of a password.
    This is how CI compares without a stored secret: the workflow signs in by federated OIDC and
    mints a token for the run (see .github/workflows/schema-drift.yml). Locally, the same value
    comes from:
        az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv

.PARAMETER OutputDirectory
    Where the report and script land. Defaults to artifacts/schema-compare, which is gitignored.

.PARAMETER GitHubSummary
    Also append a human-readable verdict to $env:GITHUB_STEP_SUMMARY. Ignored when that variable is
    not set, so the switch is harmless outside Actions.

.EXAMPLE
    $env:ConnectionStrings__Database = '<uat connection string>'
    ./scripts/schema-compare.ps1 -ConnectionString $env:ConnectionStrings__Database

.OUTPUTS
    Exit code 0 when the schema matches, 2 when it does not, 1 on a failure to compare at all.
    Three distinct codes on purpose: a caller has to be able to tell "there is drift" from "the
    comparison could not run", because the second is not a schema problem and must not be reported
    as one.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ConnectionString,

    [string] $AccessToken,

    [string] $OutputDirectory = 'artifacts/schema-compare',

    [switch] $GitHubSummary
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

# ── Object types counted as "declared" ─────────────────────────────────────────────────────────
# Read from the dacpac's own model rather than by counting files: a table's file also carries its
# indexes and constraints, so a file count would report a number that means nothing. Types the
# model holds for its own bookkeeping are excluded, so that a reader comparing this number against
# what SSMS shows them recognises it.
$countedTypes = @(
    'SqlTable', 'SqlView', 'SqlProcedure', 'SqlScalarFunction', 'SqlInlineTableValuedFunction',
    'SqlMultiStatementTableValuedFunction', 'SqlIndex', 'SqlPrimaryKeyConstraint',
    'SqlForeignKeyConstraint', 'SqlUniqueConstraint', 'SqlCheckConstraint', 'SqlDefaultConstraint',
    'SqlSchema', 'SqlPartitionFunction', 'SqlPartitionScheme', 'SqlSecurityPolicy',
    'SqlDmlTrigger', 'SqlSequence', 'SqlUserDefinedType'
)

function Get-DeclaredObjectCount {
    param([string] $DacpacPath)

    # Best effort by design: a changed dacpac layout must not turn a working comparison into a
    # failed one, so this reports $null and the caller prints 'n/a'.
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $DacpacPath))
        try {
            $entry = $archive.Entries | Where-Object { $_.FullName -eq 'model.xml' }
            if (-not $entry) { return $null }

            $reader = New-Object System.IO.StreamReader($entry.Open())
            try { $modelXml = [xml] $reader.ReadToEnd() } finally { $reader.Dispose() }

            return @($modelXml.DataSchemaModel.Model.Element |
                Where-Object { $countedTypes -contains $_.Type }).Count
        }
        finally { $archive.Dispose() }
    }
    catch {
        Write-Verbose "Could not read the dacpac model: $($_.Exception.Message)"
        return $null
    }
}

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
    #   Note what it costs, because the cost is a real gap and not a detail: an object present in
    #   the database and absent from the project produces no operation at all, so an unexpected
    #   table added by hand cannot be reported here. The alternative — leaving this True — reports
    #   the three legitimate cases above as drift on every single run, and a check that always
    #   fails is a check nobody reads. docs/Architecture/DATABASE.md records the gap and how it is
    #   covered instead.
    #
    # BlockOnPossibleDataLoss: belt and braces. Nothing here executes anything, but a generated
    # script that someone runs by hand should refuse rather than truncate.
    $options = @(
        '/p:IgnorePartitionSchemes=True'
        '/p:DropObjectsNotInSource=False'
        '/p:BlockOnPossibleDataLoss=True'
        '/p:IgnorePermissions=True'
        '/p:IgnoreRoleMembership=True'
        '/p:IgnoreUserSettingsObjects=True'
        '/p:ExcludeObjectTypes=Users;Logins;RoleMembership;Permissions;Credentials;DatabaseScopedCredentials'
    )

    # An Entra token instead of a password, when one was supplied. Kept out of $options so it is
    # never printed alongside them: that array is safe to echo, and this value is not.
    $auth = @()
    if ($AccessToken) { $auth += "/AccessToken:$AccessToken" }

    Write-Host '==> Generating drift report (read-only)' -ForegroundColor Cyan
    dotnet sqlpackage /Action:DeployReport `
        /SourceFile:$dacpac `
        /TargetConnectionString:$ConnectionString `
        /OutputPath:$report `
        @options @auth
    if ($LASTEXITCODE -ne 0) { throw 'DeployReport failed.' }

    Write-Host '==> Generating the script that WOULD be applied (not executing it)' -ForegroundColor Cyan
    dotnet sqlpackage /Action:Script `
        /SourceFile:$dacpac `
        /TargetConnectionString:$ConnectionString `
        /OutputPath:$script `
        @options @auth
    if ($LASTEXITCODE -ne 0) { throw 'Script generation failed.' }

    # An empty report has no <Operations> element at all.
    $xml = [xml](Get-Content $report -Raw)
    $operations = $xml.DeploymentReport.Operations

    $differences = @()
    if ($null -ne $operations) {
        foreach ($op in $operations.Operation) {
            foreach ($item in $op.Item) {
                $differences += [pscustomobject]@{
                    Operation = $op.Name
                    Type      = $item.Type
                    Name      = $item.Value
                }
            }
        }
    }

    $declared = Get-DeclaredObjectCount -DacpacPath $dacpac
    $declaredText = if ($null -eq $declared) { 'n/a' } else { "$declared" }

    Write-Host ''
    if ($differences.Count -eq 0) {
        Write-Host 'Schema comparison: PASSED' -ForegroundColor Green
        Write-Host ''
        Write-Host "  Declared objects: $declaredText"
        Write-Host '  Differences:      0'
    }
    else {
        Write-Host 'Schema comparison: FAILED' -ForegroundColor Yellow
        Write-Host ''
        Write-Host "  Declared objects: $declaredText"
        Write-Host "  Differences:      $($differences.Count)"
        Write-Host ''
        foreach ($difference in $differences) {
            Write-Host ("  {0,-8} {1} {2}" -f $difference.Operation, $difference.Type, $difference.Name)
        }
        Write-Host ''
        Write-Host "  report: $report"
        Write-Host "  script: $script  (NOT executed)"
        Write-Host ''
        Write-Host 'Drift means the database and the migrations disagree. The fix is an EF migration,' -ForegroundColor Yellow
        Write-Host 'not this script. Do not run it against UAT or production.' -ForegroundColor Yellow
    }

    # ── GitHub Actions job summary ─────────────────────────────────────────────────────────────
    # Written from here rather than from the workflow so the verdict has exactly one author. A
    # workflow that re-derived it from stdout would be a second implementation of the same parsing,
    # free to disagree with this one.
    if ($GitHubSummary -and $env:GITHUB_STEP_SUMMARY) {
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add('## Schema comparison')
        $lines.Add('')
        $lines.Add('```')
        $lines.Add($(if ($differences.Count -eq 0) { 'Schema comparison: PASSED' } else { 'Schema comparison: FAILED' }))
        $lines.Add('')
        $lines.Add("Declared objects: $declaredText")
        $lines.Add("Differences:      $($differences.Count)")
        if ($differences.Count -gt 0) {
            $lines.Add('')
            foreach ($difference in $differences) {
                $lines.Add(("{0,-8} {1} {2}" -f $difference.Operation, $difference.Type, $difference.Name))
            }
        }
        $lines.Add('```')
        if ($differences.Count -gt 0) {
            $lines.Add('')
            $lines.Add('The database and the EF migrations disagree. The fix is a migration, never the')
            $lines.Add('generated script. Read `would-apply.sql` in this run''s artifacts to see what')
            $lines.Add('SqlPackage *would* have done, and `docs/Architecture/DATABASE.md` for how to respond.')
        }
        [System.IO.File]::AppendAllLines($env:GITHUB_STEP_SUMMARY, $lines)
    }

    # An explicit verdict on disk, so a caller never has to infer one from exit codes plus the
    # presence of other files. The distinction that matters to CI is drift versus "could not
    # compare", and only this file can tell them apart unambiguously: it is written last, so its
    # absence means the comparison did not finish.
    $verdict = if ($differences.Count -eq 0) { 'PASSED' } else { 'FAILED' }
    Set-Content -Path (Join-Path $OutputDirectory 'verdict.txt') -Value $verdict -Encoding utf8

    if ($differences.Count -gt 0) { exit 2 }
    exit 0
}
finally {
    Pop-Location
}
