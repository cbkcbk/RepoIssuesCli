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

## Automation

- `--json` writes only JSON to stdout. For `init` that is `{ folder, created, files, features }`: `files` lists the repo-relative files it created, and `features` the prefilled features in the same shape as their YAML files. Arrays are always present, as `[]` when empty.
- Prompts never appear when you pass `--json` or `--no-interactive`, set `DOCS_NONINTERACTIVE=1` (or `ISSUES_NONINTERACTIVE=1`), or when stdin/stdout aren't a terminal.
- Errors go to stderr.
- Exit codes: `0` ok, `1` validation errors, `2` usage error / missing input, `3` not found, `4` unexpected error.
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
