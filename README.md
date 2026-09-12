# IndxServer

A self-hosted search service built on [Indx Search](https://indx.co). Blazor Server UI, HTTP API with JWT authentication, user management, and everything needed to run a multi-user search service on your own infrastructure.

## What's Included

- Blazor Server web interface with admin panel
- **Teams** — datasets belong to teams; members join with per-team roles
- HTTP API with JWT authentication and API key management
- **MCP server** at `/mcp` — connect AI agents (Claude, etc.) directly to your search data
- User registration, login, and account management
- Local accounts with optional Microsoft and Google OAuth
- Server-side boost rules, facets, vector & hybrid search
- Per-dataset synonym lists (experimental) — query expansion at search time
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
cd IndxServer
dotnet run
```

Open `https://localhost:5001` — the first visit walks you through a short setup: create the admin account, name your team, and pick instance settings. Done in under a minute.

Works immediately with no configuration:
- Local username/password accounts
- SQLite databases auto-created in `./IndxData/`
- Emails logged to console (no SMTP required)

## First-Run Setup

The setup wizard runs automatically on first visit. Afterwards, **Admin → Settings** configures:

- **Registration mode** — Open, domain-restricted, or closed
- **Email provider** — Switch from console logging to Azure Communication Services
- **OAuth** — Enable Microsoft and/or Google sign-in

Most settings can be changed through the UI without restarting the app.

## Teams

Everything is organized around teams:

- Every user gets a **personal team** on signup — your datasets live there by default.
- Create more teams to share datasets with colleagues. Members are invited with a
  per-team role: **Admin** (manage members, delete datasets), **Editor** (load,
  index, configure), or **Viewer** (search and read).
- Datasets can be **transferred** between teams you belong to.
- The HTTP API is team-scoped: every dataset route is
  `/api/teams/{team}/datasets/{dataset}/…`.

## Loading Data

Data goes in as **JSON** — from the web UI or over the API.

**From the UI:** open your dataset, upload a JSON file, mark which fields are searchable
(and filterable / facetable / sortable), then **Load & Index**. The search preview tab lets
you try queries immediately.

**The format:** an array of JSON documents. Nested objects become dotted field names
(`brand.displayName`), arrays are supported, and fields are auto-detected with types on
upload. **Export files that wrap the documents in a root object** — e.g.
`{ "count": …, "products": [ … ] }`, as many systems export — are handled automatically:
Indx finds the document array inside the envelope on its own, so upload the file as-is.

**Over the API:** the same steps as endpoints — analyze, field configuration, load, index —
plus **single-document operations** (`POST`/`PUT`/`PATCH`/`DELETE …/documents/{key}`) that
keep the search index in sync incrementally, so pushing individual record changes from a
source system needs no re-index. See `/swagger` for the full surface.

## Synonyms (experimental)

Each dataset can carry a synonym list that widens searches: when a query matches an entry,
the entry's terms are appended to the query text before scoring. Changes apply on the next
search — nothing is re-indexed.

Two kinds of entries:

- **Two-way** — all terms are equivalent; matching any of them pulls in the whole group.
  For inflected forms and spelling variants (*geriatri / geriatrisk / geriatriske*).
- **One-way** — only the **From** term expands, into its synonyms. For acronyms: *hms*
  should bring in *helse, miljø og sikkerhet*, but a search for *helse* must not become
  a search for *hms*. Multi-word terms are matched as whole phrases.

**From the UI:** the **Synonyms** tab on a dataset (editor role) — create and edit entries
in a dialog, or import/export the whole list as JSON.

**Over the API:** `GET`/`PUT api/teams/<team>/datasets/<dataset>/synonyms` — `GET` returns
the list (or `null`), `PUT` replaces it (`null` removes it; editor role required).

**Why experimental:** expansion widens recall but grows the query text, which dilutes
Coverage scores proportionally — measure the net effect on your data before shipping a
large list to production. Behavior may still change.

## MCP — connect AI agents

The server exposes a [Model Context Protocol](https://modelcontextprotocol.io) endpoint at `/mcp` (Streamable HTTP). Point an MCP-capable client — Claude Code, Claude Desktop, or any other — at it with a bearer token, and the agent gets read-only retrieval tools over your datasets: search, field info, status, synonym lists.

- Same JWT tokens and team permissions as the rest of the API
- Read-only by design — agents can search, not mutate
- Admins can disable it instance-wide under **Instance Settings** (no restart needed)

## API Access

1. Log in and open **API Key** in the menu to generate a bearer token
2. Every dataset operation is scoped to a team and dataset:

```bash
curl -X POST "https://localhost:5001/api/teams/<team>/datasets/<dataset>/search" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/json" \
  -d '{ "text": "your query", "maxNumberOfRecordsToReturn": 10 }'
```

Full API reference at `/swagger`.

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

Instead of placing the file manually, IndxServer can pull its license straight from the Indx License Portal ([license.indx.co](https://license.indx.co)) on startup. On your portal license page, create a license token, then configure:

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

## Database

Two SQLite files in `./IndxData/`:

- `identity.db` — user accounts, roles, and authentication (ASP.NET Core Identity)
- `indx.db` — application config, API keys, notifications, **and each dataset's persisted document store** (the JSON records, field configuration, and keep-alive policy)

Migrations run automatically on startup. No manual migration steps needed.

> The **inverted search index is held in memory** and rebuilt on load; `indx.db` persists the documents and configuration, so a dataset can be unloaded to free memory and reloaded — re-indexed from the store — without re-uploading data.

## Dataset lifecycle: keep-alive & hibernation

Because the documents live in `indx.db` while only the index is in memory, datasets can be loaded and unloaded on demand to manage RAM. Each dataset has a **keep-alive policy** (`KeepAliveTimeHrs`), set per dataset on the **Datasets** page (a dataset's *Options* tab) or, across all teams, on **Admin → Datasets**:

| Policy | Behaviour |
|--------|-----------|
| **Pinned** (default) | Loaded at startup, never evicted. |
| **Timed** (N hours) | Loaded at startup; evicted from memory after N hours idle, then **reloaded automatically on the next request**. |
| **Off** (client-managed) | Not loaded at startup and never auto-loaded — you load/wake it explicitly. Never auto-evicted. |

A background sweeper frees idle *Timed* datasets; the next access transparently reloads and re-indexes from `indx.db`. **Pinned** and **Off** datasets are never auto-evicted.

**Manual hibernation.** From a dataset's *Options* tab you can **Hibernate now** an *Off* dataset to free its memory immediately (the data stays on disk). A hibernated dataset shows a **💤 Hibernated** state with a **Wake up** action that reloads it.

> This is a *deep* hibernate — the in-memory engine is disposed and rebuilt from `indx.db` on wake — which is the right model when storage is the source of truth. It is distinct from the core library's lighter `Hibernate`/`WakeUp` (which keeps documents resident in RAM and only drops the index).

Over the HTTP API, the per-dataset `Hibernate`, `WakeUp`, and `LoadFromDatabase` operations control loading — see the [API reference](https://v5.docs.indx.co).

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

- [`@indxsearch/intrface`](https://www.npmjs.com/package/@indxsearch/intrface) — React search UI components for IndxServer (with [`@indxsearch/systm`](https://www.npmjs.com/package/@indxsearch/systm) and [`@indxsearch/pixl`](https://www.npmjs.com/package/@indxsearch/pixl))
- [`IndxSearchLib`](https://www.nuget.org/packages/IndxSearchLib) — the embedded C# search engine this server is built on
- [Documentation](https://v5.docs.indx.co) — guides, how-tos, and the full API reference
