param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectRoot "publish\$Runtime"

dotnet test (Join-Path $projectRoot "tests\CodexTokenMonitor.Tests\CodexTokenMonitor.Tests.csproj") --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Release tests failed with exit code $LASTEXITCODE."
}

dotnet publish (Join-Path $projectRoot "src\CodexTokenMonitor.App\CodexTokenMonitor.App.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $outputDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Release publish failed with exit code $LASTEXITCODE."
}

Write-Output "Published to $outputDirectory"
