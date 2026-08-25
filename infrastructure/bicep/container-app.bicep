// Container Apps environment and the API container app.
//
// What this file sets: the image, the identity, ingress, scale, and the probes. What it deliberately
// leaves alone: the application's own configuration — the connection string, the JWT signing key,
// the seed administrator. Those are deployment state rather than build state, and the deploy
// workflow does not reassert them either, for the same reason: a deployment that rewrote them on
// every push would silently undo any correction made against the running app. They are set once, by
// hand, and nothing in Git records their values (docs/Infrastructure/AZURE.md §7).
//
// Consequence worth stating plainly: applying this template to an app that already has those
// settings will REMOVE them unless they are added here first. That is the single biggest reason
// README §2's what-if reconciliation is not optional.

param location string
param tags object
param containerAppName string
param environmentName string
param image string
param aspNetCoreEnvironment string
param workspaceCustomerId string
@secure()
param workspaceSharedKey string
param apiIdentityId string

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspaceCustomerId
        sharedKey: workspaceSharedKey
      }
    }
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    // User-assigned, so the identity outlives any single revision and can be granted database
    // access once rather than per deployment. The API identity only — the migrator identity belongs
    // to the migration job, and giving this app both would undo the role split.
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        // The rate limiter partitions on client IP and every audit row records it, so the real
        // address has to survive the proxy. The application corrects it from forwarded headers
        // (ForwardedHeaders must be on in a deployed environment) before anything reads it.
        allowInsecure: false
      }
      // GHCR, not ACR — the registry is GitHub's and the pull secret is configured on the app by
      // hand. Deliberately not declared here: it is a credential, and this file holds none.
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: aspNetCoreEnvironment
            }
            {
              name: 'ASPNETCORE_HTTP_PORTS'
              value: '8080'
            }
          ]
          probes: [
            {
              // Liveness on /health/live, which runs NO checks. That is the point: a liveness probe
              // that depends on the database restarts a healthy process every time the database
              // hiccups, turning a brief dependency failure into a restart loop.
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 15
              failureThreshold: 3
            }
            {
              // Readiness on /health/ready, which includes the database and pending-migration state.
              // This is what decides whether a revision takes traffic, and what the deployment
              // pipeline verifies. initialDelaySeconds covers first-boot seeding.
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              initialDelaySeconds: 20
              periodSeconds: 10
              failureThreshold: 6
            }
          ]
        }
      ]
      scale: {
        // minReplicas 1, not 0. A scale-to-zero app pays cold-start on the first request after idle,
        // and its startup work — seeding, the isolation validator, both maintenance sweeps — is not
        // work to run on a user's request path.
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output fqdn string = containerApp.properties.configuration.ingress.fqdn
