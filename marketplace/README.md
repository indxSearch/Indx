# Indx - Azure Marketplace package

This folder holds the artifacts for shipping `IndxServer` as an **Azure Managed Application** on the Azure Marketplace. Each customer purchase provisions one dedicated ASP.NET Core instance into the customer's subscription, locked into a managed resource group.

Local `dotnet run` is unaffected - none of these files are referenced by the project build.

## Files

| File | Purpose |
|------|---------|
| `main.bicep` | Resource template. Compiled to `mainTemplate.json` for the Marketplace package. |
| `createUiDefinition.json` | Customer-facing deployment wizard. Collects admin email, OAuth keys, JWT signing key, etc. |
| `viewDefinition.json` | Post-deploy customer dashboard shown inside the managed resource group. |

## What gets provisioned per customer

- Windows App Service Plan (default `P1v3`), as cloud.indx.co runs
- App Service (Windows, .NET 10, 64-bit) with system-assigned managed identity
- All data in `D:\home\data`, the app's persistent storage outside `wwwroot` (`identity.db`, `indx.db`, statistics, settings, licence, backups), covered by App Service backups
- Key Vault holding: JWT signing key, initial admin password, ACS connection string, OAuth client secrets
- Application Insights (workspace-based) + Log Analytics workspace
- Diagnostic settings shipping App Service logs to the workspace
- Role assignment: App Service identity -> Key Vault Secrets User

## Plans & capacity

This package deploys as the **Managed edition** (`Indx__Edition=Managed`). Per-instance licensing is hidden (the licence arrives on its own, below) and features are gated by the purchased plan via `Indx__Plan` (fixed per marketplace plan, not a customer input):

| Plan | Marketplace visibility | What it includes |
|------|------------------------|------------------|
| Indx Cloud Search (Free) | Public, $0 | Single team, single user; boost rules **without** a schedule window |
| Indx Cloud Search (Professional) | Public, flat monthly | Multiple teams & users; **scheduled** boost rules |
| Enterprise | Private plan (per-customer) | Same features as Professional, with custom terms / SLA (monthly or annual) |

The enforceable differences today are exactly two: **multiple teams/users** and **boost-rule scheduling** (the `ActiveFrom`/`ActiveUntil` window). Everything else — search, indexing, filters, facets, boosts, MCP, social (Google/Microsoft) login — is the same on every plan. Add tiers here only when a feature is actually built and gated via `EditionFeature`.

Plans differ by **features**, never by document/dataset capacity (there are no capacity caps; data durability/backups are handled by Azure storage, not gated). "Upgrading" means deploying the higher plan — there is no in-app plan switch.

### Lifting the 100K document cap

The engine enforces a 100,000-document limit unless a valid `.license` is present. No build carries
a licence any more (16 Oct 2026, after one was deployed by mistake), so a Managed instance fetches
one, like a self-hosted instance with a licence token, and the portal (license.indx.co) delivers
the token without anyone typing it:

1. Partner Center's notification address for the offer is
   `https://license.indx.co/api/marketplace?sig=<Marketplace:NotificationSig>`. Azure posts to it,
   with `/resource` added, when a customer deploys, updates or deletes the app.
2. The portal reads the application back from Azure (a notification is not signed), and on a
   succeeded deployment makes a Managed licence: its tier from the plan id (`enterprise` in it is
   Enterprise, `pro` is Pro, else Free), its own token.
3. With the publisher access the offer grants on the managed resource group, the portal writes
   the token into the App Service's app settings as `Indx__LicenseToken`. The app restarts,
   fetches `indx.license` into the data folder, and refreshes it daily. The file runs 90 days from
   each fetch, so a deleted deployment's licence lapses within that time.
4. Observr's subscription check reads the same token: Professional and Enterprise include it.

What the portal needs, all in its own configuration: `Marketplace:NotificationSig` and the
publisher app registration (`TenantId`, `ClientId`, `ClientSecret`). That registration is the
principal named in the offer's publisher access (Contributor on the managed resource group is
enough to read the site and write its settings). The admin page (Admin > Licenses) lists each
deployment and whether its token was written; a failed write is answered with a 500, which makes
Azure send the notification again for up to 10 hours.

The token is an app setting outside the template, so a redeploy of this template over an existing
instance must not drop it: the template's `appsettings` resource replaces the whole set.

## Build and package

One package per plan, built by `deploy/marketplace-package.sh` (in the private repo, beside the
deploy scripts) and uploaded to that plan's Technical configuration in Partner Center:

```bash
deploy/marketplace-package.sh --plan Professional --version 1.0.0
```

The package holds, at its root, `mainTemplate.json` (this folder's `main.bicep`, compiled, with the
plan as its default), `createUiDefinition.json`, `viewDefinition.json` and `app.zip`, which is
IndxServer itself. The template installs `app.zip` from the package (`ZipDeploy`, addressed through
`_artifactsLocation`, which Azure sets), so a customer gets the version the package was certified
with and nothing of ours has to stay downloadable. The script refuses a build that:

- is not built against the published IndxSearchLib (its `Indx.dll` must be the NuGet package's),
- carries the brand font (licensed for Indx's own sites; a customer's instance renders in Geist),
- carries `IndxData` or any `.license` file.

About 66 MB; a managed app definition takes at most 120 MB.

## One-time publisher setup

1. **Partner Center commercial account**, enrolled for Azure Application offers.
2. **The publisher identity** is the `Indx Marketplace` app registration in Indx's tenant. Its
   service principal is named in each plan's *Publisher management access* with **Contributor**:
   enough to read the site and write its app settings, which is all the licence portal does. This
   cannot be changed once the offer is live.
3. **Notification endpoint URL** (each plan's Technical configuration):
   `https://license.indx.co/api/marketplace?sig=<Marketplace:NotificationSig>`. Azure adds `/resource`.
4. **Customer access**: restrict with deny assignments, so the managed resources cannot be changed
   from the customer's side; read stays available.
5. The licence portal's `Marketplace` settings (sig and the registration's tenant, client id and
   secret) set on `indxlicenseportal`.

To try a package in our own subscription first, without Partner Center, create a service catalog
definition from it (`az managedapp definition create --package-file-uri <blob url>` with the
publisher principal as an authorization) and deploy that. The notification and licence steps then
need the definition's own `notificationPolicy`.

## Test locally before submission

ARM Template Toolkit (`arm-ttk`) and the createUiDefinition Sandbox are the two essential pre-flight checks:

1. **Validate Bicep**:
   ```bash
   az deployment group validate \
     --resource-group <test-rg> \
     --template-file main.json \
     --parameters appName=indxtest adminEmail=test@example.com adminInitialPassword='<strong>' jwtSigningKey='<32+ chars>'
   ```

2. **Test the UI** in the Sandbox: https://portal.azure.com/?feature.customportal=false#blade/Microsoft_Azure_CreateUIDef/SandboxBlade
   - Paste `createUiDefinition.json` and click Preview.
   - Step through the wizard and verify outputs match `main.bicep` parameter names exactly.

3. **End-to-end deploy** into a throwaway subscription:
   ```bash
   az deployment group create \
     --resource-group <test-rg> \
     --template-file main.json \
     --parameters @test-params.json
   ```

4. Hit `https://<appName>.azurewebsites.net/health` and `/swagger` to confirm the app starts.

## Pre-launch checklist

App-side hardening (in `IndxServer/Program.cs`) that must land before going live:

- [x] Refuse to start in `Production` if `Jwt:Key` equals the placeholder value.
- [ ] Force admin password change on first login.
- [ ] Lock CORS to the deployed hostname in `Production`.
- [ ] Add `app.MapHealthChecks("/health")`.
- [ ] Wire `AddApplicationInsightsTelemetry()` from `APPLICATIONINSIGHTS_CONNECTION_STRING`.
- [x] Startup logic auto-fetches the license from the Indx portal when a license token is configured (see `LicenseBootstrapper`), Managed included; the portal writes the token (see above).
- [ ] Switch EF `EnsureCreated()` to `Database.Migrate()` so schema changes can ship to existing customers.

Marketplace-side:

- [ ] Privacy policy + Terms of Use URLs published.
- [ ] Logos (small/medium/large), screenshots, marketing copy ready in Partner Center.
- [ ] Support contact + SLA documented.
- [ ] Pricing model decided (publisher fee per managed-RG-hour or zero).
- [ ] Lead-gen connector configured.

## Updating an existing customer deployment

Bump the version, recompile, re-upload the zip, then update the application definition. Customers receive an update prompt in their managed application blade.

```bash
az managedapp definition update \
  --name "IndxServer" \
  --resource-group "indx-marketplace-rg" \
  --package-file-uri "https://<your-storage>.blob.core.windows.net/packages/indxcloud-managed-app-1.1.0.zip"
```

Schema changes between versions must be backward-compatible during the rolling update window.
