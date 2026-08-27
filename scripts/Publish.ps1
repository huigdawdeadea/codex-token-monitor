param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectRoot "publish\$Runtime"

dotnet test (Join-Path $projectRoot "tests\CodexTokenMonitor.Tests\CodexTokenMonitor.Tests.csproj") --configuration Release
dotnet publish (Join-Path $projectRoot "src\CodexTokenMonitor.App\CodexTokenMonitor.App.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $outputDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

Write-Output "Published to $outputDirectory"
