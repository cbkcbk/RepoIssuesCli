$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

$stamp = Get-Date -Format yyyyMMddHHmmss
$tools = @(
    @{ Package = 'RepoIssuesCli'; Command = 'issues' },
    @{ Package = 'RepoDocsCli'; Command = 'docs' }
)

foreach ($tool in $tools) {
    $package = $tool.Package
    $project = "$package/$package.csproj"
    $base = dotnet msbuild $project -getProperty:Version
    $version = "$base-local.$stamp"

    Remove-Item "nupkg/$package.*-local.*.nupkg" -ErrorAction SilentlyContinue
    dotnet pack $project -c Release -p:Version=$version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet tool uninstall -g $package *> $null
    dotnet tool install -g $package --add-source nupkg --version $version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Installed $($tool.Command) $version"
}
