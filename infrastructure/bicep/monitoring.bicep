// Log Analytics — the whole observability stack, deliberately.
//
// Container Apps ships container stdout here, and the application writes compact JSON outside
// Development precisely so these lines are queryable rather than merely readable (AD-38). No
// Application Insights resource and no third-party platform: an OTLP exporter is already wired in
// the application and inert until `OpenTelemetry:OtlpEndpoint` is set, so adding one later is
// configuration rather than code. Adding one now would be a second place to look during an incident.
//
// Queries that answer real questions: docs/runbooks/INCIDENT-RESPONSE.md §4.

param location string
param tags object
param workspaceName string
param retentionInDays int

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    features: {
      // Ingestion is by shared key from the Container Apps environment, so local auth stays on.
      // Turning it off requires the environment to authenticate by identity instead; that is a
      // change worth making, and it is not this file's job to make it silently.
      disableLocalAuth: false
    }
  }
}

output workspaceId string = workspace.id
output customerId string = workspace.properties.customerId
#disable-next-line outputs-should-not-contain-secrets
output sharedKey string = workspace.listKeys().primarySharedKey
