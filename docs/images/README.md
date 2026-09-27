# Screenshots

Images for `README.md` and `docs/release-notes/`. Reference them as `docs/images/<name>.jpg` from
the repository root, or `../images/<name>.jpg` from inside `docs/release-notes/`.

| File | Shows |
|---|---|
| `console-datasets.jpg` | A team's dataset list: five datasets, document counts, Ready chips |
| `console-datasets-hibernated.jpg` | The same list with a hibernated dataset, so the chip is visible |
| `console-field-configuration.jpg` | Field configuration on a 57-field dataset: types, the four roles, weights |
| `admin-users.jpg` | Admin → Users: accounts and platform roles |
| `admin-teams.jpg` | Admin → Teams: both teams with their members and team roles |
| `admin-datasets.jpg` | Admin → Datasets: every dataset across every team, with keep-alive |
| `admin-monitor.jpg` | Admin → Monitor: the in-memory event stream |

1456x830, captured in the dark theme at the default zoom.

## No customer data is in these

Every dataset, person and company here is invented, and the documents are generated. The people are
at `.example` addresses, which RFC 2606 reserves so they can never belong to anyone. Nothing on a
real instance may be photographed for this folder: every dataset on the working instance is a
customer's.

## Regenerating

The instance is built by tooling in `/presentation` of the internal IndxSolutions repository, which
is **deliberately not versioned** — it is one person's job and it must never ship. `Notes/presentation-screenshots-plan.md`
carries enough to rebuild it. In outline:

1. `node generate-datasets.mjs` writes nine datasets, 1,160,334 documents, from a seeded PRNG, so
   regeneration is byte-identical.
2. Clone this repository beside it, drop a licence in `IndxData/`, and configure an admin through
   `Identity:AdminEmail` — that is what skips the setup wizard, whose second step is an interactive
   circuit and cannot be scripted.
3. `node seed.mjs people`, then `teams` with the server stopped, then `datasets`, then `sleep`.
4. Capture with a browser at 1456x830.

Retake them when the UI meaningfully changes. A screenshot nobody looks at is how a release ships a
picture of a broken layout — which is not hypothetical: `admin-monitor.jpg` was captured while the
page's scoped stylesheet was matching nothing, and it shows "1 minute ago" wrapped over three lines.
That is fixed; the image still needs retaking.

## Known cosmetic issue

Numbers are formatted in the server's culture rather than the invariant one, so an instance running
under a Norwegian locale renders `937 886` and `1,86s` where an English-language page wants `937,886`
and `1.86s`. Visible in several of these. The terminal monitor was made invariant for the same
reason; the console has not been.
