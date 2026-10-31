#!/usr/bin/env bash
set -euo pipefail

skip_tests=false
install=true
for arg in "$@"; do
  case "$arg" in
    --skip-tests) skip_tests=true ;;
    --no-install) install=false ;;
    *) echo "usage: build.sh [--skip-tests] [--no-install]" >&2; exit 2 ;;
  esac
done

cd "$(dirname "$0")/.."

project=RepoInfraCli/RepoInfraCli.csproj
tests=RepoInfraCli.Tests/RepoInfraCli.Tests.csproj

dotnet build "$tests" -c Release -bl:infra-build.binlog

if [ "$skip_tests" = false ]; then
  dotnet test "$tests" -c Release --no-build
fi

dotnet run --project "$project" -c Release --no-build -- validate --root RepoInfraCli/demo

if [ "$install" = false ]; then
  exit 0
fi

base=$(dotnet msbuild "$project" -getProperty:Version)
version="$base-local.$(date +%Y%m%d%H%M%S)"

rm -f nupkg/RepoInfraCli.*-local.*.nupkg
dotnet pack "$project" -c Release -p:Version="$version"

dotnet tool uninstall -g RepoInfraCli >/dev/null 2>&1 || true
dotnet tool install -g RepoInfraCli --add-source nupkg --version "$version"

echo "Installed infra $version"
