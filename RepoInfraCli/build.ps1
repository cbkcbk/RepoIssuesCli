param(
    [switch]$SkipTests,
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..')

$project = 'RepoInfraCli/RepoInfraCli.csproj'
$tests = 'RepoInfraCli.Tests/RepoInfraCli.Tests.csproj'

dotnet build $tests -c Release -bl:infra-build.binlog
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    dotnet test $tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# dotnet run --project $project -c Release --no-build -- validate --root RepoInfraCli/demo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($NoInstall) { exit 0 }

$base = dotnet msbuild $project -getProperty:Version
$version = "$base-local.$(Get-Date -Format yyyyMMddHHmmss)"

Remove-Item 'nupkg/RepoInfraCli.*-local.*.nupkg' -ErrorAction SilentlyContinue
dotnet pack $project -c Release -p:Version=$version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (dotnet tool list -g | Select-String -Pattern '^repoinfracli\s' -Quiet) {
    dotnet tool uninstall -g RepoInfraCli
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
dotnet tool install -g RepoInfraCli --add-source nupkg --version $version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Installed infra $version"
