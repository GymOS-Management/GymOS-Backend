// GymOS environment — the whole environment as one deployment.
//
// ============================================================================================
// THIS HAS NEVER BEEN APPLIED. It is not yet the source of truth for anything.
//
// The UAT resources it describes were created by hand on 2026-08-24 and are still governed by
// docs/Infrastructure/AZURE.md. Deploying this as-is against UAT would very likely try to
// *change* live resources to match assumptions written here without reading them. Run
// `az deployment group what-if` and reconcile the differences first — infrastructure/bicep/README.md
// is the procedure, and it is not optional.
// ============================================================================================
//
// Scope: resource group. One resource group per environment (GymOS-UAT, GymOS-Production), so a
// deployment can never touch the other environment — the same reasoning that gives the two
// environments separate databases and separately-scoped GitHub secrets.
targetScope = 'resourceGroup'

@description('Environment discriminator. Drives every resource name and the database name, and those two must agree: DatabaseEnvironmentGuard refuses to boot a Staging host against anything but GymOS_UAT, and a Production host against anything but GymOS_Production.')
@allowed([
  'uat'
  'prod'
])
param environment string

@description('Azure region. EU only — GDPR data residency (confirm the choice with counsel, not with this file). UAT is swedencentral.')
param location string = resourceGroup().location

@description('Microsoft Entra object id of the group or user that administers the SQL server. Required: without an Entra admin the server accepts no token authentication at all, which is what both managed-identity access and schema drift detection depend on.')
param sqlAdminObjectId string

@description('Entra login name matching sqlAdminObjectId (a group name is preferable to a person).')
param sqlAdminLogin string

@description('Container image to run, by digest or commit SHA. NEVER a floating tag: production deploys the exact artifact UAT tested, and `latest` cannot express that.')
param containerImage string

@description('Log retention in days for the workspace. 30 is the Azure default; raise it deliberately rather than by accident.')
param logRetentionDays int = 30

// Names are derived rather than parameterised so two environments cannot accidentally be given the
// same one. The database name is the exception that matters most — it is matched in application code.
var databaseName = environment == 'prod' ? 'GymOS_Production' : 'GymOS_UAT'
var sqlServerName = 'gymos-${environment}'
var containerAppName = 'gymos-${environment}-api'
var containerEnvName = 'gymos-${environment}-env'
var workspaceName = 'gymos-${environment}-logs'
var apiIdentityName = 'id-gymos-api-${environment}'
var migratorIdentityName = 'id-gymos-migrator-${environment}'

// ASPNETCORE_ENVIRONMENT is 'Staging' for UAT. Not a typo and not tidiness: .NET has three
// environment names, the deployed non-production one is Staging, and appsettings.Staging.json is
// selected by it. The *environment* is called UAT; the framework's name for it is Staging.
var aspNetCoreEnvironment = environment == 'prod' ? 'Production' : 'Staging'

var tags = {
  application: 'GymOS'
  environment: environment
  managedBy: 'infrastructure/bicep (NOT YET RECONCILED — see README)'
}

module identities 'identities.bicep' = {
  name: 'identities'
  params: {
    location: location
    tags: tags
    apiIdentityName: apiIdentityName
    migratorIdentityName: migratorIdentityName
  }
}

module monitoring 'monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    tags: tags
    workspaceName: workspaceName
    retentionInDays: logRetentionDays
  }
}

module sql 'sql.bicep' = {
  name: 'sql'
  params: {
    location: location
    tags: tags
    serverName: sqlServerName
    databaseName: databaseName
    adminObjectId: sqlAdminObjectId
    adminLogin: sqlAdminLogin
  }
}

module containerApp 'container-app.bicep' = {
  name: 'container-app'
  params: {
    location: location
    tags: tags
    containerAppName: containerAppName
    environmentName: containerEnvName
    image: containerImage
    aspNetCoreEnvironment: aspNetCoreEnvironment
    workspaceCustomerId: monitoring.outputs.customerId
    workspaceSharedKey: monitoring.outputs.sharedKey
    apiIdentityId: identities.outputs.apiIdentityId
  }
}

output sqlServerFqdn string = sql.outputs.fullyQualifiedDomainName
output databaseName string = databaseName
output apiFqdn string = containerApp.outputs.fqdn
output apiIdentityClientId string = identities.outputs.apiIdentityClientId
output migratorIdentityClientId string = identities.outputs.migratorIdentityClientId
