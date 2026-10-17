# issues — repo issues

An issue tracker that lives in your repository. Each issue is a JSON file in `.repo-issues/`, so issues are versioned, branched and reviewed with the code. Humans get interactive prompts. Scripts and agents get flags and `--json`.

## Install

```sh
dotnet tool install -g RepoIssuesCli     # global
dotnet tool install RepoIssuesCli        # local (tool manifest)
```

Requires the .NET 11 runtime or later.

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
issues delete 1 --force                              # also removes it from other issues' blockers
issues validate                                      # exit code 1 when problems are found
```

Issue references (`<ref>`) accept a number (`3`, `0003`), a name or unique name prefix, a full title (`0003 Fix login`), or the issue id.

## Automation

- `--json` writes only JSON to stdout: one issue for `new`/`show`/`edit`/`status`/`delete`, an array for `list` and `validate`.
- Prompts never appear when you pass `--json` or `--no-interactive`, set `ISSUES_NONINTERACTIVE=1`, or when stdin/stdout aren't a terminal. In those modes, missing input is an error.
- Errors go to stderr.
- Exit codes: `0` ok, `1` validation problems, `2` usage error / missing input, `3` issue not found, `4` unexpected error.
- `--root <path>` overrides repository discovery. By default `issues` walks up from the current directory to the nearest folder containing `.repo-issues`, then `.git`.

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

## File format

`.repo-issues/{uuid-v7}.json`:

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
  "updated": "2026-10-17T16:25:54+00:00"
}
```

- `status`: `Open`, `InProgress`, `Done`, `Cancelled`.
- `priority`: `Low`, `Medium`, `High`, `Critical`.
- `author` defaults to the git author (`user.name <user.email>`, honoring `GIT_AUTHOR_NAME`/`GIT_AUTHOR_EMAIL`). Override it with `--author`, and filter with `issues list --author <text>`.
- `blockedBy` holds issue numbers, so renaming an issue never breaks references.
- Empty lists and missing values are omitted.
- Location paths are repo-relative with `/` separators.
- Numbers are assigned as highest + 1. If two branches create the same number, `issues validate` reports the duplicate after the merge.
