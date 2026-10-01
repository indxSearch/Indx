# Screenshots

Images for `README.md` and `docs/release-notes/`. Reference them as `docs/images/<name>.png` from
the repository root, or `../images/<name>.png` from inside `docs/release-notes/`.

| File | Shows |
|---|---|
| `console-datasets.png` | A team's dataset list: five datasets, document counts, Ready chips |
| `console-datasets-hibernated.png` | The same list with a hibernated dataset, so the chip is visible (the *list*; the page itself is `console-hibernated-wake.png`) |
| `console-field-configuration.png` | Field configuration on a nested 71-field dataset: objects and their children, types, the four roles, weights |
| `console-hibernated-wake.png` | A hibernated dataset's page, with its Wake up action |
| `console-boost-rules.png` | Boost rules on `bookshop`: four rules, one scheduled, one switched off |
| `console-synonyms.png` | The synonym list on `bookshop`: two-way entries and one one-way |
| `console-statistics.png` | The Statistics tab on `bookshop`: a month of searches, clicks and orders, the changes made to the dataset marked under the chart and listed below it. Seeded by `seed.mjs traffic` and `history` |
| `console-statistics-coverage.png` | The same tab's Without coverage section: misspellings coverage confirmed nothing for, with what visitors chose anyway beside the ones fuzzy matching rescued |
| `admin-users.png` | Admin → Users: accounts and platform roles. Not in the root README — six images is already a lot for one page; this one is here for release notes |
| `admin-teams.png` | Admin → Teams: both teams with their members and team roles |
| `admin-datasets.png` | Admin → Datasets: every dataset across every team, with keep-alive |
| `admin-monitor.png` | Admin → Monitor: the in-memory event stream |
| `tour.gif` | The README's opening image: nine of the console shots above, 1.2 s each with a short crossfade. Built from them by `capture.mjs`, never edited by hand |
| `tui.png` | The terminal monitor: instance figures, the dataset table, the event stream |

The console shots are **2912x1660 PNG** — a 1456x830 viewport at `deviceScaleFactor: 2`, dark
theme, default zoom — written by `presentation/capture.mjs` (Playwright), not by hand.

`tui.png` is hand-captured, and has to be: Terminal.Gui has no DOM, so nothing can drive it the way
Playwright drives the browser. Take it with the macOS window capture (`Cmd+Shift+4`, `Space`, click
the terminal) while the seed's activity is still in the event ring.

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
3. `node seed.mjs people`, then `teams` with the server stopped, then `datasets`, then `extras`
   (boost rules and a synonym list on `bookshop`, which is what those two images show).
   Hibernate `order-archive-2024` and `test-import-small` from their Options tabs, not with
   `seed.mjs sleep` — see `Notes/screenshot-run.md` for why.
4. `node seed.mjs traffic` (server running, before hibernating), then `history` with the server
   stopped: a month of search statistics, which the Statistics shot and every dataset card show.
5. `node capture.mjs` writes all of the console shots, then builds `tour.gif` from them with ffmpeg.
   `--light` for the light theme, `--gif-only` to rebuild just the GIF.

Retake them when the UI meaningfully changes, and **look at them** rather than trusting the run. A
screenshot nobody looks at is how a release ships a picture of a broken layout — not hypothetical
here: the monitor page's scoped stylesheet was matching nothing, and the picture was the only thing
that showed it.

`Notes/screenshot-run.md` is the full procedure.

## Number format

Numbers are formatted in the server's culture, so these images, taken under a Norwegian locale,
show `937 886` and `1,86s` rather than `937,886` and `1.86s`. That is intended, not a defect.
