# Changelog

All notable changes to the Indx server. The top section is the release notes for the next
GitHub release: copy it as-is.

Versions: the server has its own line (2.x). It bundles the Indx core library (`IndxSearchLib`,
5.x) and serves the HTTP API contract `v2.0-beta`, which is the version in every URL. A server
patch can pick up a newer core without changing the HTTP contract.

## [2.0.0-beta] - Unreleased

Runs on IndxSearchLib 5.0.0-beta5. First public release of the v2 server; v1 users should read
the [v1 → v2 migration guide](https://github.com/indxSearch/skill-indx-search/blob/main/references/migration-v4-to-v5.md)
in the AI skill.

### Breaking

- **Team-scoped routes.** Every dataset operation lives under
  `/api/teams/{team}/datasets/{dataset}/…`. The 47 v1 route aliases are gone.
- **Errors are RFC 9457 problem documents** (`application/problem+json`) with a machine-readable
  `code`: `invalidArgument`, `unknownFilter`, `datasetNotFound`, `insufficientRole`,
  `invalidState` (with `currentState`, `allowedStates`, `retryable` and a `Retry-After` header
  while loading or indexing), `shadowBusy`. Switch on `code`, not the status.
- **A filter token the server cannot honour is a 400 `unknownFilter`**, never a silently
  unfiltered search. Filter tokens are opaque by contract; do not parse or construct them.
- **A busy dataset answers 409, not 400**, from every state, not only Ready.
- **The `?configuration=` query parameter on create is ignored.** There is one configuration.
- **Renamed C# types** for callers using the NuGet's request shapes: `CloudQuery` → `QueryProxy`,
  namespace `Indx.CloudApi` → `Indx.Http`, `ICloudSearchEngine` → `IServerSearchEngine`. The old
  names remain as `[Obsolete]` aliases for the 5.0 betas. JSON on the wire is unchanged.
- **The shipped binary is `IndxServer.dll`** (was `IndxCloudApi.dll`); update run scripts.

### Added

- **Dashboard.** Team page with the datasets as cards → dataset page with Status, Field
  configuration, Search preview, Boost rules, Synonyms and Options tabs. Breadcrumb with team
  and dataset switchers, "New team…" / "New dataset…" from the switcher, remembered last team.
  Manage team (members, rename, delete with typed confirmation). Rename a dataset. Field
  config with weight sliders and type icons. Reconnect overlay. Full tab set visible from the
  first upload, with not-yet tabs disabled.
- **Synonyms** per dataset: `GET`/`PUT …/synonyms`, a Synonyms tab (experimental), copy a
  list onto another dataset. Expansion lowers coverage scores by design.
- **Boost rules** per dataset with schedules; import/export as JSON; expired, scheduled and
  disabled rules are visible; editors and admins are notified when a rule expires.
- **Replace** a dataset's documents atomically with live step-by-step progress, cancellable
  during transfer; reports lost field roles and a missing declared key field.
- **MCP server** at `/mcp` for AI agents: search, browse, describe dataset, get synonyms.
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

### Security

- Seeded admin gets a generated initial password and must change it on first login.
- Email confirmation enforced on `/api/login`; revoked API keys are evicted from the cache;
  login tokens are retired on password change.
- Cross-origin requests are denied in Production until CORS is configured.
- `IndxData/settings.json` is runtime state and is no longer shipped.

### Fixed

- A 500's underlying exception is logged.
- The search preview finds nested fields by name.
- In-flight searches are drained before hibernate or dispose.

## [1.0.2]

Last release of the v1 server. See the GitHub release notes.
