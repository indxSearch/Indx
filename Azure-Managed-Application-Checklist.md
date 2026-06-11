# Azure Marketplace Managed Application Checklist

Target: Azure Marketplace (Azure app store) listing for IndxCloudApi as an Azure Managed Application.
Deployment model: Publisher-managed, customer-deployed into their own subscription via Marketplace.

---

## 1. Deployment Package (Blob Storage)

Azure Marketplace requires a `.zip` uploaded to a publicly readable Azure Blob container. The zip contains the ARM template and UI definition that Marketplace uses to deploy into the customer's subscription.

- [ ] Compile `marketplace/main.bicep` to ARM JSON: `az bicep build --file marketplace/main.bicep --outfile marketplace/mainTemplate.json`
- [ ] Validate ARM template: `az deployment group validate --template-file marketplace/mainTemplate.json`
- [ ] Validate `createUiDefinition.json`: use the [Azure portal sandbox](https://portal.azure.com/?feature.customPortal=false#blade/Microsoft_Azure_CreateUIDef/SandboxBlade)
- [ ] Create a zip of `mainTemplate.json` + `createUiDefinition.json` (+ `viewDefinition.json` if used)
- [ ] Create an Azure Blob Storage container with public read access for the package
- [ ] Upload the zip and record the public blob URL — this is the `packageFileUri` in Partner Center
- [ ] Set up a versioned folder structure in blob storage (e.g. `/packages/2.0.0/app.zip`) so future versions don't overwrite current
- [ ] Add CI step to rebuild and re-upload the zip on release

---

## 2. Publisher Authorization

Azure Managed Applications require the publisher (Indx/Bionic AS) to declare which service principal gets what RBAC role in the customer's managed resource group. Without this you cannot push updates, monitor, or provide support.

- [ ] Create a service principal in the Indx Azure AD tenant for publisher access
- [ ] Add `authorizations` block to `mainTemplate.json`:
  ```json
  "authorizations": [
    {
      "principalId": "<indx-service-principal-object-id>",
      "roleDefinitionId": "<built-in-role-id>"
    }
  ]
  ```
- [ ] Choose the minimum required role (Contributor for updates; Reader for monitoring-only)
- [ ] Document the service principal and rotate its credentials on a schedule
- [ ] Confirm the managed resource group naming convention (set via `managedResourceGroupId` in the offer)

---

## 3. Partner Center / Marketplace Listing

All Marketplace offers are created and managed in [Microsoft Partner Center](https://partner.microsoft.com/dashboard).

- [ ] Enroll in the Microsoft AI Cloud Partner Program (if not already done)
- [ ] Create a new offer: **Azure Application → Managed Application**
- [ ] Complete offer listing:
  - [ ] Name, short description, long description
  - [ ] Search keywords (search, full-text search, JSON, BM25)
  - [ ] Categories (e.g. Developer Tools, AI + Machine Learning)
  - [ ] Offer logo (216×216, 300×300, 1280×720 PNGs required)
  - [ ] Screenshots (up to 5, minimum 1, 1280×720)
  - [ ] Demo video link (optional but improves conversion)
- [ ] Set offer availability: which Azure regions to support
- [ ] Set supported cloud: Azure Global (+ Azure Government if required)
- [ ] Add Terms of Service URL
- [ ] Add Privacy Policy URL
- [ ] Add Support contact (email, URL, phone)
- [ ] Add Engineering contact
- [ ] Set CSP (Cloud Solution Provider) resell policy

---

## 4. Plans & Pricing

- [ ] Create at least one plan (e.g. "Standard", "Professional")
- [ ] Set pricing model: Free, Flat rate, or Per-user
- [ ] Set markets (countries where the offer is available)
- [ ] Configure technical configuration per plan:
  - [ ] Set `packageFileUri` to the blob URL from section 1
  - [ ] Set `deploymentMode`: `Complete` or `Incremental`
  - [ ] Set minimum `applicationDefinitionVersion`
- [ ] If offering a free trial, configure trial duration and limits
- [ ] Confirm App Service plan SKU options in `createUiDefinition.json` match what you want to offer

---

## 5. App Package — Bicep / ARM Completeness

- [ ] Add `authorizations` array to mainTemplate (see section 2)
- [ ] Add `managedResourceGroupId` parameter (Marketplace injects this automatically — verify it's referenced correctly)
- [ ] Review all parameter defaults — nothing sensitive should have a hardcoded default
- [ ] Confirm `createUiDefinition.json` outputs map 1:1 to mainTemplate parameters
- [ ] Remove `appsettings.production.json` from source control — production config should come exclusively from Key Vault / environment variables set by Bicep
- [ ] Upgrade Storage Account replication from `Standard_LRS` to `Standard_ZRS` or `Standard_GRS` (protects customer data from zone/region failure)
- [ ] Add a backup policy or lifecycle rule on the Azure Files share (SQLite databases)

---

## 6. Application Binary — Publishing to App Service

The Bicep template provisions the App Service but the application binary must come from somewhere. Options: zip deploy via startup script, container registry, or a pre-built zip in blob storage.

- [ ] Decide on binary delivery method:
  - **Option A (Recommended):** Store a versioned publish zip in blob storage; reference it in Bicep via `WEBSITE_RUN_FROM_PACKAGE` app setting
  - **Option B:** Docker image in Azure Container Registry; set `linuxFxVersion` in Bicep
- [ ] Add a CI/CD pipeline (GitHub Actions or Azure Pipelines) that:
  - [ ] Builds and tests the solution (`dotnet build`, `dotnet test`)
  - [ ] Publishes the app (`dotnet publish -c Release`)
  - [ ] Zips the output and uploads to versioned blob path
  - [ ] Updates the blob URL reference for the next marketplace package build
- [ ] Confirm the publish zip includes `IndxData/*.license` (already in csproj as `PreserveNewest`)

---

## 7. Security & Hardening

- [ ] Add rate limiting to `/api/search` and `/api/login` (ASP.NET Core `AddRateLimiter`, sliding window)
- [ ] Add CORS configuration with a customer-configurable `AllowedOrigins` setting in Bicep / createUiDefinition
- [ ] Add security response headers middleware:
  - `X-Frame-Options: DENY`
  - `X-Content-Type-Options: nosniff`
  - `Referrer-Policy: strict-origin-when-cross-origin`
  - `Content-Security-Policy` (scope to Blazor's needs)
- [ ] Confirm `Identity:RequireConfirmedEmail` is forced `true` when registration mode is `Open` or `EmailDomain`
- [ ] Verify JWT audience (`Jwt:Audience`) is validated — currently only issuer is checked

---

## 8. Health & Observability

- [ ] Extend `/health` endpoint to check SQLite database connectivity (not just return static `Healthy`)
- [ ] Add a `/health/ready` probe (dependencies ready) distinct from `/health/live` (process alive)
- [ ] Configure App Service health check path in Bicep (`healthCheckPath: '/health'`) — confirm this is present
- [ ] Verify Application Insights connection string is correctly injected and telemetry flows end-to-end
- [ ] Add custom Application Insights events for key actions (index created, search performed, user registered)

---

## 9. Update / Upgrade Path

- [ ] Document how publisher-initiated updates work for existing customer deployments
- [ ] Confirm EF Core migrations do not block startup on large databases (wrap in try/catch with fallback logging)
- [ ] Add `WEBSITE_RUN_FROM_PACKAGE` with a versioned URL so updates are atomic (swap package, restart)
- [ ] Test zero-downtime deployment: App Service deployment slots or rolling restart

---

## 10. Documentation & Support

- [ ] Add Terms of Service document (required for Marketplace listing)
- [ ] Add Privacy Policy document (required for Marketplace listing)
- [ ] Add a support page or URL (customers need a way to file support tickets)
- [ ] Update `marketplace/README.md` with end-to-end deployment instructions for the publisher (how to build, package, and upload a new version)
- [ ] Document the update flow for customers (what happens when publisher pushes a new version)
- [ ] Add a `CHANGELOG.md` — Marketplace reviewers look for version history

---

## 11. Pre-Submission Validation

- [ ] Run the [Azure Marketplace offer validation tool](https://docs.microsoft.com/azure/marketplace/azure-app-solution-best-practices) against the package zip
- [ ] Test full deployment from the Marketplace sandbox (Partner Center → Preview audience)
- [ ] Verify the customer experience end-to-end: deploy → login → create index → search
- [ ] Test all registration modes: Open, EmailDomain, Closed
- [ ] Test license bootstrap via portal token (`Indx:LicenseToken`; the download URL is hardcoded, legacy `Indx:LicenseDownloadUrl` is no longer read)
- [ ] Confirm health check returns 200 after deployment (App Service probe)
- [ ] Confirm Application Insights receives telemetry after deployment

---

## Status Key

| Symbol | Meaning |
|--------|---------|
| `[ ]` | Not started |
| `[x]` | Complete |
| `[-]` | In progress |
| `[~]` | Deferred / out of scope for now |
