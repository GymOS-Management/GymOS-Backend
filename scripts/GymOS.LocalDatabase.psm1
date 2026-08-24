<#
.SYNOPSIS
    The environment guard both local database scripts share.

.DESCRIPTION
    populate-all.ps1 writes data and reset-local-db.ps1 destroys it. Both must be incapable of doing
    that to UAT or Production, and "the developer passes the right connection string" is not a
    control -- it is the thing that goes wrong.

    So the check lives here, once, and both scripts call it before they do anything at all. One
    implementation rather than two, because a copy of a safety check is a copy that drifts, and the
    copy that drifts is the one in the script nobody reads.

    This mirrors DatabaseEnvironmentGuard on the application side, with one deliberate difference:
    the application denies *cross-environment* pairings and tolerates unmanaged database names,
    because integration tests legitimately create their own databases. These scripts are stricter --
    they require GymOS_Local exactly. Nothing about local development needs to run them against
    anything else, and a destructive script has no business being liberal.
#>

Set-StrictMode -Version Latest

# The three managed databases, matching DatabaseEnvironmentGuard's constants.
$script:LocalDatabase = 'GymOS_Local'
$script:UatDatabase = 'GymOS_UAT'
$script:ProductionDatabase = 'GymOS_Production'

<#
.SYNOPSIS
    Splits a SQL Server connection string into a case-insensitive hashtable of its keywords.
.DESCRIPTION
    Deliberately not a regex over the whole string: a password may legitimately contain '=' or ';'
    inside braces, and a keyword match that guesses would read the wrong database name -- which for a
    destructive script is the one mistake that matters. Splits on the first '=' per segment only.
#>
function ConvertFrom-SqlConnectionString {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ConnectionString)

    $parts = @{}
    foreach ($segment in $ConnectionString.Split(';')) {
        if ([string]::IsNullOrWhiteSpace($segment)) { continue }

        $index = $segment.IndexOf('=')
        if ($index -lt 1) { continue }

        $key = $segment.Substring(0, $index).Trim().ToLowerInvariant().Replace(' ', '')
        $value = $segment.Substring($index + 1).Trim()
        $parts[$key] = $value
    }

    return $parts
}

<#
.SYNOPSIS
    Returns the database name a connection string targets, or $null.
#>
function Get-SqlDatabaseName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ConnectionString)

    $parts = ConvertFrom-SqlConnectionString -ConnectionString $ConnectionString

    # Both spellings the driver accepts.
    foreach ($key in @('database', 'initialcatalog')) {
        if ($parts.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace($parts[$key])) {
            return $parts[$key]
        }
    }

    return $null
}

<#
.SYNOPSIS
    Returns the server a connection string targets, or $null.
#>
function Get-SqlServerName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ConnectionString)

    $parts = ConvertFrom-SqlConnectionString -ConnectionString $ConnectionString

    foreach ($key in @('server', 'datasource', 'addr', 'address', 'networkaddress')) {
        if ($parts.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace($parts[$key])) {
            return $parts[$key]
        }
    }

    return $null
}

<#
.SYNOPSIS
    Whether a server name denotes something on this machine or the local compose network.
.DESCRIPTION
    An allowlist, not a denylist. A denylist of "things that look like Azure" is a guess about
    hostnames that will be wrong the first time someone self-hosts UAT on a VM with an ordinary name;
    an allowlist of local spellings is a closed set that can be read and checked.
#>
function Test-IsLocalSqlServer {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Server)

    if ([string]::IsNullOrWhiteSpace($Server)) { return $false }

    # Strip the instance/port suffix: 'localhost,1433' and 'localhost\SQLEXPRESS' are both local.
    $host_ = $Server.Split(',')[0].Split('\')[0].Trim().Trim('"').ToLowerInvariant()

    # 'mssql' is the compose service name, reachable only from inside the compose network -- which is
    # where the app runs, and is local by construction.
    $local = @('localhost', '127.0.0.1', '::1', '.', '(local)', '(localdb)', 'mssql', $env:COMPUTERNAME)

    foreach ($candidate in $local) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and $host_ -eq $candidate.ToLowerInvariant()) {
            return $true
        }
    }

    return $false
}

<#
.SYNOPSIS
    Throws unless the target is unmistakably the local development database.
.DESCRIPTION
    Four independent conditions, all of which must hold. Each one alone would be defeatable:

      1. ASPNETCORE_ENVIRONMENT is Development (or unset, which means Development locally).
      2. The database is named GymOS_Local exactly.
      3. The database is not GymOS_UAT or GymOS_Production -- implied by 2, asserted separately so
         the failure message can say which environment was refused rather than a generic mismatch.
      4. The server is local.

    Condition 4 is the one that catches the case the others cannot: someone creates a database called
    GymOS_Local on the UAT server. The name would pass, and the write would still land on shared
    infrastructure.
#>
function Assert-LocalDatabaseTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ConnectionString,
        [Parameter()][string] $Operation = 'This operation'
    )

    $environment = if ([string]::IsNullOrWhiteSpace($env:ASPNETCORE_ENVIRONMENT)) {
        'Development'
    }
    else {
        $env:ASPNETCORE_ENVIRONMENT
    }

    if ($environment -ine 'Development') {
        throw "$Operation is local-development only, but ASPNETCORE_ENVIRONMENT is '$environment'. " +
        "Refusing to run. Unset it, or set it to Development, and only ever against $script:LocalDatabase."
    }

    $database = Get-SqlDatabaseName -ConnectionString $ConnectionString
    if ([string]::IsNullOrWhiteSpace($database)) {
        throw "$Operation cannot run: the connection string names no database, so there is no way to " +
        "tell which environment it would affect. Refusing to run."
    }

    if ($database -ieq $script:UatDatabase) {
        throw "$Operation is refusing to run against '$database' -- that is the shared UAT database. " +
        "These scripts only ever touch $script:LocalDatabase."
    }

    if ($database -ieq $script:ProductionDatabase) {
        throw "$Operation is refusing to run against '$database' -- that is the PRODUCTION database. " +
        "These scripts only ever touch $script:LocalDatabase."
    }

    if ($database -ine $script:LocalDatabase) {
        throw "$Operation only runs against '$script:LocalDatabase', but the connection string targets " +
        "'$database'. Refusing to run rather than guessing whether that database is safe to modify."
    }

    $server = Get-SqlServerName -ConnectionString $ConnectionString
    if (-not (Test-IsLocalSqlServer -Server $server)) {
        throw "$Operation is refusing to run: the database is named '$database' but the server is " +
        "'$server', which is not local. A database called $script:LocalDatabase on a shared or cloud " +
        "server is still shared infrastructure. Point this at the local Docker sidecar."
    }

    Write-Verbose "Target verified: $database on $server (environment $environment)."
}

<#
.SYNOPSIS
    Runs a native command, failing on its exit code rather than on its stderr.
.DESCRIPTION
    Windows PowerShell turns a native command's stderr into ErrorRecords, and under
    $ErrorActionPreference = 'Stop' that makes any stderr output terminating. `docker compose` writes
    its ordinary progress there -- "Container gymos-mssql Running" -- so a working compose command
    kills a strict script. It only shows up when something merges the streams, which is exactly the
    condition a caller creates by piping the script's output, so it is a latent failure rather than an
    obvious one.

    Exit code is the signal that actually means failure for these tools, so that is what is checked.
.EXAMPLE
    Invoke-Native -FailureMessage 'Migrations failed' -- docker compose up --exit-code-from migrator migrator
#>
function Invoke-Native {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FailureMessage,
        [Parameter(Mandatory, ValueFromRemainingArguments)][string[]] $Command
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command[0] @($Command[1..($Command.Length - 1)])
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($code -ne 0) {
        throw "$FailureMessage (exit code $code)."
    }
}

Export-ModuleMember -Function ConvertFrom-SqlConnectionString, Get-SqlDatabaseName, Get-SqlServerName,
Test-IsLocalSqlServer, Assert-LocalDatabaseTarget, Invoke-Native
