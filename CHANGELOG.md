# Changelog

All notable changes to the Indx server. The top section is the release notes for the next
GitHub release: copy it as-is.

Versions: the server has its own line (2.x). It bundles the Indx core library (`IndxSearchLib`,
5.x) and serves the HTTP API contract `v2.0-beta`, which is the version in every URL. A server
patch can pick up a newer core without changing the HTTP contract.

## [2.0.0-beta] - Unreleased

Runs on IndxSearchLib 5.0.2. First public release of the v2 server; v1 users should read
the [v1 → v2 migration guide](https://github.com/indxSearch/skill-indx-search/blob/main/references/migration-v4-to-v5.md)
in the AI skill.

### Changed

- **Field roles are written by one route, and it no longer waits for a rebuild.** The per-role
  setters are removed: `PUT fields/searchable`, `fields/filterable`, `fields/facetable`,
  `fields/sortable`, `fields/word-indexing` and `fields/embeddable` answer `405`. They set one
  flag on the live engine with no rebuild, so a role that needs one (searchable, or a field's
  first role) had no effect on search until the next index build. The lists stay readable with
  `GET`, and `GET fields/embeddable` is new. Send roles to `PUT fields/configuration`, one object
  per field with only the properties to change.
  `PUT fields/configuration` checks the whole request first (a refused one is a `400` and
  changes nothing), then answers `204` when the change is applied at once, or `202` with the
  status when it has started a rebuild of a dataset that is Ready. It used to hold the request
  for the whole rebuild, minutes on a large dataset. Poll `GET status` until
  `shadowBuildInProgress` is false, then read `shadowBuildError`.
- **`POST index` answers when the build is started**, as its `202` always said. It waited for
  the build to finish. After a first build poll `GET status` until Ready; on a Ready dataset it
  is a rebuild on a shadow engine, reported like a field configuration change.
- **`GET status` says how the last rebuild ended**: `shadowBuildFinishedUtc`, and
  `shadowBuildError` with the reason when it failed (the dataset then serves as it did). Also
  in MCP `get_status`.
- **A change while the dataset is loading, indexing or rebuilding is a `409`**, for
  `PUT fields/configuration` as it already was for documents. Applied in the middle of a build
  it was half in the build and half out of it.
- **A vector field has no other role, in the console too.** The field table disables the boxes
  the engine would refuse and says why: no other role on an embeddable field, and embeddable
  only on an array of numbers.
- **A dataset that sleeps keeps its vector graphs in a file**, and waking it reads them in
  place of building them, which is the slow part of loading a dataset with vectors (41 to 25
  seconds on 43 000 vectors of 1 536 numbers). One file per dataset in `embeddings` beside the
  search database, about 3 KB a vector of that size. A cache only: a file that is missing or
  out of date costs the time to build.
- **A load that fails is not indexed, and a refused field configuration is shown.** The server
  went on to index after a load from the store that had failed or timed out, which left the
  dataset Ready on the documents read so far. It now indexes only after a load that succeeded.
  In the console, a field configuration the engine refuses at Load is shown as the reason
  instead of being lost.
- **MCP is read-only.** `set_field_configuration` is gone from the MCP surface; configuration is
  changed in the web console or over the HTTP API, where a person sees the change. It was the only
  tool that wrote, and it forced a **Full** key on anyone who wanted it - a key that, over HTTP,
  can also delete the dataset and replace its data. No MCP connection now needs a key that could
  do either.
- **An MCP session is shaped by its key.** `tools/list` offers only what the key can call, and the
  instructions sent at connection describe that set. Previously every client was told to call
  `describe_dataset` before searching and was offered every tool, while a Search key could do
  neither. Calling a tool that was not offered still gives the refusal naming the level needed,
  rather than "unknown tool".
- **One log.** `IndxServer.log` is now a provider of the server's normal logging, beside the
  console, the monitor and Application Insights. Before, the dataset registry and the search
  engines wrote only to that file, and everything else only to the others, so Application
  Insights never saw a failed load, index or search. The file is capped: it rolls over at 10 MB
  and keeps 9 archives. It takes Indx categories from Information and the rest from Warning, set
  under `Logging:NLog:LogLevel` in `appsettings.json`, and `Indx:LogFile` moves it (a container
  volume, say). A failure is logged with its whole stack, inner exceptions included, everywhere
  it used to be only a message or text glued into one: failed loads, replaces, wakes and
  searches, and dataset creation in the console.

### Added

- **Statistics for one search surface.** `overview`, `timeseries`, `queries`,
  `queries/documents`, `documents` and `filters` take `?source=` and count only the searches sent
  with it; a click or conversion counts for the surface of the search it names by `queryId`.
  `GET statistics/sources` lists the surfaces with their searches. The `source` parameter was
  accepted before and ignored, so every surface read as the dataset's totals. The console's
  Statistics tab gets a surface selector.
- **Monitor.** A live view of the instance: dataset states, document counts, idle-eviction
  countdowns, process and native memory, filter-cache routing, and an event stream carrying state
  changes, field configuration changes and anything the server logs.
  - **In the console**, under **Admin → Monitor**. Admin only, since an event names the team whose
    dataset changed. Events are held in memory and start again on restart. This is the one to use
    on a hosted deployment, where there is no terminal.
  - **In the terminal**, drawn automatically when the server is started from one. `Ctrl+Q`
    detaches it and leaves the server running; `F10` shuts the server down after a confirmation;
    `--no-monitor` turns it off.
  - **Piped**, where stdout is redirected (Azure log stream, `docker logs`, systemd): a periodic
    status block, opt-in via `Indx:Monitor:Enabled`.

### Breaking

- **Team-scoped routes.** Every dataset operation lives under
  `/api/teams/{team}/datasets/{dataset}/…`. The 47 v1 route aliases are gone.
- **Errors are RFC 9457 problem documents** (`application/problem+json`) with a machine-readable
  `code`: `invalidArgument`, `unknownFilter`, `datasetNotFound`, `insufficientRole`, `insufficientKeyScope`,
  `invalidState` (with `currentState`, `allowedStates`, `retryable` and a `Retry-After` header
  while loading or indexing), `shadowBusy`. Switch on `code`, not the status.
- **A filter token the server cannot honour is a 400 `unknownFilter`**, never a silently
  unfiltered search. Filter tokens are opaque by contract; do not parse or construct them.
- **A busy dataset answers 409, not 400**, from every state, not only Ready. And `POST
  …/documents` on a dataset that is loaded but not indexed is a 409 `invalidState` saying to
  index first; it used to hand back the engine's refusal as a 400 `invalidArgument`.
- **The `?configuration=` query parameter on create is ignored.** There is one configuration.
- **Renamed C# types** for callers using the NuGet's request shapes: `CloudQuery` → `QueryProxy`,
  namespace `Indx.CloudApi` → `Indx.Http`, `ICloudSearchEngine` → `IServerSearchEngine`. The old
  names were `[Obsolete]` aliases in the 5.0 betas and are removed in the release candidate; a
  build against them fails with the rename in the message. JSON on the wire is unchanged.
- **The shipped binary is `IndxServer.dll`** (was `IndxCloudApi.dll`); update run scripts.

### Added

- **Dashboard.** Team page with the datasets as cards → dataset page with Status, Field
  configuration, Search preview, Boost rules, Synonyms and Options tabs. Breadcrumb with team
  and dataset switchers, "New team…" / "New dataset…" from the switcher, remembered last team.
  Manage team (members, rename, delete with typed confirmation). Rename a dataset. Field
  config with weight sliders and type icons. Reconnect overlay. Full tab set visible from the
  first upload, with not-yet tabs disabled.
- **`GET …/fields/configuration` at Search level** (was Read). A filter panel needs each field's
  type to send a selected value as a value filter or as a range with equal limits, and the name
  lists it reads carry no type. Nothing new is revealed: a Search key can already search and read
  documents. `PUT` stays Full.
- **Waking a small dataset no longer takes as long as the team's largest.** The database had no
  index a dataset lookup could use, so every wake, auto-reload and count walked the team's whole
  table; where that table is large, loading a dataset of a hundred documents took on the order of a
  second instead of being immediate. The server adds the index to an existing database at startup,
  in a fraction of a second.
- **`filters/value` on a numeric field, and `filters/range` on a text field, are `400`.** The
  field's type decides the filter kind: a value filter compares text and is wrong on a number
  (`129` is not `129.0`), a range filter needs numbers, and a number stored as a JSON string is
  text. The message names the alternative - a range with equal limits for equality on a number,
  storing the values as numbers for the other. A token of the refused kind answers
  `400 unknownFilter`; re-create the filter. Boost rules and MCP conditions with a value on a
  numeric field keep working: the server builds the range for them.
- **`POST …/filters/not`**: the NOT of a filter, as a token like any other. Combines further,
  survives eviction, and answers `400 unknownFilter` for a token it cannot honour, like its
  siblings. Search key level.
- **`isCaseSensitive` on `filters/value`**, optional and off by default. Set it when the value
  comes from a facet: facets count distinct stored values with their casing, so only a
  case-sensitive filter agrees with a facet count. `value` takes a string, number or boolean.
- **Synonyms** per dataset: `GET`/`PUT …/synonyms`, a Synonyms tab (experimental), copy a
  list onto another dataset. Expansion lowers coverage scores by design.
- **Boost rules** per dataset with schedules; import/export as JSON; expired, scheduled and
  disabled rules are visible; editors and admins are notified when a rule expires.
- **Replace** a dataset's documents atomically with live step-by-step progress, cancellable
  during transfer; reports lost field roles and a missing declared key field.
- **API key access levels.** A new key is limited to one team, optionally to some of its datasets,
  and to **Search only** (what a search front-end needs — safe in a browser), **Read only** or
  **Full access**. A key never exceeds its owner's team role. Outside its team or datasets a key
  gets the same `404` as a missing one; above its level, `403 insufficientKeyScope`. Keys created
  earlier keep working unscoped and are labelled **Unscoped** — replace them, and use a Search only
  key for anything that ships to a browser (including `@indxsearch/intrface`).
- **Download a dataset:** `GET …/datasets/{dataset}/export` streams every document as one JSON
  array ordered by key, and **Options → Download data** in the dashboard. Works on hibernated
  datasets; needs Read access.
- **MCP server** at `/mcp` for AI agents: search, browse, describe dataset, get synonyms. Tools
  honour the API key's access level, and tool errors now tell the agent why.
- **Export-envelope detection**: an upload that is one document wrapping the real array gets a
  warning instead of a one-document dataset.
- **Error-state datasets** get a recovery panel.
- **Notifications** in-app and by email, with per-type preferences.

### Changed

- Product name is **Indx**; the dashboard is the Indx Dashboard. GitHub repo renamed to
  `indxSearch/Indx`.
- Dataset lists render immediately and fill in state per dataset off the request thread; the
  admin datasets page no longer wakes hibernated datasets to list them.
- Datasets warm in the background at startup (fixes the Azure boot loop).
- Deleting a team deletes its datasets; deleting a dataset can no longer leave a stale engine
  behind for a dataset re-created under the same name.
- Lower-case kebab-case UI routes (`/account/api-key`, `/admin/settings`, `/teams/{team}`).
- **Opening an existing dataset** (`PUT …/datasets/{dataset}`) needs only read access; creating one
  still needs Editor or Admin. Search front-ends run on a Search only key and a Viewer's key.
- A dataset's last refused operation shows under **Status → Last error**, with Clear, instead of a
  banner that could not be dismissed.

### Security

- Seeded admin gets a generated initial password and must change it on first login.
- Email confirmation enforced on `/api/login`; revoked API keys are evicted from the cache;
  login tokens are retired on password change.
- Cross-origin requests are denied in Production until CORS is configured.
- `IndxData/settings.json` is runtime state and is no longer shipped.
- **Rate limiting**, both limits answering `429` with `code: rateLimited`, `retryAfterSeconds` and
  a `Retry-After` header: the anonymous auth endpoints per client IP (`RateLimits:Auth`, on by
  default) and authenticated API traffic per API key (`RateLimits:Api`, off by default, on for
  managed instances). A request with an invalid bearer token counts against the anonymous
  window; a made-up `Authorization` header no longer escapes it.

### Fixed

- **`appsettings.Production.json` is read on Linux.** It was named `appsettings.production.json`;
  ASP.NET looks for the environment name as spelled, and Linux file names are case-sensitive, so
  the production overrides (closed registration, among others) were silently skipped there.
- **On a Linux App Service the databases go under `/home`.** Without a configured connection
  string the server used `d:/home/IndxData`, the Windows path, on every App Service plan; on
  Linux that is a relative directory the container loses on restart. It now follows `HOME`, as
  the log file does. Windows plans are unchanged.
- **An upload cut off in the middle fails** (IndxSearchLib, next version) rather than loading the
  records that arrived before the break and reporting success.
- **Coverage ranking** (IndxSearchLib 5.0.0-RC240926): a search containing æ, ø, å or another
  folded character fully matches documents with the same word; a query being typed ranks by its
  whole text, so `knut h` puts Knut Hamsun above Knut Faldbakken; words in order anywhere in a
  document count as in order; and a document's score no longer depends on which others the search
  met first.
- **Filters** (IndxSearchLib 5.0.0-RC250926): a combined filter used right after a reload or a first
  field role no longer answers with the result from before it; a filtered search that overlapped a
  filter load can no longer run unfiltered; a filter loaded while documents are inserted or updated
  no longer misses documents or fails.
- **Facet panel and Search preview** (IndxSearchLib 5.0.0-RC250926): counts follow inserts and
  deletes at once, and deleted documents are no longer counted or listed.
- **Search beside writes** (IndxSearchLib 5.0.0-RC250926): searches, facets and vector searches that
  run while documents are inserted, updated or deleted no longer lose documents or hits.
- **Filters after a reload** (IndxSearchLib 5.0.0-RC250926): after a dataset is loaded again in place,
  case-sensitive and array filters see the new documents, and a field update reaches the filters.
- **Search ranking** (IndxSearchLib 5.0.0-RC150926): common text fragments no longer push unrelated
  documents to the top score — the cause of hundreds of ties and the real match buried, most
  visible with long searchable text and natural-language queries; term frequency, field length
  and `bM25b` now affect scores.
- **Vector and hybrid search:** building an embedding index no longer runs out of memory (it grew
  with the fourth power of the document count; now a fixed 12 MB cache plus the vectors).
- A 500's underlying exception is logged.
- The search preview finds nested fields by name.
- In-flight searches are drained before hibernate or dispose.

## [1.0.2]

Last release of the v1 server. See the GitHub release notes.
