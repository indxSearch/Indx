# IndxCloudApi

A self-hosted search service built on [Indx Search](https://indx.co). Blazor Server UI, HTTP API with JWT authentication, user management, and everything needed to run a multi-user search service on your own infrastructure.

## What's Included

- Blazor Server web interface with admin panel
- HTTP API with JWT authentication and API key management
- User registration, login, and account management
- Local accounts with optional Microsoft and Google OAuth
- Notifications system
- SQLite databases — no external database required
- Swagger UI at `/swagger`
- Automatic database migrations on startup

## Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Run Locally

```bash
git clone https://github.com/indxSearch/IndxCloudApi
cd IndxCloudApi
dotnet run
```

Open `https://localhost:5001` and register an account. The first user to register becomes the admin.

Works immediately with no configuration:
- Local username/password accounts
- SQLite databases auto-created in `./IndxData/`
- Emails logged to console (no SMTP required)

## First-Run Setup

After deploying and registering your admin account, go to **Admin → Settings** to configure:

- **Registration mode** — Open, domain-restricted, or closed
- **Email provider** — Switch from console logging to Azure Communication Services
- **OAuth** — Enable Microsoft and/or Google sign-in

Most settings can be changed through the UI without restarting the app.

## Configuration

The app works out of the box. For production, set these via environment variables or Azure App Service application settings.

### Email (Optional)

By default, emails are logged to the console. To send real emails via Azure Communication Services:

```
Email__Provider = AzureCommunicationServices
Email__AzureCommunicationServices__ConnectionString = endpoint=https://...;accesskey=...
Email__FromAddress = noreply@your-verified-domain.com
```

See [docs/EMAIL_SETUP.md](docs/EMAIL_SETUP.md) for full ACS setup instructions.

### OAuth (Optional)

OAuth providers are optional. Leave credentials empty to use local accounts only. When credentials are present, sign-in buttons appear on the login page automatically.

**Microsoft:**
```
Authentication__Microsoft__ClientId = your-client-id
Authentication__Microsoft__ClientSecret = your-client-secret
```

**Google:**
```
Authentication__Google__ClientId = your-client-id
Authentication__Google__ClientSecret = your-client-secret
```

See [docs/OAUTH_SETUP.md](docs/OAUTH_SETUP.md) for app registration instructions.

### Registration Mode

```
Registration__Mode = Closed
```

Options: `Open` (default), `EmailDomain` (restrict by domain), `Closed` (admin-only account creation).

For domain restrictions:
```
Registration__Mode = EmailDomain
Registration__AllowedDomains__0 = yourcompany.com
Registration__AllowedDomains__1 = partner.com
```

### Email Confirmation

```
Identity__RequireConfirmedEmail = true
```

OAuth users are auto-confirmed. Requires a real email provider when enabled.

## License

Indx Search enforces a **100,000 document limit** without a license file. A free extended license removing this limit is available from the Indx License Portal at [license.indx.co](https://license.indx.co).

### Manual placement

Place the `.license` file in `./IndxData/`:

```
IndxData/
├── identity.db
├── indx.db
└── indx-developer.license
```

The app detects any `.license` file in that directory on startup. Custom path via:
```
Indx__LicenseFile = /path/to/your.license
```

### Auto-fetch from the license portal

Instead of placing the file manually, IndxCloudApi can pull its license straight from the Indx License Portal ([license.indx.co](https://license.indx.co)) on startup. On your portal license page, create a license token, then configure:

```
Indx__LicenseToken = <token from your portal license page>
```

The download endpoint is **hardcoded** to the Indx portal (`https://license.indx.co/api/license/current`) — only Indx issues licenses, so it is not configurable. Auto-fetch is **token-gated**: with a token set, a **fresh license is fetched on every startup** (the portal rolls Pro/Free expiry forward) and written to `Indx:LicenseFile` (default `./IndxData/indx.license`). **Without a token, auto-fetch is a no-op** — the server never reaches out and relies on a manually placed file or free-tier mode. Fetch failures are logged but never fatal — the server keeps any existing file, or falls back to free-tier mode. Set the token via environment variable or Key Vault, not in `appsettings.json`.

> **Legacy:** the old `Indx__LicenseDownloadUrl` setting is no longer read — the URL is hardcoded and fetching is driven solely by `Indx__LicenseToken`. Remove it from any existing configuration.

## Deployment

### Azure App Service

1. Create an App Service with **.NET 10** Linux runtime
2. Deploy via zip deploy, GitHub Actions, or Visual Studio publish
3. Set `ASPNETCORE_ENVIRONMENT = Production` in Application Settings

The app creates its SQLite databases in `./IndxData/` on first run. This directory persists across redeployments.

For email and OAuth, set the relevant Application Settings listed in the Configuration section above. The app restarts automatically when settings change in Azure.

**Redirect URIs** — if using OAuth, add these to your app registrations:
```
https://your-app.azurewebsites.net/signin-microsoft
https://your-app.azurewebsites.net/signin-google
```

### Docker

```bash
docker build -t indxcloudapi .
docker run -p 8080:8080 \
  -v /your/data:/app/IndxData \
  -e Jwt__Key="your-secret-key-minimum-32-characters" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  indxcloudapi
```

Mount a volume to `/app/IndxData` to persist databases and license files across container restarts.

## API Access

1. Register and log in at `/Account/Login`
2. Navigate to **API Key** in the menu to generate a JWT token
3. Use the token in API requests:

```bash
curl -H "Authorization: Bearer <your-token>" https://localhost:5001/api/Search/myDataset
```

Full API reference available at `/swagger`.

## Database

Two SQLite files in `./IndxData/`:

- `identity.db` — user accounts, roles, and authentication (ASP.NET Core Identity)
- `indx.db` — application config, API keys, notifications

Migrations run automatically on startup. No manual migration steps needed.

> Indx search indexes are held in memory — `indx.db` stores configuration and metadata only, not search data.

## Local Development

```bash
# Run with hot reload
dotnet watch run

# Set secrets without editing appsettings.json
dotnet user-secrets set "Jwt:Key" "your-dev-key-minimum-32-characters"
dotnet user-secrets set "Authentication:Microsoft:ClientId" "your-client-id"
dotnet user-secrets set "Email:Provider" "Console"

# Run tests
dotnet test
```

## Related Projects

- [IndxCloudLoader (C#)](https://github.com/indxSearch/IndxCloudLoader) — CLI tool for bulk-loading JSON into IndxCloudApi
- [IndxNodeLoader (Node.js)](https://github.com/indxSearch/IndxNodeLoader) — Node.js equivalent
- [indx-intrface (React)](https://github.com/indxSearch/indx-intrface) — React search UI components
