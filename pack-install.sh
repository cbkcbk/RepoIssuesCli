#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

stamp=$(date +%Y%m%d%H%M%S)

for tool in RepoIssuesCli:issues RepoDocsCli:docs; do
  package=${tool%%:*}
  command=${tool##*:}
  project=$package/$package.csproj
  base=$(dotnet msbuild "$project" -getProperty:Version)
  version="$base-local.$stamp"

  rm -f nupkg/$package.*-local.*.nupkg
  dotnet pack "$project" -c Release -p:Version="$version"

  dotnet tool uninstall -g "$package" >/dev/null 2>&1 || true
  dotnet tool install -g "$package" --add-source nupkg --version "$version"

  echo "Installed $command $version"
done
