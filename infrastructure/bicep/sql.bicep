// Azure SQL — one server, one database per environment.
//
// The database NAME is load-bearing, not cosmetic. DatabaseEnvironmentGuard maps environments to
// database names in application code, with no bypass switch, so GymOS_UAT and GymOS_Production are
// the only two names a deployed host will open. Renaming a database here breaks the application on
// purpose. docs/Architecture/SECURITY.md §4.
//
// What this file deliberately does NOT contain:
//   * SQL logins or passwords. Authentication is Microsoft Entra — the target state is managed
//     identity with no database password anywhere (docs/Infrastructure/AZURE.md §5), and a template
//     that took an administratorLoginPassword parameter would be a place for a secret to land.
//   * The role split. db_ddladmin for the migrator, reader/writer/EXECUTE for the app, and schema
//     ownership are T-SQL, applied by docs/runbooks/managed-sqlserver-roles.md and verified by the
//     queries at the end of it. Bicep cannot express them and must not appear to.
//   * Schema. EF Core migrations are the only thing that changes a GymOS database, in every
//     environment. This creates an empty database and stops.
//   * READ_COMMITTED_SNAPSHOT. It is `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON` and has no
//     ARM property, so it cannot be set from here at all. It IS required — the concurrency handling
//     was designed against MVCC semantics, and without it readers block writers and the retry
//     behaviour differs from every test. docker/sqlserver/initdb/00-database.sql sets it locally;
//     on a managed instance it is a post-create statement in the roles runbook.

param location string
param tags object
param serverName string
param databaseName string
param adminObjectId string
param adminLogin string

resource server 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    // Entra-only. No SQL administrator login exists to leak, and password authentication for the
    // *administrator* is off — the application principals are a separate matter and are being moved
    // to identity too.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: adminLogin
      sid: adminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: {
    // General Purpose serverless, 2 vCore ceiling. UAT's actual tier was chosen by hand and has NOT
    // been read back into this file — reconcile before applying (README §2).
    name: 'GP_S_Gen5_2'
    tier: 'GeneralPurpose'
  }
  properties: {
    // Latin1_General_100_CI_AS: fixed at creation, matched by docker/sqlserver/initdb/00-database.sql
    // and by the database project's ModelCollation. Case-insensitive, accent-sensitive. Changing it
    // changes query results and index behaviour, so it is set once and never revisited.
    collation: 'Latin1_General_100_CI_AS'
    readScale: 'Disabled'
    zoneRedundant: false
    autoPauseDelay: 60
    minCapacity: json('0.5')
  }
}

// Azure services only. A developer's machine reaching UAT needs an explicit IP rule added by hand
// and removed afterwards — occasionally legitimate, never routine (docs/Infrastructure/AZURE.md §4).
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: server
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverId string = server.id
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output databaseId string = database.id
