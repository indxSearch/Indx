// Indx - Azure Managed Application main template
//
// Provisions a single-tenant deployment of IndxServer for one customer.
// Resources are deployed into the customer's managed resource group.
//
// Compile to ARM JSON before packaging:
//   az bicep build --file main.bicep
//
// The resulting main.json goes into the marketplace .zip as mainTemplate.json, alongside
// createUiDefinition.json, viewDefinition.json and app.zip (IndxServer itself). Build the whole
// package with deploy/marketplace-package.sh, never by hand.

// ---------- Parameters ----------

@description('Base name used for all resources. 3-20 lowercase alphanumeric chars.')
@minLength(3)
@maxLength(20)
param appName string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('App Service plan SKU. P1v3 recommended for production.')
@allowed([
  'B2'
  'B3'
  'P0v3'
  'P1v3'
  'P2v3'
  'P3v3'
])
param skuName string = 'P1v3'

@description('Email address for the initial administrator account.')
param adminEmail string

@description('Initial administrator password. Must satisfy Identity policy: 8+ chars, upper, lower, digit, non-alphanumeric.')
@secure()
@minLength(12)
param adminInitialPassword string

@description('JWT signing key. Must be at least 32 characters of high-entropy randomness.')
@secure()
@minLength(32)
param jwtSigningKey string

@description('User registration mode.')
@allowed([
  'Open'
  'EmailDomain'
  'Closed'
])
param registrationMode string = 'Closed'

@description('Comma-separated list of allowed email domains when registrationMode = EmailDomain.')
param allowedEmailDomains string = ''

@description('Email provider for transactional mail.')
@allowed([
  'Console'
  'AzureCommunicationServices'
])
param emailProvider string = 'Console'

@description('Azure Communication Services connection string. Required when emailProvider = AzureCommunicationServices.')
@secure()
param acsConnectionString string = ''

@description('Verified ACS sender address. Required when emailProvider = AzureCommunicationServices.')
param acsFromAddress string = ''

@description('Display name shown as the email sender.')
param emailFromName string = 'Indx Authentication'

@description('Optional Google OAuth client ID. Leave blank to disable Google sign-in.')
param googleClientId string = ''

@description('Optional Google OAuth client secret.')
@secure()
param googleClientSecret string = ''

@description('Optional Microsoft OAuth client ID. Leave blank to disable Microsoft sign-in.')
param microsoftClientId string = ''

@description('Optional Microsoft OAuth client secret.')
@secure()
param microsoftClientSecret string = ''

@description('Subscription tier this package represents. Set per marketplace plan (Free / Professional public plans, Enterprise private plan) — NOT a customer input, so it is not collected in createUiDefinition. The release pipeline builds one package per plan with the matching value.')
@allowed([
  'Free'
  'Professional'
  'Enterprise'
])
param plan string = 'Free'

// Where Azure staged the package's own files, app.zip among them. Azure Marketplace fills both;
// the defaults are what the template test toolkit requires.
@description('Location of the package\'s files. Set by Azure; leave the default.')
param _artifactsLocation string = deployment().properties.templateLink.uri

@description('Access token for the package\'s files. Set by Azure; leave the default.')
@secure()
param _artifactsLocationSasToken string = ''

// ---------- Variables ----------

var uniqueSuffix = uniqueString(resourceGroup().id, appName)
var planName = '${appName}-plan'
var siteName = appName
var keyVaultName = 'kv-${take(uniqueSuffix, 16)}'
var workspaceName = '${appName}-logs'
var insightsName = '${appName}-ai'

// Built-in role IDs
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// ---------- Log Analytics + Application Insights ----------

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: insightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

// ---------- Key Vault ----------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource jwtSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'JwtKey'
  properties: {
    value: jwtSigningKey
  }
}

resource adminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'AdminInitialPassword'
  properties: {
    value: adminInitialPassword
  }
}

resource acsSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(acsConnectionString)) {
  parent: keyVault
  name: 'AcsConnectionString'
  properties: {
    value: acsConnectionString
  }
}

resource googleSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(googleClientSecret)) {
  parent: keyVault
  name: 'GoogleClientSecret'
  properties: {
    value: googleClientSecret
  }
}

resource microsoftSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(microsoftClientSecret)) {
  parent: keyVault
  name: 'MicrosoftClientSecret'
  properties: {
    value: microsoftClientSecret
  }
}

// ---------- App Service Plan + Web App ----------

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: {
    name: skuName
  }
  // Windows, as cloud.indx.co: the setup that has run IndxServer in production, with its data
  // in the app's own storage (D:\home) and covered by App Service's snapshots. Linux has never
  // run it (Notes/linux-and-containers-plan.md), and it would put the SQLite files on a mounted
  // Azure Files share, where file locking is not reliable.
  kind: 'app'
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  kind: 'app'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      use32BitWorkerProcess: false
      alwaysOn: true
      // The dashboard is Blazor Server over SignalR; without WebSockets it falls back to long
      // polling (slow, and a 90 s GET /_blazor in every log line).
      webSocketsEnabled: true
      ftpsState: 'Disabled'
      http20Enabled: true
      minTlsVersion: '1.2'
      healthCheckPath: '/health'
    }
  }
}

// App settings - separate resource so we can reference the Key Vault after role assignment
resource webAppSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: webApp
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    // App Service fronts the app with a proxy; this makes RemoteIpAddress the real client, which
    // the per-IP auth rate limit and the request logs depend on.
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    // Per-API-key rate limit (token bucket). Off in the self-host defaults; on for managed
    // instances. Plan tiers can vary the numbers later.
    RateLimits__Api__Enabled: 'true'
    RateLimits__Api__RequestsPerSecond: '50'
    RateLimits__Api__Burst: '200'

    // All data in D:\home\data, the app's persistent storage outside wwwroot, which an update
    // replaces: the databases, statistics, embeddings, instance settings, licence and backups
    // (IndxServer DataPaths). The shipped connection strings are relative, so they follow it.
    Indx__DataDirectory: 'D:\\home\\data'

    // JWT - sourced from Key Vault
    Jwt__Key: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=JwtKey)'
    // Opaque identifier baked into every issued API key (iss claim); the product was renamed
    // but this must stay, or every existing key is rejected.
    Jwt__Issuer: 'IndxCloudApi'

    // Identity / registration
    Registration__Mode: registrationMode
    Registration__AllowedEmailDomains: allowedEmailDomains
    Identity__RequireConfirmedEmail: emailProvider == 'AzureCommunicationServices' ? 'true' : 'false'
    Identity__AdminEmail: adminEmail
    Identity__AdminInitialPassword: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=AdminInitialPassword)'

    // Email
    Email__Provider: emailProvider
    Email__FromAddress: emailProvider == 'AzureCommunicationServices' ? acsFromAddress : 'noreply@indx.local'
    Email__FromName: emailFromName
    Email__AzureCommunicationServices__ConnectionString: emailProvider == 'AzureCommunicationServices' ? '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=AcsConnectionString)' : ''

    // OAuth
    Authentication__Google__ClientId: googleClientId
    Authentication__Google__ClientSecret: empty(googleClientSecret) ? '' : '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=GoogleClientSecret)'
    Authentication__Microsoft__ClientId: microsoftClientId
    Authentication__Microsoft__ClientSecret: empty(microsoftClientSecret) ? '' : '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=MicrosoftClientSecret)'

    // Edition + plan. Managed = Azure Marketplace deployment: per-instance licensing is hidden,
    // features are gated by the purchased plan (set above, fixed per marketplace plan).
    Indx__Edition: 'Managed'
    Indx__Plan: plan

    // Application Insights
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    XDT_MicrosoftApplicationInsights_Mode: 'Recommended'
  }
  dependsOn: [
    keyVaultRoleAssignment
    jwtSecret
    adminPasswordSecret
  ]
}

// Grant the Web App's managed identity read access to Key Vault secrets
// ---------- IndxServer itself ----------

// The app comes in the package (app.zip, built against the published IndxSearchLib), so the
// version a customer installs is the version the package was certified with, and nothing of ours
// has to stay downloadable. Copied into wwwroot once, after the settings, so the first start
// already sees the data folder, Key Vault and the edition. The data lives outside wwwroot.
// ZipDeploy is the extension Microsoft documents for a plain zip by URL (Kudu; the Functions ARM
// samples); Bicep's types list only MSDeploy, which expects a Web Deploy package, and onedeploy.
resource appCode 'Microsoft.Web/sites/extensions@2023-12-01' = {
  parent: webApp
  #disable-next-line BCP088
  name: 'ZipDeploy'
  properties: {
    packageUri: uri(_artifactsLocation, 'app.zip${_artifactsLocationSasToken}')
    appOffline: true
  }
  dependsOn: [
    webAppSettings
  ]
}

resource keyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, webApp.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Diagnostic settings - ship App Service logs to Log Analytics
resource webAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: webApp
  name: 'send-to-workspace'
  properties: {
    workspaceId: workspace.id
    logs: [
      {
        category: 'AppServiceHTTPLogs'
        enabled: true
      }
      {
        category: 'AppServiceConsoleLogs'
        enabled: true
      }
      {
        category: 'AppServiceAppLogs'
        enabled: true
      }
      {
        category: 'AppServiceAuditLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// ---------- Outputs ----------

output appUrl string = 'https://${webApp.properties.defaultHostName}'
output swaggerUrl string = 'https://${webApp.properties.defaultHostName}/swagger'
output healthUrl string = 'https://${webApp.properties.defaultHostName}/health'
output adminEmail string = adminEmail
output keyVaultName string = keyVault.name
output applicationInsightsName string = appInsights.name
