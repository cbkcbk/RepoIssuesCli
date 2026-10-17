$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

$project = 'RepoIssuesCli/RepoIssuesCli.csproj'
$package = 'RepoIssuesCli'
$base = dotnet msbuild $project -getProperty:Version
$version = "$base-local.$(Get-Date -Format yyyyMMddHHmmss)"

Remove-Item "nupkg/$package.*-local.*.nupkg" -ErrorAction SilentlyContinue
dotnet pack $project -c Release -p:Version=$version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet tool uninstall -g $package *> $null
dotnet tool install -g $package --add-source nupkg --version $version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Installed issues $version"
