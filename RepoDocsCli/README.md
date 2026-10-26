# docs — repo docs

Agent docs (the CLAUDE.md kind) kept as YAML inside your repository, one file per feature in `.repo-docs/`. The goals are to condense docs, make the relevant ones easy to find, and remove stale ones. `docs` is the companion of [`issues`](https://www.nuget.org/packages/RepoIssuesCli) and behaves the same way: `--json`, exit codes, `--root`, non-interactive mode, git staging and locking.

## Install

```sh
dotnet tool install -g RepoDocsCli     # global
dotnet tool install RepoDocsCli        # local (tool manifest)
```

Requires the .NET 10 runtime or later.

## Usage

```sh
docs init            # create .repo-docs/ at the repo root, one feature per Features/{X}/ folder
docs init --json     # { folder, created, files, features }
docs touch billing/rounding   # mark an entry (or a feature: docs touch billing) as updated now
docs touch           # stamp entries edited by hand since the last commit
docs touch --json    # { target, updated, files }
```

`init` is idempotent. On an existing `.repo-docs/` it only recreates a missing `repo.yaml`, `config.yaml` or `.gitignore`. It never prefills features again, so features you deleted stay deleted.

### Adding, editing and removing docs

`add`, `edit` and `rm` are the way to change docs. Agents should use them instead of editing the YAML, because the commands validate the change, create anchors from the current code, stamp timestamps, stage files in git and hold the lock.

```sh
# features
docs add billing --status in-progress --summary "Invoices and payments" --path App/Features/Billing/ --keyword invoice,vat
docs edit billing --status shipped --add-anchor App/Features/Billing/Invoice.cs:42-44
docs edit billing --id invoicing                  # rename: git mv billing.yaml invoicing.yaml
docs rm invoicing --force

# entries (feature/entry, or repo/entry for repo.yaml)
docs add billing/rounding --kind rule --summary "Round half to even" --body "Use MidpointRounding.ToEven." --path App/Features/Billing/Money.cs
docs add billing --entry --kind gotcha --summary "VAT rates are cached for an hour"   # id: vat-rates-are-cached-for-an-hour
docs add repo/style --kind rule --summary "Use file-scoped namespaces" --body-file style.md
git diff | docs add billing/review --kind fact --summary "Review notes" --body-stdin
docs edit billing/rounding --summary "Round once, half to even" --remove-keyword money --add-keyword rounding
docs edit billing/rounding --id money-rounding    # rename within the file
docs edit billing/rounding                        # interactive field menu
docs edit repo --summary "Monorepo for the shop"  # the repo.yaml summary
docs rm billing/rounding --force

# plan steps, with linked issues as trailing #n tokens
docs add billing/rollout --kind plan --summary "Roll out the new invoices" --step "Schema and store #23 #24" --step "Write the migration guide"
docs edit billing/rollout --add-step "Release #30" --set-step "2=Write the guide #31" --step-issues 1=23,24,25 --remove-step 3
```

`docs add <target>` creates a feature when the target is a feature id, and an entry when it is `feature/entry` or `repo/entry`:

- Features take `--status` (`proposed` by default; `in-progress` and `InProgress` both work), `--summary` and the hints `--path`, `--anchor` and `--keyword`.
- Entries take `--kind` (required, must be declared in `config.yaml`), `--summary` (required, one line), the body as `--body <text>`, `--body-file <path>` or `--body-stdin`, the same hints, and `--step` for `plan` entries.
- `--entry` creates an entry in the feature given as target and derives its id from the summary: lowercase ASCII words joined by `-`, cut at a word boundary to at most 40 characters, with apostrophes dropped (`entry` when no word is left). If the id is taken in that feature, `-2`, `-3`, … is appended. An explicit id that already exists is an error.
- Run interactively without a target, `add` prompts for the target, kind, summary and body.

`docs edit <target>` changes only what you pass:

- `--summary`, `--clear-summary` (features and `repo`), `--status` (features), `--kind`, `--body`/`--body-file`/`--body-stdin`, `--clear-body` (entries).
- `--add-path`/`--remove-path`, `--add-anchor`/`--remove-anchor` and `--add-keyword`/`--remove-keyword`, all repeatable. Removing a hint that isn't there is an error. `--remove-anchor` takes a path (every anchor in the file), `path:line` or a symbol (`Invoice.Total`). `--add-anchor` on the path and line of an existing anchor replaces it, which is how you re-anchor code that changed.
- Plan steps (plan entries only): `--add-step "<title> #n ..."` appends, `--set-step N="<title> #n ..."` replaces step N, `--step-issues N=23,24` sets its issues (`N=` clears them), and `--remove-step N` removes it. Steps are numbered from 1, as they were before the edit.
- `--id` renames. A feature's file is moved with `git mv`; an entry is renamed in place and keeps its timestamps.
- Without change options it opens a field menu when interactive, like `issues edit`, and is a usage error otherwise. An edit that changes nothing reports `Unchanged` and leaves the file alone, so `updated` only moves on real changes.

`docs rm <target>` removes a feature (its whole file, with `git rm`) or a single entry. It asks for confirmation, and needs `--force` when not interactive. `repo` itself can't be removed.

Shared rules:

- Paths and anchors are relative to the current folder when you run inside the repository, and stored repo-relative with `/`. An existing folder gets a trailing `/`.
- `--anchor path:line` or `path:line-end` is anchored on the file as it is now: the line hint, span, enclosing `symbol` and `context`/`body` hashes are computed for you. A missing file, or a line outside the file, is a usage error (exit `2`).
- Every change is validated before it is saved, and a change that would add a validation error (an undeclared kind, a summary over several lines, a missing summary, steps on a non-`plan` entry, an invalid or duplicate id) is refused with exit `2` and the problems on stderr. Problems that were already in other files don't block it.
- A missing feature or entry is exit `3`.
- `--json` writes the resulting item as `{ address, file, feature }` for a feature, `{ address, file, entry }` for an entry and `{ address, file, repo }` for `edit repo`; `rm` writes the removed item. `feature`, `entry` and `repo` have the same shape as in the YAML files; `file` is repo-relative (`.repo-docs/billing.yaml`).

### Rendering CLAUDE.md

```sh
docs render            # write the generated block into the repo-root CLAUDE.md
docs render --check    # write nothing; exit 1 when the block is missing or stale
docs render --json     # { path, created, changed, stale }
```

The always-loaded CLAUDE.md should hold only your global rules plus this generated index; everything else is fetched on demand. `render` writes the block between two marker lines:

```markdown
<!-- docs:begin (generated by docs render; change docs with the docs CLI, not here) -->
## Repo

Monorepo for the shop

### repo/style: Use file-scoped namespaces

No `_` prefix on fields.

## Features

Read a feature with `docs show <id>` (or one entry with `docs show <id>/<entry>`) and search with `docs find <query>`. Never read or edit `.repo-docs/` files directly.

- billing [in-progress]: Invoices and payments
  - billing/rounding: Round half to even
- search [shipped]

## Feature details

### billing/vat-cache: VAT rates are cached for an hour

Clear it with `vat reset`.
<!-- docs:end -->
```

- `## Repo` holds the `repo.yaml` summary and its entries in full (summary and body), unless their kind renders `never`.
- `## Features` has one line per feature (`id [status]: summary`), sorted by id, with `deprecated` features left out. Under each feature, entries whose kind renders `index` get one line (`feature/entry: summary`).
- `## Feature details` holds feature entries whose kind renders `always`, in full. Entries whose kind renders `never` or is not declared are left out. Entries keep their file order. Empty sections are omitted.
- The output is deterministic (no timestamps), so `--check` is a plain comparison. `--check` exits `1` when CLAUDE.md is missing, has no block, or has a different block.
- Everything outside the markers is kept byte for byte, including its line endings and a byte order mark. The block itself uses LF. Without markers the block is appended; a missing CLAUDE.md is created and `git add`ed. Change docs with the docs CLI (`docs add`/`edit`), not the block: the next `render` overwrites it.
- Marker lines must start a line. A begin marker without an end marker, an end marker without a begin marker, or two blocks are reported as errors (exit `1`) and nothing is written.
- `render` needs `.repo-docs/` (run `docs init` first) and holds the docs lock while writing. Files that fail to load are reported on stderr and left out; the rest is still rendered.
- JSON: `path` is the absolute CLAUDE.md path, `created` whether this run created it, `changed` whether it was written, and `stale` whether the block was out of date before the run (with `--check`, `changed` is always `false`).

### Importing existing CLAUDE.md files

```sh
docs import                      # import the root CLAUDE.md and AGENTS.md plus nested ones
docs import Web/CLAUDE.md        # import only the given markdown files
docs import --dry-run            # print the proposed entries; write nothing
docs import --json               # { dryRun, sources, imported, skipped, createdFeatures, imports }
```

`import` is the migration path for repos that already have agent docs. It turns markdown sections into entries; an agent or person then reviews them.

- Sources: without file arguments, the root `CLAUDE.md` and `AGENTS.md`, then every nested `CLAUDE.md` and `AGENTS.md`, skipping hidden folders, `bin`, `obj` and `node_modules`. File arguments are relative to the current directory and must be inside the repository.
- Splitting: each heading with text under it (up to the next heading of any level) becomes one entry, with the heading as `summary` and the text as the markdown `body`. Text before the first heading becomes an entry whose summary is its first sentence. Headings without text of their own are skipped, and headings inside code fences are text.
- Bullets: when a heading has no subheadings, is not a `how to` or `steps` heading, and its text is only a list of two or more top-level `-`, `*` or `+` bullets (indented lines belong to the bullet above), each bullet becomes its own entry. Its first sentence is the summary and the rest is the body. Numbered lists stay together.
- Summaries lose a trailing `.` or `:`. Entry ids are the summary in kebab-case, cut to about 40 characters at a word boundary, with `-2`, `-3` appended when the id is taken in that feature.
- The generated `docs render` block is skipped. Lines that are only an `@import` (`@docs/style.md`, optionally as a bullet) are left out of the entries and listed in the output; resolve them by hand.
- Feature: sections of a root file go to `repo.yaml`, unless one of their headings (nearest first) names an existing feature, as in `## Billing` for `billing`. A nested file goes to the feature whose `when.paths` is the longest prefix of the file's folder. Without a match, a `shipped` feature named after the folder is created with `when.paths` set to that folder (or the folder is added to an existing feature with that id). When a section's own heading names its feature, the summary is the first sentence of its text instead.
- Kind, from the headings, summary and body, first match wins:
  1. `gotcha`: gotcha, careful, beware, note that, watch out, pitfall, caveat.
  2. `howto`: how to, steps, two or more numbered lines, or a shell code block (`sh`, `bash`, `powershell`, `pwsh`, `cmd`, ... or `$ ` lines).
  3. `decision`: we chose, we picked, we went with, decided, decision.
  4. `rule`: always, never, must, don't, do not, avoid.
  5. `decision`: because.
  6. `fact` otherwise. A guessed kind that `config.yaml` doesn't declare becomes `fact`, or the first declared kind.
- Re-importing is safe: an entry whose summary and body already exist in the target feature is skipped and listed under `skipped` with the existing entry's address.
- A real run holds the docs lock, stamps `created` on new entries and features, and `git add`s new feature files. It stops with exit `1`, writing nothing, when a docs file fails to load. `import` needs `.repo-docs/` (run `docs init` first).
- Source files are never changed. After reviewing the entries, remove the imported text from the sources and run `docs render`.
- JSON: `sources` lists the repo-relative files read. `imported` and `skipped` hold `{ address, kind, summary, source, line }`, where `line` is the 1-based line of the heading or bullet. `createdFeatures` lists new feature ids and `imports` holds `{ source, line, text }` per `@import` line. `dryRun` is `true` under `--dry-run`, and the same entries are reported as a real run would write them.

### Checking docs

```sh
docs check             # exit 1 on errors; drift is a warning
docs check --strict    # exit 1 on warnings too
docs check --fix       # repair mechanical drift, then report what is left
docs check --json      # [{ kind, message, file?, address?, severity, fixable }]
docs check --fix --json  # { fixed: [...], problems: [...] }
docs validate          # alias of check
```

`check` is the staleness and consistency gate for agents and CI, with the same exit codes and `--strict` as `issues validate`. It reports:

| Kind | Severity | Fixable | Meaning |
|---|---|---|---|
| `InvalidFile` | error | no | A docs file that fails to load, or malformed CLAUDE.md markers. |
| `UndeclaredKind`, `MissingSummary`, `MultiLineSummary`, `DuplicateEntryId`, `InvalidEntryId`, `StepsOnNonPlan`, `InvalidStep` | error | no | Schema problems. |
| `MergeConflict` | error | when it merges per entry | A docs file with git conflict markers. |
| `BrokenAnchor` | error | no | An anchor that can't be found any more. |
| `MovedAnchor` | warning | yes | An anchor whose code moved unchanged (same file, a git rename or its symbol). |
| `ChangedAnchor` | warning | no | An anchor whose code changed: review the docs, then re-anchor. |
| `FuzzyAnchor` | warning | no | An anchor found only by similar context; confirm it before re-anchoring. |
| `DeadPath` | warning | no | A `when.paths` entry that matches no file or folder (a path ending in `/` must be a folder; a glob must match at least one file). |
| `StatusMismatch` | warning | no | A `planned` feature with linked `InProgress` issues, or a `shipped` one with linked issues still open. |
| `MissingIssue` | warning | no | A plan step links an issue number that is not in `.repo-issues`. |
| `StaleClaudeMd` | warning | yes | The generated CLAUDE.md block is missing or out of date. |
| `UnstampedEdit` | warning | yes | Only reported as fixed: hand edits stamped by `--fix`. |

- Errors mean the docs are wrong or unreadable; warnings mean they drifted from the code or the issues and need a look. A broken anchor is an error because the docs point at code that is gone.
- Anchors are resolved in this order: hint line, same file, git renames, symbol search, then similar context. All anchors are resolved in one pass that reads each file once; `.repo-issues` and `.repo-docs` are read once.
- A stale block is only reported when every docs file loads, since a skipped file would make the comparison meaningless.
- `--fix` holds the docs lock and, in order: merges files with conflict markers per entry (as the merge driver does), stamps hand edits since `HEAD` (as `docs touch`), rewrites the hints of `MovedAnchor` anchors, and re-renders a stale CLAUDE.md block. It never applies changed or fuzzy anchors. Anchor rewrites keep `created` and `updated` as they are, even when the path or context hash changes, since the code itself did not change. Fixed items are printed first, then what still needs a human or agent.
- JSON: without `--fix` an array of problems, like `issues validate --json`; with `--fix` an object whose `fixed` holds what was repaired and `problems` what is left, in the same shape. `kind` and `severity` (`Error` or `Warning`) are strings, `file` is the file name in `.repo-docs/` (or `CLAUDE.md`), `address` is the feature or `feature/entry` the problem belongs to, and `fixable` says whether `--fix` repairs it. Missing values are omitted. The exit code reflects only the remaining problems.

### Pruning stale docs

```sh
docs prune                          # a table of stale-docs candidates, strongest reason first
docs prune --json                   # [{ address, kind, summary, reasons: [{ code, message }], suggestion: { action, command } }]
docs prune --reason unread          # only candidates with this reason; repeatable
docs prune --feature billing        # only the feature and its entries (repo for repo.yaml)
docs prune --today 2026-10-26       # count unread days up to this date instead of today
```

`prune` is the worklist for removing stale docs. It is advice, not a gate: it always exits `0`, never writes a file and takes no lock. Act on a candidate through the docs commands, never by editing the YAML.

| Code | Applies to | Suggestion | Meaning |
|---|---|---|---|
| `DeprecatedOrphan` | feature | `delete` | A `deprecated` feature whose `when` paths and anchors all match nothing. |
| `Orphaned` | feature, entry | `delete` | Its `when` has paths or anchors, and none matches a file any more: every path is dead and every anchor broken. Keywords alone never make an orphan. |
| `BrokenAnchor` | feature, entry | `re-anchor` | An anchor that can't be found any more. |
| `ChangedAnchor` | feature, entry | `review` | An anchor whose code changed: check the docs still hold, then re-anchor. |
| `DeadPath` | feature, entry | `re-anchor` | A `when.paths` entry that matches no file or folder, as in `docs check`. |
| `ShippedPlan` | plan entry | `convert-to-decision` | A plan of a `shipped` feature: turn it into a `decision` or `fact`, or delete it. |
| `Unread` | entry | `review` | Not read for at least the kind's `prune.unreadDays`. |
| `CollapsibleStep` | plan entry | `collapse-steps` | Plan steps that are `done` (every linked issue `Done` or `Cancelled`). |

- The table is ordered from strongest to weakest. A candidate lists all its reasons in that order; the first one sets its suggestion. Candidates sort by their strongest reason, then by address.
- `Unread` counts days from the latest of `read`, `updated` and `created` (an edit or `docs touch` counts as a look), so an entry never read falls back to when it was created or last changed. An entry without any of these is skipped until it is stamped. Kinds without `prune.unreadDays` are never unread.
- `--today` defaults to today's UTC date.
- Suggestion commands: `docs rm <address>` for `delete`, `docs touch <address>` for an unread entry that is still correct, and `docs edit <address> ...` for the rest.
- Anchors and paths are inspected in the same pass as `docs check`. Claims are not checked yet.
- `--reason` takes the codes above, in any case and with or without dashes (`dead-path`). It keeps candidates with at least one of the given reasons and still shows all their reasons. An unknown `--feature` exits `3`.
- JSON: an array of candidates. `address` is the feature id for feature candidates and `feature/entry` (or `repo/entry`) for entries. `kind` is the entry kind and is omitted for features; `summary` is omitted when missing.

### Docs affected by a change

```sh
docs affected                            # docs hit by uncommitted changes but not updated
docs affected --since origin/main        # ... by everything since a ref (CI)
docs affected --since origin/main --strict  # exit 1 when anything is affected
docs affected --json                     # { since, entries: [{ address, kind, summary, matched, suggestion }] }
```

`affected` lists the docs that went stale in the current change: their code changed, but they were not updated. Review each one, then fix it with `docs edit <address>`, or run `docs touch <address>` when it is still correct. A Claude Code Stop hook runs it after an agent finishes; CI runs it with `--strict`.

- The diff is between `--since <ref>` (default `HEAD`) and the working tree: staged, unstaged and untracked files, with renames detected. Changes under `.repo-docs/` are not code changes. When the ref has moved on (say `origin/main` got new commits), pass the merge base instead (`--since $(git merge-base origin/main HEAD)`), since commits that are only on the ref show up as changes too.
- A feature or entry is **hit** when the diff touches one of its hints:
  - an anchor (`AnchorEdited`): a changed line falls inside the anchor's span, located in the ref's version of the file: at the line hint when the body hash matches there, else at the nearest line where the body hash, then the context hash, matches, else at the line hint. Lines added just before or after the span don't count; edits elsewhere in the file don't either. A deleted (`AnchorDeleted`) or renamed (`AnchorRenamed`) anchored file is always a hit;
  - a path (`PathAdded`, `PathModified`, `PathDeleted`, `PathRenamed`): a changed file that a `when.paths` entry covers, matched as in `docs for`. A file already hit through an anchor is not listed again as a path hit.
  - An entry without location hints (no `when.paths` or `when.anchors`) uses its feature's, as in `docs for`. Like `for`, it leaves out `decision` entries and `plan` entries unless their feature is `in-progress`. `repo.yaml` entries only count with their own hints.
- A hit feature or entry is **updated**, and not listed, when either:
  1. its content (an entry's `kind`, `summary`, `body` or `when`; a feature's `status`, `summary` or `when`) differs from the ref, or it is new since the ref; or
  2. its last change (`updated`, else `created`) is not earlier than every hit file's change time. A file with uncommitted changes changed at its last write time (a deleted file at its folder's); otherwise at the author time of the last commit since the ref that touched it.
- So `docs touch <address>` clears an entry until its code changes again. A deleted or renamed anchored file usually needs `docs edit` to fix the anchor anyway.
- Anchor hits sort first, since they are stronger than path hits; the rest keep file order (`repo.yaml`, then features by id, each feature before its entries).
- `affected` writes nothing and takes no lock. It runs at most five git commands: the changed names, untracked files, the `-U0` hunks (only when an anchored file was modified) and, for a ref other than `HEAD`, the uncommitted files and the commit times. It also reads the ref's version of each changed docs file and modified anchored file.
- Exit codes: `0`, also when docs are affected; `1` under `--strict` when anything is affected; `2` without `.repo-docs/`, outside a git repository, or for a ref that names no commit.
- JSON: `since` is the ref as given. Each entry has its `address` (a feature id, `feature/entry` or `repo/entry`), `kind` (`feature` for a feature), `summary`, a `suggestion` and `matched`: `{ path, anchor, previousPath, reason }` per hit file. `path` is the file's current repo-relative path (for deletions, the deleted path), `anchor` is the anchor as `path:line` and only on anchor hits, `previousPath` is the old path of a rename, and `reason` is one of the names above.

### Reading docs

Agents read docs through these commands, never through the YAML files.

```sh
docs show billing                 # a feature: status, summary, when hints and entry summaries
docs show billing --full          # ... with every entry's body
docs show billing/rounding        # one entry in full
docs show repo                    # the repo.yaml entries; repo/style is one of them
docs for src/Billing/Invoice.cs   # the docs that apply to a file or folder
docs for src/Billing --plans      # ... also plans of features that are not in-progress
docs for src/Billing --all-kinds  # ... every kind, including decisions
docs find "vat rounding"          # ranked search; --limit 5 (default 10)
docs list                         # features with status and entry count
docs list --status in-progress    # repeatable
```

- An address is `feature`, `feature/entry`, `repo` or `repo/entry`. An invalid address exits `2`, and a missing feature or entry exits `3`.
- `show` is a real read: it records today's date in `read` for the entry it shows, or for every entry with `show <feature> --full`. A feature shown without `--full` lists only summaries and records nothing. `for`, `find` and `list` never record reads.
- Plan entries show their progress, derived from `.repo-issues` (see [plan steps](#file-format)). Without a `.repo-issues` folder, `show` and `for` say so on stderr, list the steps without states and leave `progress` out of the JSON.
- `for` takes a path absolute or relative to the current directory (or to the repository root when the current directory is outside it) and makes it repo-relative; a path outside the repository exits `2`. It returns:
  - every `repo.yaml` entry;
  - feature entries whose `when.paths` cover the path or whose `when.anchors` name that file. A hint covers a path when it is the path itself or a folder above it, compared per path segment and ignoring case (`App/Billing` covers `App/Billing/Invoice.cs`, not `App/BillingOld/x.cs`). Hints may use simple globs: `*` and `?` within a segment, `**` across segments, and a glob without `/` (`*.cs`) matches the file name in any folder;
  - an entry without location hints (no `when.paths` or `when.anchors`) applies wherever its feature does. An entry with its own location hints applies only where they match.
- `for` returns current-state kinds only: it leaves out `decision` entries (history, not current state) and `plan` entries unless their feature is `in-progress`. `--plans` adds all plans, `--all-kinds` adds every kind. The same rule applies to `repo.yaml` entries, whose plans need `--plans`.
- `for` includes bodies of up to 600 characters (about 150 tokens); `--full` includes all of them. `hasBody` tells you an omitted body exists; read it with `docs show <address>`.
- `find` scores every feature and entry case-insensitively: `1000` when the query, or one of its words, is the id or address, plus `100` per word found in the summary, `10` per word found in a keyword, and `1` per word found in the body. Results with a score of `0` are dropped; ties sort by address.
- Query commands need `.repo-docs/`. Files that fail to load are reported on stderr as warnings and left out.

JSON shapes. Arrays are always present, as `[]` when empty; missing values are omitted.

- `show <feature>` and `show repo`: `{ feature, status, summary, when: { paths, anchors, keywords }, created, updated, entries: [entry] }`. `repo` has no `status`, `when` or timestamps.
- `show <feature>/<entry>`: one entry.
- An entry is `{ address, feature, id, kind, summary, when, steps: [{ title, issues }], created, updated, read, hasBody, body, progress, match }`. `feature` is `repo` for `repo.yaml` entries. `when` is omitted when the entry has no hints. `body` is present when it is shown (always for a single entry). `progress` is only on plan entries, as `{ done, total, cancelled, missing, steps: [{ title, state, done, total, issues: [{ number, name, state, blockedBy, blocked }] }], complete }`. `match` is only in `for` output: `repo`, `feature` (inherited from the feature's hints) or `entry` (the entry's own hints).
- `for`: `{ path, features: [{ feature, status, summary }], entries: [entry] }`. `path` is repo-relative with `/`; `features` lists the features that matched or contributed an entry, sorted by id; `entries` holds the `repo.yaml` entries first, then each feature's in file order.
- `find`: `[{ address, feature, kind, summary, score }]`, best first. A feature hit has `kind` `feature` and its id as `address`.
- `list`: `[{ feature, status, summary, entryCount }]`, sorted by id.

### Token budget

```sh
docs budget                 # always-loaded vs on-demand token cost, with totals
docs budget --by kind       # on-demand costs per kind only (or --by feature); both by default
docs budget --top 5         # list the 5 largest entries as condensing targets (default 10, 0 for none)
docs budget --json
```

`budget` is the headline metric for condensing: what every session pays up front, against what agents fetch only when they need it. It is read-only, takes no lock and exits `0`.

- Tokens are estimated, without a tokenizer, as `max(ceil(chars / 4), ceil(words * 4 / 3))`, where a line ending counts as one character and a word is a run of non-whitespace. The estimate is deterministic, and the output states the method.
- Always loaded:
  - the root `CLAUDE.md` as it is on disk, split into your text outside the generated block and the block itself;
  - the files it imports with `@path`, followed recursively. Paths are relative to the importing file. Each file is counted once, so cycles end. Imports more than 5 hops deep and missing files are listed as skipped; `~/` paths are listed as external and not counted. `@` references inside code fences or inline code spans don't count, and neither do e-mail addresses.
  - Also shown: what the block would cost if `docs render` ran now, so a stale block shows its delta.
- Contextual: nested `CLAUDE.md` files (skipping hidden folders, `bin`, `obj` and `node_modules`). Claude Code loads them only when working in their folder, so they are listed with their own and their imports' cost and not added to the always-loaded total.
- On demand: every entry's summary plus body, including `repo.yaml` entries and kinds that never render, per feature (every feature, `repo` included) and per kind.
- Push: what hooks would inject per the kind's `push` policy: bodies of up to 600 characters in full, longer ones as the summary only. `when-feature-active` counts only entries of `in-progress` features. It is the worst case, as if every entry were pushed once.
- Without `.repo-docs/`, `CLAUDE.md` and its imports are still measured, with a note; the on-demand and push parts are empty.

JSON shape. Arrays are always present, as `[]` when empty; missing values are omitted. `--by` only changes the text output.

```
{
  method,
  alwaysLoaded: {
    total,
    claudeMd: { path, exists, outsideBlock, block, blockIfRendered, stale },
    imports: [{ path, tokens, depth, from }],
    external: [{ path, from }],
    skipped: [{ path, from, reason }],
  },
  contextual: [{ path, tokens, importTokens }],
  onDemand: {
    total, entries,
    byFeature: [{ feature, entries, summaryTokens, bodyTokens, tokens }],
    byKind: [{ kind, render, push, entries, summaryTokens, bodyTokens, tokens }],
  },
  push: { total, byPolicy: [{ policy, entries, full, summaryOnly, tokens }] },
  largest: [{ address, kind, tokens }],
  notes: [],
}
```

- `total` under `alwaysLoaded` is `outsideBlock + block + ` the imports. `blockIfRendered` and `stale` are omitted without `.repo-docs/`; a missing `CLAUDE.md` counts as stale.
- Paths are repo-relative with `/` (absolute when outside the repository); `from` is the importing file and `depth` its hop count from `CLAUDE.md` (1 for a direct import). `reason` is `missing` or `too-deep`.
- `byFeature`, `byKind` and `largest` are sorted by tokens, largest first. `render` and `push` are omitted for an undeclared kind. `byPolicy` always lists `on-edit`, `on-read` and `when-feature-active`; `full` and `summaryOnly` count the entries pushed with and without their body.
- A `CLAUDE.md` with malformed markers is counted as text outside the block, with a note.

## Automation

- `--json` writes only JSON to stdout. For `init` that is `{ folder, created, files, features }`: `files` lists the repo-relative files it created, and `features` the prefilled features in the same shape as their YAML files. Arrays are always present, as `[]` when empty.
- Prompts never appear when you pass `--json` or `--no-interactive`, set `DOCS_NONINTERACTIVE=1` (or `ISSUES_NONINTERACTIVE=1`), or when stdin/stdout aren't a terminal.
- Errors go to stderr.
- Exit codes: `0` ok, `1` validation errors (an invalid docs file or CLAUDE.md markers, a stale block under `render --check`, `check` errors, or warnings with `check --strict`, affected docs with `affected --strict`), `2` usage error / missing input, `3` not found, `4` unexpected error.
- `--root <path>` overrides repository discovery. By default `docs` walks up from the current directory to the nearest folder containing `.repo-issues`, `.repo-docs` or `.git`.

## File format

```
.repo-docs/
  .gitignore       # ignores the .lock file
  repo.yaml        # repo-wide entries
  config.yaml      # settings, including the entry kinds
  billing.yaml     # one file per feature, named after its id
```

A feature, `.repo-docs/billing.yaml`:

```yaml
feature: billing
status: in-progress
summary: Invoices and payments
when:
  paths:
    - App/Features/Billing/
  anchors:
    - path: App/Features/Billing/Invoice.cs
      line: 42
      span: 3
      symbol: Invoice.Total
      context: a1b2c3d4e5f6
      body: 0f1e2d3c4b5a
  keywords:
    - invoice
    - vat
created: 2026-10-20T09:15:00Z
updated: 2026-10-26T14:33:11Z
entries:
  - id: rounding
    kind: rule
    summary: Round half to even
    when:
      paths:
        - App/Features/Billing/Money.cs
    created: 2026-10-20T09:15:00Z
    updated: 2026-10-21T08:00:00Z
    read: 2026-10-26
    body: |
      Use MidpointRounding.ToEven.
      Never round twice.
  - id: vat-cache
    kind: gotcha
    summary: VAT rates are cached for an hour
```

`config.yaml`, as written by `init`:

```yaml
version: 1
kinds:
  rule:
    render: index
    push: on-edit
  fact:
    render: never
    push: on-read
    prune:
      unreadDays: 180
  gotcha:
    render: never
    push: on-edit
    prune:
      unreadDays: 365
  howto:
    render: index
    push: never
    prune:
      unreadDays: 180
  decision:
    render: never
    push: never
  plan:
    render: never
    push: when-feature-active
```

`repo.yaml` holds repo-wide entries in the same entry format:

```yaml
entries: []
```

A `plan` entry lists its `steps`, each with a `title` and the numbers of the [`issues`](https://www.nuget.org/packages/RepoIssuesCli) that implement it:

```yaml
  - id: rollout
    kind: plan
    summary: Roll out the new invoices
    steps:
      - title: Schema and store
        issues: [23, 24]
      - title: Write the migration guide
```

- Only `plan` entries may have `steps`. A step needs a one-line `title`; `issues` is optional and omitted when empty.
- Progress is derived from `.repo-issues` whenever it is shown and never stored. A plan counts its distinct linked issues: `Done` and `Cancelled` ones are resolved (cancelled ones are still listed), and a missing issue number counts as unresolved and is flagged.
- A step is `done` when it has issues and all are resolved, `in-progress` when any is `InProgress`, `blocked` when every remaining `Open` issue has an unresolved or missing blocker (as in `issues list --blocked`), and `open` otherwise, including steps without issues.

- The tool owns the formatting. Files are rewritten in a fixed layout, so YAML comments are not kept.
- A feature id is lowercase kebab-case (`user-accounts`) and must match its file name. `repo` and `config` are reserved.
- `status` is set by hand: `proposed`, `planned`, `in-progress`, `shipped` or `deprecated`. Progress is never stored; it is derived from linked issues.
- `when` holds the hints that make a feature or entry relevant: `paths` (files or folders, folders end in `/`), `anchors` (code locations with a line hint, a span of non-blank lines, an optional `symbol`, and `context` and `body` fingerprints) and `keywords`. Empty lists are omitted. An entry without `when` applies wherever its feature does.
- An entry has an `id`, a `kind`, a one-line `summary` and an optional markdown `body`, written as a literal block. Entry ids are lowercase kebab-case and unique within their feature (or `repo.yaml`). An entry is addressed as `feature/entry`, and repo-wide entries as `repo/entry`.
- `created` and `updated` are UTC timestamps (`2026-10-26T14:33:11Z`); `read` is a date (`2026-10-26`). See [Timestamps](#timestamps).
- `config.yaml` declares the entry kinds. An entry whose kind is not declared is invalid. Each kind sets:
  - `render`: how it appears in the generated CLAUDE.md block. `always` renders the whole entry, `index` one line with its summary, `never` nothing.
  - `push`: when hooks push it to the agent. `on-edit` when a matching file is edited, `on-read` when one is read, `when-feature-active` when the feature is `in-progress`, `never` only on request.
  - `prune`: when it becomes a prune candidate. `unreadDays` flags entries not read for that many days; without it, the kind is never pruned for being unread.
  - A kind written as `{}` renders and pushes nothing and is never pruned for being unread.
- `init` prefills one feature per `Features/{X}/` folder anywhere in the repository, skipping hidden folders, `bin`, `obj` and `node_modules`. The id is `{X}` in kebab-case (`UserAccounts` becomes `user-accounts`, and a reserved name gets a `-feature` suffix). Folders with the same name in different projects merge into one feature with several `when.paths`. Prefilled features are `shipped`, because their code already exists.
- New files are `git add`ed, renamed features are moved with `git mv` and deleted ones are `git rm`ed. Outside a git repository, plain file operations are used.
- Writes hold `.repo-docs/.lock`, which is listed in `.repo-docs/.gitignore`.
- Unknown keys, invalid YAML and a `feature` id that doesn't match the file name are reported as errors for that file. They never crash the tool.
- Missing values are omitted.

## Timestamps

Timestamps drive staleness checks, so the tool keeps them honest:

- `created` is set when a feature or entry first appears. A missing `created` on an existing one is restored from the previous version, or set to now.
- `updated` changes only when content changes. For an entry that is its `kind`, `summary`, `body` or `when`; for a feature its `status`, `summary` or `when`. Editing an entry does not touch its feature's `updated`. New items get `created` only.
- An anchor counts as edited only when what it points at changes: its `body` hash or `span`. Its `path`, `line`, `symbol` and `context` are location hints, so re-locating an anchor (by `check --fix`, a rename or a symbol move) is not an edit.
- Reading never changes `updated`. `read` is the UTC date of the last real read (`show`, or a hook pushing the full body), never `list`, `find` or the index. It is written only when the date changes, so reading an entry twice in a day leaves the file untouched.
- Every write by the tool compares against the file on disk and stamps what changed.
- Hand edits (by you or an agent editing the YAML) are stamped by comparing each file with its version at `HEAD`: run `docs touch` before committing. An edit whose `updated` is already later than `HEAD`'s is left alone, so running it twice changes nothing.
- `docs touch feature/entry` sets `updated` to now without an edit, for an entry you checked and found still correct after its code changed.

### Merging

When two branches change the same file, the later timestamp wins. Entries merge one by one by id: the version with the later `updated` (or `created`) wins, `read` takes the later date on its own, and entries that exist on one side only are kept. With a common ancestor, a side that left an entry unchanged loses to the side that edited it, and an entry deleted on one side and unchanged on the other is deleted. Feature fields follow the same rule. `config.yaml` is never merged this way.

To merge `.repo-docs` files automatically, register the merge driver:

```sh
git config merge.repo-docs.name "docs per-entry merge"
git config merge.repo-docs.driver "docs merge-driver %O %A %B %P"
```

and in `.gitattributes`:

```
.repo-docs/*.yaml merge=repo-docs
```

The driver first tries a normal three-way text merge. If that conflicts, it merges per entry; if it can't (invalid YAML, `config.yaml`), it leaves the conflict markers and exits `1`, like a regular conflict.
