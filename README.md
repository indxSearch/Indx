# Indx

A self-hosted search service built on [Indx Search](https://indx.co). Blazor Server UI, HTTP API with JWT authentication, user management, and everything needed to run a multi-user search service on your own infrastructure.

## What's Included

- **Dashboard**: teams, datasets, field configuration with weight sliders, search preview,
  status, boost rules, synonyms, options; plus an admin panel
- **Teams**: datasets belong to teams; members join with per-team roles
- **HTTP API**: JWT authentication and API key management; errors are RFC 9457 problem
  documents with a machine-readable `code`
- **Dynamic data**: insert, update, delete, by key or by filter, with the index kept in sync
- **Zero-downtime rebuilds**: replace a dataset, or change its field configuration, on a
  shadow engine while the old one keeps serving
- **Keep-alive & hibernation**: pinned, timed or client-managed memory per dataset
- **MCP server**: connect AI agents (Claude, etc.) directly to your search data, at `/mcp`
- **Accounts**: registration, login and account management; local passwords, with optional
  Microsoft and Google sign-in
- **Relevance**: server-side boost rules with schedules, facets, coverage, vector and hybrid search
- **Synonyms**: per-dataset lists (experimental); query expansion at search time
- **Notifications**: in the app and by email
- **SQLite storage**: no external database required, and migrations run on startup
- **Swagger UI**: the whole API, browsable at `/swagger`

## Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Run Locally

```bash
git clone https://github.com/indxSearch/Indx
cd Indx
dotnet run
```

Open `https://localhost:5001`. The first visit walks you through a short setup: create the admin account, name your team, and pick instance settings. Done in under a minute.

Works immediately with no configuration:
- Local username/password accounts
- SQLite databases auto-created in `./IndxData/`
- Emails logged to console (no SMTP required)

## First-Run Setup

The setup wizard runs automatically on first visit. Afterwards, **Admin → Settings** configures:

- **Registration mode**: Open, domain-restricted, or closed
- **Email provider**: Switch from console logging to Azure Communication Services
- **OAuth**: Enable Microsoft and/or Google sign-in

Most settings can be changed through the UI without restarting the app.

## Teams

Everything is organized around teams:

- Every user gets a **personal team** on signup, and your datasets live there by default.
- Create more teams to share datasets with colleagues. Members are invited with a
  per-team role: **Admin** (manage members, delete datasets), **Editor** (load,
  index, configure), or **Viewer** (search and read).
- Datasets can be **renamed**, or **transferred** to another team you administer. Deleting a
  team deletes its datasets with it (the dashboard asks you to type the team's name).
- The HTTP API is team-scoped: every dataset route is
  `/api/teams/{team}/datasets/{dataset}/…`.

## Loading Data

Data goes in as **JSON**, from the web UI or over the API.

**From the UI:** open your dataset, upload a JSON file, mark which fields are searchable
(and filterable / facetable / sortable), then **Load & Index**. The search preview tab lets
you try queries immediately.

**The format:** an array of JSON documents. Nested objects become dotted field names
(`brand.displayName`), arrays are supported, and fields are auto-detected with types on
upload. **Export files that wrap the documents in a root object**, such as
`{ "count": …, "products": [ … ] }` as many systems export, are handled automatically:
Indx finds the document array inside the envelope on its own, so upload the file as-is.

**Over the API:** the same steps as endpoints, so analyze, field configuration, load, index.
See `/swagger` for the full surface.

**Changing data afterwards:** `POST`/`PUT`/`PATCH`/`DELETE …/documents` and
`…/documents/{key}` insert, update and delete documents one at a time or in batches, and
`documents/delete-by-filter` / `documents/update-by-filter` act on everything a filter matches.
The index follows immediately; no re-index. Batches are all-or-nothing. Fields that were not
present at load time are stored but not indexed. Use **Replace** to adopt them.

**Replace** (`POST …/replace`, or the *Options* tab) swaps the whole dataset for a new JSON
file with no downtime: the new documents load and index on a shadow engine while the old one
keeps answering, then the two are swapped. Changing the **field configuration** of a loaded
dataset rebuilds the same way. While a build runs, `GET status` reports
`shadowBuildInProgress` and a second build is refused with `409 shadowBusy`.

## Synonyms (experimental)

Each dataset can carry a synonym list that widens searches: when a query matches an entry,
the entry's terms are appended to the query text before scoring. Changes apply on the next
search, and nothing is re-indexed.

Two kinds of entries:

- **Two-way**: all terms are equivalent; matching any of them pulls in the whole group.
  For inflected forms and spelling variants (*geriatri / geriatrisk / geriatriske*).
- **One-way**: only the **From** term expands, into its synonyms. For acronyms: *hms*
  should bring in *helse, miljø og sikkerhet*, but a search for *helse* must not become
  a search for *hms*. Multi-word terms are matched as whole phrases.

**From the UI:** the **Synonyms** tab on a dataset (editor role). Create and edit entries
in a dialog, or import/export the whole list as JSON.

**Over the API:** `GET`/`PUT api/teams/<team>/datasets/<dataset>/synonyms`. `GET` returns
the list (or `null`), `PUT` replaces it (`null` removes it; editor role required).

**Why experimental:** expansion widens recall but grows the query text, which dilutes
Coverage scores proportionally. Measure the net effect on your data before shipping a
large list to production. Behavior may still change.

## MCP: connect AI agents

The server exposes a [Model Context Protocol](https://modelcontextprotocol.io) endpoint at `/mcp` (Streamable HTTP). Point an MCP-capable client at it with a bearer token, whether Claude Code, Claude Desktop, or any other, and the agent gets read-only tools over your datasets, for both querying and setting one up:

- **Querying**: `list_datasets`, `describe_dataset`, `search`, `get_document`, `get_synonyms`
- **Setting up**: `get_status` (state, document count, scoring mode, errors, and what to do next),
  `get_field_configuration` (every field including the ones switched off, with weights, BM25
  parameters and a sample of the real content), and `diagnose_search` (why a search returned
  nothing: not indexed, no searchable field, query refused, matching too strict, or a genuine
  no-match). It reports strict and pattern-matching hit counts separately, because the MCP
  search tool disables pattern matches while an HTTP client gets them by default, so "finds
  nothing" means different things for an agent and for a search box

Paired with the [Indx agent skill](https://github.com/indxSearch/skill-indx-search), an agent can read the documentation and inspect your live instance at the same time, which is most of what setting up a dataset takes.

- **Same keys and permissions** as the rest of the API. A Search only key lets the agent list,
  search and fetch documents; `describe_dataset` and `get_synonyms` need Read only
- **Read-only by design**: agents can search, not mutate
- **Switched off instance-wide** by an admin under Instance Settings, with no restart

## API Access

1. Log in and open **API keys** in the account menu. A key is limited to one team, optionally to
   some of its datasets, and to an access level:

   | Level | Can do | Use it for |
   |---|---|---|
   | **Search only** | Search (text, vector, hybrid), fetch result documents, build filters, read field lists and status | Websites and apps, safe in a browser |
   | **Read only** | Every read, including export, field configuration and synonyms | Exports, reporting, AI agents, kept on a server |
   | **Full access** | Everything your team role allows, including loading and deleting data | Your own servers and pipelines |

   A key never exceeds your role in the team, and cannot be changed after it is created. Outside its
   team or datasets it gets `404`; above its level, `403 insufficientKeyScope`.

   **Anything that runs in a browser, [`@indxsearch/intrface`](https://github.com/indxSearch/indx-react)
   included, ships its key to every visitor. Use a Search only key limited to the datasets that page searches.**
   Keys created before access levels existed show as **Unscoped** and reach every team you belong
   to: replace them and revoke the old ones.
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

### Rate limiting

The anonymous auth endpoints, meaning `POST /api/login` and the dashboard's login, register,
forgot-password, reset and resend-confirmation forms, are limited per client IP: 10 attempts
per 60 seconds by default, together. Over the limit answers `429` with an RFC 9457 problem
(`code: rateLimited`) and a `Retry-After` header. Configure under `RateLimits:Auth`
(`Enabled`, `PermitLimit`, `WindowSeconds`).

Authenticated `/api` and `/mcp` traffic can be limited **per API key** with a token bucket,
`RateLimits:Api` (`Enabled`, `RequestsPerSecond`, `Burst`). It is **off by default**, since on your
own hardware the ceiling is the hardware, and on for managed instances. Each key has its own
bucket, so one runaway integration does not throttle a user's other keys. Over the limit is
the same `429 rateLimited` with `Retry-After`.

`/api` and `/mcp` requests with **no bearer token that validates**, whether none at all, forged or
expired, are limited per client IP too, `RateLimits:Anon` (`Enabled`, `PermitLimit`,
`WindowSeconds`), 30 per 60 seconds by default, **on**. They all end in `401`, so a working
client makes them only while a token is being refreshed, whereas a scanner makes nothing else.
Sending a junk `Authorization` header is not a way out of this window. CORS preflight (`OPTIONS`)
is excluded.

All three limits answer the same `429 rateLimited` problem, so a client needs one handler: wait
`retryAfterSeconds` and retry. Rejections are logged (`IndxServer.RateLimit`) and counted
(`indx.ratelimit.rejections`, tagged by limit).

There is no instance-wide cap on how much work may run at once. Concurrency is bounded where
it is actually known: a dataset that is mid-rebuild answers `409` (`ShadowBusyException`), and
the engine holds its own slot pool per dataset.

Behind a reverse proxy or App Service, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so the
server sees the client's IP rather than the proxy's; otherwise every caller shares one window.

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

The download endpoint is **hardcoded** to the Indx portal (`https://license.indx.co/api/license/current`), because only Indx issues licenses, so it is not configurable. Auto-fetch is **token-gated**: with a token set, a **fresh license is fetched on every startup** (the portal rolls Pro/Free expiry forward) and written to `Indx:LicenseFile` (default `./IndxData/indx.license`). **Without a token, auto-fetch is a no-op**: the server never reaches out and relies on a manually placed file or free-tier mode. Fetch failures are logged but never fatal, and the server keeps any existing file, or falls back to free-tier mode. Set the token via environment variable or Key Vault, not in `appsettings.json`.

> **Legacy:** the old `Indx__LicenseDownloadUrl` setting is no longer read. The URL is hardcoded and fetching is driven solely by `Indx__LicenseToken`. Remove it from any existing configuration.

## Deployment

### Azure App Service

1. Create an App Service with **.NET 10** Linux runtime
2. Deploy via zip deploy, GitHub Actions, or Visual Studio publish
3. Set `ASPNETCORE_ENVIRONMENT = Production` in Application Settings
4. **Turn on Web sockets** (Configuration → General settings, or
   `az webapp config set --web-sockets-enabled true`). The dashboard is Blazor Server and
   talks over SignalR; without WebSockets it falls back to long polling, which is slow and
   shows up as a `GET /_blazor` taking 90 s in every IIS log line.

The app creates its SQLite databases in `./IndxData/` on first run. This directory persists across redeployments.

For email and OAuth, set the relevant Application Settings listed in the Configuration section above. The app restarts automatically when settings change in Azure.

**Redirect URIs**, if using OAuth, add these to your app registrations:
```
https://your-app.azurewebsites.net/signin-microsoft
https://your-app.azurewebsites.net/signin-google
```

## Database

Two SQLite files in `./IndxData/`:

- `identity.db`: user accounts, roles, and authentication (ASP.NET Core Identity)
- `indx.db`: application config, API keys, notifications, **and each dataset's persisted document store** (the JSON records, field configuration, and keep-alive policy)

Migrations run automatically on startup. No manual migration steps needed.

> The **inverted search index is held in memory** and rebuilt on load; `indx.db` persists the documents and configuration, so a dataset can be unloaded to free memory and reloaded, re-indexed from the store, without re-uploading data.

## Dataset lifecycle: keep-alive & hibernation

Because the documents live in `indx.db` while only the index is in memory, datasets can be loaded and unloaded on demand to manage RAM. Each dataset has a **keep-alive policy** (`KeepAliveTimeHrs`), set per dataset on the **Datasets** page (a dataset's *Options* tab) or, across all teams, on **Admin → Datasets**:

| Policy | Behaviour |
|--------|-----------|
| **Pinned** (default) | Loaded at startup, never evicted. |
| **Timed** (N hours) | Loaded at startup; evicted from memory after N hours idle, then **reloaded automatically on the next request**. |
| **Off** (client-managed) | Not loaded at startup and never auto-loaded, so you load and wake it explicitly. Never auto-evicted. |

A background sweeper frees idle *Timed* datasets; the next access transparently reloads and re-indexes from `indx.db`. **Pinned** and **Off** datasets are never auto-evicted.

**Manual hibernation.** From a dataset's *Options* tab you can **Hibernate now** an *Off* dataset to free its memory immediately (the data stays on disk). A hibernated dataset shows a **💤 Hibernated** state with a **Wake up** action that reloads it.

> This is a *deep* hibernate: the in-memory engine is disposed and rebuilt from `indx.db` on wake, which is the right model when storage is the source of truth. It is distinct from the core library's lighter `Hibernate`/`WakeUp` (which keeps documents resident in RAM and only drops the index).

Over the HTTP API, the per-dataset `Hibernate`, `WakeUp`, and `LoadFromDatabase` operations control loading. See the [API reference](https://v5.docs.indx.co).

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

- [`@indxsearch/intrface`](https://www.npmjs.com/package/@indxsearch/intrface): React search UI components for IndxServer (with [`@indxsearch/systm`](https://www.npmjs.com/package/@indxsearch/systm) and [`@indxsearch/pixl`](https://www.npmjs.com/package/@indxsearch/pixl))
- [`@indxsearch/indx-types`](https://www.npmjs.com/package/@indxsearch/indx-types): TypeScript types for every request, response and error code
- [Indx Search skill](https://skills.sh/indxsearch/skill-indx-search/indx-search): teaches AI coding agents this API, including a v1 → v2 migration guide
- [`IndxSearchLib`](https://www.nuget.org/packages/IndxSearchLib): the embedded C# search engine this server is built on
- [Documentation](https://v5.docs.indx.co): guides, how-tos, and the full API reference
- [Changelog](CHANGELOG.md) and [release notes](docs/release-notes/)
