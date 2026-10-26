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
<!-- docs:begin (generated by docs render; edit .repo-docs instead) -->
## Repo

Monorepo for the shop

### repo/style: Use file-scoped namespaces

No `_` prefix on fields.

## Features

Each feature lives in `.repo-docs/<id>.yaml`.

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
- Everything outside the markers is kept byte for byte, including its line endings and a byte order mark. The block itself uses LF. Without markers the block is appended; a missing CLAUDE.md is created and `git add`ed. Edit the YAML, not the block: the next `render` overwrites it.
- Marker lines must start a line. A begin marker without an end marker, an end marker without a begin marker, or two blocks are reported as errors (exit `1`) and nothing is written.
- `render` needs `.repo-docs/` (run `docs init` first) and holds the docs lock while writing. Files that fail to load are reported on stderr and left out; the rest is still rendered.
- JSON: `path` is the absolute CLAUDE.md path, `created` whether this run created it, `changed` whether it was written, and `stale` whether the block was out of date before the run (with `--check`, `changed` is always `false`).

## Automation

- `--json` writes only JSON to stdout. For `init` that is `{ folder, created, files, features }`: `files` lists the repo-relative files it created, and `features` the prefilled features in the same shape as their YAML files. Arrays are always present, as `[]` when empty.
- Prompts never appear when you pass `--json` or `--no-interactive`, set `DOCS_NONINTERACTIVE=1` (or `ISSUES_NONINTERACTIVE=1`), or when stdin/stdout aren't a terminal.
- Errors go to stderr.
- Exit codes: `0` ok, `1` validation errors (an invalid docs file or CLAUDE.md markers, or a stale block under `render --check`), `2` usage error / missing input, `3` not found, `4` unexpected error.
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
- Moving an anchor's `line` hint is not an edit. Changing its path, span, symbol or `context`/`body` hashes is.
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
