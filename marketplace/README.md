# Indx Cloud API - Azure Marketplace package

This folder holds the artifacts for shipping `IndxServer` as an **Azure Managed Application** on the Azure Marketplace. Each customer purchase provisions one dedicated ASP.NET Core instance into the customer's subscription, locked into a managed resource group.

Local `dotnet run` is unaffected - none of these files are referenced by the project build.

## Files

| File | Purpose |
|------|---------|
| `main.bicep` | Resource template. Compiled to `mainTemplate.json` for the Marketplace package. |
| `createUiDefinition.json` | Customer-facing deployment wizard. Collects admin email, OAuth keys, JWT signing key, etc. |
| `viewDefinition.json` | Post-deploy customer dashboard shown inside the managed resource group. |

## What gets provisioned per customer

- Linux App Service Plan (default `P1v3`)
- App Service (Linux, .NET 10) with system-assigned managed identity
- Storage Account + Azure Files share mounted at `/home/data` (holds `identity.db`, `indx.db`, `indx.license`)
- Key Vault holding: JWT signing key, initial admin password, ACS connection string, OAuth client secrets
- Application Insights (workspace-based) + Log Analytics workspace
- Diagnostic settings shipping App Service logs to the workspace
- Role assignment: App Service identity -> Key Vault Secrets User

## Plans & capacity

This package deploys as the **Managed edition** (`Indx__Edition=Managed`). Per-instance licensing is hidden — there are no `.license` files to manage — and features are gated by the purchased plan via `Indx__Plan` (fixed per marketplace plan, not a customer input):

| Plan | Marketplace visibility | What it includes |
|------|------------------------|------------------|
| Indx Cloud Search (Free) | Public, $0 | Single team, single user; boost rules **without** a schedule window |
| Indx Cloud Search (Professional) | Public, flat monthly | Multiple teams & users; **scheduled** boost rules |
| Enterprise | Private plan (per-customer) | Same features as Professional, with custom terms / SLA (monthly or annual) |

The enforceable differences today are exactly two: **multiple teams/users** and **boost-rule scheduling** (the `ActiveFrom`/`ActiveUntil` window). Everything else — search, indexing, filters, facets, boosts, MCP, social (Google/Microsoft) login — is the same on every plan. Add tiers here only when a feature is actually built and gated via `EditionFeature`.

Plans differ by **features**, never by document/dataset capacity (there are no capacity caps; data durability/backups are handled by Azure storage, not gated). "Upgrading" means deploying the higher plan — there is no in-app plan switch.

### Lifting the 100K document cap

The engine enforces a 100,000-document limit unless a valid `.license` is present (independent of edition/plan). The Managed package lifts it by **bundling a no-expiry Indx master license**, and the mechanism needs **no env var or bicep change** — it rides the existing build pipeline:

- The master license is generated once via the Eziriz `LicenseGenerator` (`LicensedTo = "Indx Managed"`, no expiry) and placed at `IndxData/indx-managed.license`.
- `IndxServer.csproj` already publishes it: `<Content Include="IndxData\*.license" CopyToPublishDirectory="PreserveNewest" />`.
- The engine auto-detects `./IndxData/*.license` at startup → cap lifted. No `Indx:LicenseFile` setting required.
- `*.license` is gitignored, so the file is **never committed** to the public repo. The Managed package build must have it present in `IndxData/` (developer machine, or CI pulling it from a secret); a clean public/self-host build has no license and runs at the 100K cap (self-hosters add their own free dev license from indx.co).

Why this is safe to ship universally: payment is enforced by **Azure** (unpaid subscription → Microsoft suspends it → the app stops), and unlimited documents is already free for anyone via the indx.co developer license, so the bundled license has negligible leak value. Keep the app-binaries blob private/SAS regardless.

## Build and package

Bicep is compiled to ARM JSON before zipping:

```bash
az bicep build --file main.bicep
# Produces main.json next to main.bicep
```

Package the three files into a zip for Partner Center:

```bash
zip indxcloud-managed-app.zip main.json createUiDefinition.json viewDefinition.json
```

The zip is uploaded to a publisher-controlled Azure Storage container (with public read access on the blob), and the blob URL is referenced by the `applicationDefinition` resource.

## One-time publisher setup

These steps happen once per publisher tenant, outside this folder:

1. **Microsoft Partner Center commercial account** - sign up at partner.microsoft.com. Required to publish on Marketplace.
2. **Capture the publisher Entra principal ID** that should retain management access into customer-locked resource groups. Typically a security group containing your support team. Get the object ID via:
   ```bash
   az ad group show --group "Indx Support" --query id -o tsv
   ```
3. **Create the application definition** in your publisher tenant pointing at the uploaded zip. This is the resource that ties together the package, the publisher principal, and the access role:
   ```bash
   az managedapp definition create \
     --name "IndxServer" \
     --location "westeurope" \
     --resource-group "indx-marketplace-rg" \
     --lock-level "ReadOnly" \
     --display-name "Indx Cloud API" \
     --description "Single-tenant search engine for JSON data" \
     --authorizations "<publisher-principal-id>:8e3af657-a8ff-443c-a75c-2fe8c4bcb635" \
     --package-file-uri "https://<your-storage>.blob.core.windows.net/packages/indxcloud-managed-app.zip"
   ```
   The role GUID `8e3af657-...` is the built-in **Owner** role; downgrade to **Contributor** (`b24988ac-...`) if you don't need to manage role assignments inside the customer RG.

4. **Submit the offer** in Partner Center under Marketplace offers > New offer > Azure Application > Managed Application plan. Upload the zip, link to your application definition, set pricing, and publish.

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

App-side hardening (in `IndxCloudApi/Program.cs`) that must land before going live:

- [x] Refuse to start in `Production` if `Jwt:Key` equals the placeholder value.
- [ ] Replace hardcoded `admin@indx.co` / `Admin123!@#` seed with `Identity:AdminEmail` / `Identity:AdminInitialPassword` config.
- [ ] Force admin password change on first login.
- [ ] Lock CORS to the deployed hostname in `Production`.
- [ ] Add `app.MapHealthChecks("/health")`.
- [ ] Wire `AddApplicationInsightsTelemetry()` from `APPLICATIONINSIGHTS_CONNECTION_STRING`.
- [x] Startup logic auto-fetches the license to `Indx:LicenseFile` from the Indx portal when a license token is configured (see `LicenseBootstrapper`).
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
