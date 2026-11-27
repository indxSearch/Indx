# Screenshots

Images for `README.md` and `docs/release-notes/`. Reference them as `docs/images/<name>.png` from
the repository root, or `../images/<name>.png` from inside `docs/release-notes/`.

| File | Shows |
|---|---|
| `console-datasets.png` | A team's dataset list: five datasets, document counts, Ready chips |
| `console-datasets-hibernated.png` | The same list with a hibernated dataset, so the chip is visible (the *list*; the page itself is `console-hibernated-wake.png`) |
| `console-field-configuration.png` | Field configuration on a nested 71-field dataset: objects and their children, types, the four roles, weights |
| `console-records.png` | The Records section on `product-catalog-staging`: the records as a table, with filters, columns and the scan |
| `console-records-report.png` | The scan's report on `customer-records`: each field, how many records lack it, worst first, and a button that lists them |
| `console-hibernated-wake.png` | A hibernated dataset's page, with its Wake up action |
| `console-status.png` | The Status tab on `bookshop`: the index and usage tiles, each with its icon |
| `console-categories.png` | The Categories tab on `product-catalog-staging`: the category paths as a tree, opened to the second level |
| `console-boost-rules.png` | Boost rules on `bookshop`: four rules, one scheduled, one switched off |
| `console-synonyms.png` | The synonym list on `bookshop`: two-way entries and one one-way |
| `console-query-parameters.png` | Query parameters on `bookshop`: the values a search takes when its request leaves them out, typos, typing and the result list in front, the rest under Advanced |
| `console-statistics.png` | The Statistics tab on `bookshop`: a month of searches, clicks and orders, the changes made to the dataset marked under the chart and listed below it. Seeded by `seed.mjs traffic` and `history` |
| `console-statistics-overview.png` | The same tab's Overview section: the tiles, when people search as a grid and a clock, searches by surface, which result is chosen, how people type |
| `console-statistics-coverage.png` | The same tab's Partial matches section: misspelled searches, how often a result was chosen anyway, and the result chosen most |
| `console-statistics-journeys.png` | The same tab's Journeys section: how visits that began with a search went on, to a chosen result, a conversion or nothing |
| `console-statistics-changes.png` | The same tab's Changes section: what the owners changed, beside what visitors did |
| `admin-users.png` | Admin → Users: accounts and platform roles. Not in the root README — six images is already a lot for one page; this one is here for release notes |
| `admin-teams.png` | Admin → Teams: both teams with their members and team roles |
| `admin-datasets.png` | Admin → Datasets: every dataset across every team, with keep-alive |
| `admin-monitor.png` | Admin → Monitor: CPU and memory rings, the native pool, searches, every dataset's state, then the in-memory event stream |
| `tour.gif` | The README's opening image: fifteen of the console shots above, 1.2 s each with a short crossfade, the dataset screens in the order of the dataset page's side menu. Built from them by `capture.mjs`, never edited by hand |
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

The images are taken by our own tooling from generated data, so they hold no one's real records.
They are retaken when the console changes, and looked at, not trusted to the run.

## Number format

Numbers are formatted in the server's culture, so these images, taken under a Norwegian locale,
show `937 886` and `1,86s` rather than `937,886` and `1.86s`. That is intended, not a defect.
