# issues — repo issues

An issue tracker that lives in your repository. Each issue is a JSON file in `.repo-issues/`, so issues are versioned, branched and reviewed with the code. Humans get interactive prompts. Scripts and agents get flags and `--json`.

## docs

This repository also holds `docs` (RepoDocsCli), a companion tool for agent docs (the CLAUDE.md kind). It keeps them as YAML in `.repo-docs/`, one file per feature, and behaves like `issues` for `--json`, exit codes and `--root`. Both tools share `RepoCli.Core`. See [RepoDocsCli/README.md](RepoDocsCli/README.md).

## Install

```sh
dotnet tool install -g RepoIssuesCli     # global
dotnet tool install RepoIssuesCli        # local (tool manifest)
```

Requires the .NET 10 runtime or later.

To build and install the current checkout as a global tool, run `./pack-install.sh` or `./pack-install.ps1`.

## Usage

```sh
issues init                                          # create .repo-issues/ at the repo root
issues new "Login fails on empty password" -p High -t bug -l src/Auth/Login.cs:42:9
issues new "Add password rules" -b 1                 # blocked by issue 1
issues new                                           # interactive wizard
issues list                                          # open issues (hides Done/Cancelled)
issues list --all | --status Done | --tag bug | --ready | --blocked
issues show 1                                        # details + blocker tree
issues edit 1 --name "Login rejects empty password" --add-tag auth --add-blocker 3
issues edit 1                                        # interactive field menu
issues status 1 in-progress
issues comment 1 "Reproduced on main"                # prompts for the text when omitted
issues delete 1 --force                              # also removes it from other issues' blockers
issues validate                                      # exit code 1 on errors; stale locations are warnings
issues validate --strict                             # exit code 1 on warnings too
issues renumber <id>                                 # give an issue the next free number (fixes duplicates)
```

A `blockedBy` number with no matching issue counts as blocking: the issue shows under `--blocked`, not `--ready`, with the blocker marked `(missing)`.

`renumber` rewrites `blockedBy` references when the old number was unique. When the number is duplicated, references can't tell the duplicates apart, so they stay with the issue that keeps the number; the output lists them so you can move any with `issues edit`. A bare duplicated number is ambiguous: pass the id or name, or pick from the prompt.

Interactive description and comment prompts take multiple lines; a blank line finishes. When editing, a blank first line keeps the current description and `-` clears it.

Issue references (`<ref>`) accept a number (`3`, `0003`), a name or unique name prefix, a full title (`0003 Fix login`), or the issue id.

## Automation

- `--json` writes only JSON to stdout: one issue for `new`/`show`/`edit`/`status`/`comment`/`delete`, an array for `list`, an array of `{ kind, message, file?, severity }` for `validate` (`severity` is `Error` or `Warning`), and `{ issue, previousNumber, rewritten, keptReferences }` for `renumber`. Issues always include `blockedBy`, `locations`, `tags` and `comments`, as `[]` when empty, though issue files leave empty lists out.
- Prompts never appear when you pass `--json` or `--no-interactive`, set `ISSUES_NONINTERACTIVE=1` (or `DOCS_NONINTERACTIVE=1`), or when stdin/stdout aren't a terminal. In those modes, missing input is an error.
- Errors go to stderr.
- Exit codes: `0` ok, `1` validation errors (or warnings with `validate --strict`), `2` usage error / missing input, `3` issue not found, `4` unexpected error.
- `--root <path>` overrides repository discovery. By default `issues` walks up from the current directory to the nearest folder containing `.repo-issues`, `.repo-docs` or `.git` (a folder, or the file used by worktrees and submodules), so running it from a subfolder uses the repository root. Outside any repository it uses the current directory.

## Claude Code plugin

This repository is also a Claude Code plugin marketplace (`repo-issues`). Its `issues` plugin ships a skill that teaches Claude to drive the CLI. Install it with:

```
/plugin install issues --marketplace <owner>/RepoIssuesCli
```

On older Claude Code versions, run `/plugin marketplace add <owner>/RepoIssuesCli` and then `/plugin install issues@repo-issues`.

The marketplace and skill files (`.claude-plugin/`, `plugins/issues/`) are generated from the CLI's own command model. After changing commands, regenerate them with the debug-only command, which is never packed into the NuGet tool:

```sh
dotnet run --project RepoIssuesCli -- generate-plugin
```

## Releasing

CI (`.github/workflows/ci.yml`) tests and packs every push and pull request. Pushing a `vX.Y.Z` tag also publishes `RepoIssuesCli` version `X.Y.Z` to nuget.org:

```sh
git tag v0.2.0 && git push origin v0.2.0
```

Publishing uses nuget.org trusted publishing, so no API key is stored. One-time setup: on nuget.org, add a trusted publishing policy for this repository with workflow file `ci.yml` and environment `nuget`, then add your nuget.org username as the `NUGET_USER` repository secret.

## File format

`.repo-issues/{number:00000}-{name_in_snake_case}.json`, e.g. `00001-login_fails_on_empty_password.json`:

```json
{
  "id": "01a14aaf-2636-76c1-8f28-fed5d98d1f75",
  "number": 1,
  "name": "Login fails on empty password",
  "description": "Optional text",
  "author": "Ada Lovelace <ada@example.com>",
  "status": "Open",
  "priority": "High",
  "blockedBy": [3, 7],
  "locations": [{ "path": "src/Auth/Login.cs", "line": 42, "column": 9 }],
  "tags": ["bug"],
  "created": "2026-10-17T16:25:54+00:00",
  "updated": "2026-10-17T16:31:02+00:00",
  "comments": [{ "author": "Grace Hopper <grace@example.com>", "created": "2026-10-17T16:31:02+00:00", "text": "Reproduced on main" }]
}
```

- The name part is cut at a word boundary after 60 characters. Changing an issue's name or number renames its file with `git mv`, so git keeps its history. Files with any other name, such as the older `{uuid}.json` files, are renamed the next time `issues` runs.
- New issue files are `git add`ed and deleted ones `git rm`ed. Outside a git repository, plain file operations are used.
- `status`: `Open`, `InProgress`, `Done`, `Cancelled`.
- `priority`: `Low`, `Medium`, `High`, `Critical`.
- `author` defaults to the git author (`user.name <user.email>`, honoring `GIT_AUTHOR_NAME`/`GIT_AUTHOR_EMAIL`). Override it with `--author`, and filter with `issues list --author <text>`.
- `comments` are appended in order by `issues comment`, which also bumps `updated`. A comment's `author` defaults to the git author like the issue's.
- `blockedBy` holds issue numbers, so renaming an issue never breaks references.
- Empty lists and missing values are omitted.
- Location paths are repo-relative with `/` separators.
- Numbers are assigned as highest + 1 while holding `.repo-issues/.lock`, so concurrent `issues new` and `issues renumber` runs in one checkout (e.g. several agents) never pick the same number. The lock file is listed in `.repo-issues/.gitignore`. If two branches create the same number, `issues validate` reports the duplicate after the merge and `issues renumber` fixes it.
