// User-assigned managed identities — one per role, never one shared.
//
// The API identity and the migrator identity are separate for the same reason gymos_app and
// gymos_migrator are separate SQL principals: the process that serves requests must not be able to
// change the schema. Combining them would make the role split a naming convention rather than a
// permission boundary. docs/Architecture/SECURITY.md §3.
//
// Creating them grants nothing. Each still needs a database user and its grants, applied per
// docs/runbooks/managed-sqlserver-roles.md:
//   CREATE USER [id-gymos-api-uat]      FROM EXTERNAL PROVIDER;   -- reader/writer/EXECUTE/VIEW DEFINITION
//   CREATE USER [id-gymos-migrator-uat] FROM EXTERNAL PROVIDER;   -- db_ddladmin + ALTER ANY SECURITY POLICY
// That step is deliberately not expressible here: Bicep cannot run T-SQL, and a template that
// appeared to grant database permissions while granting none would be worse than one that does not try.

param location string
param tags object
param apiIdentityName string
param migratorIdentityName string

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: apiIdentityName
  location: location
  tags: tags
}

resource migratorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: migratorIdentityName
  location: location
  tags: tags
}

output apiIdentityId string = apiIdentity.id
output apiIdentityClientId string = apiIdentity.properties.clientId
output migratorIdentityId string = migratorIdentity.id
output migratorIdentityClientId string = migratorIdentity.properties.clientId
