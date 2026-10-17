#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

project=RepoIssuesCli/RepoIssuesCli.csproj
package=RepoIssuesCli
base=$(dotnet msbuild "$project" -getProperty:Version)
version="$base-local.$(date +%Y%m%d%H%M%S)"

rm -f nupkg/$package.*-local.*.nupkg
dotnet pack "$project" -c Release -p:Version="$version"

dotnet tool uninstall -g "$package" >/dev/null 2>&1 || true
dotnet tool install -g "$package" --add-source nupkg --version "$version"

echo "Installed issues $version"
