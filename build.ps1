<#
.SYNOPSIS
    Builds NEWS//WATCH and publishes a single-file exe.

.EXAMPLE
    .\build.ps1                        # x64, framework-dependent (needs .NET 8 runtime on target) -> dist\
    .\build.ps1 -SelfContained         # x64, portable exe, no .NET install needed (~150 MB)       -> dist\
    .\build.ps1 -Arm64                 # Windows on ARM (Snapdragon etc.), framework-dependent    -> dist-arm64\
    .\build.ps1 -Arm64 -SelfContained  # Windows on ARM, portable                                  -> dist-arm64\
    .\build.ps1 -DebugBuild            # plain Debug build only, no publish
#>
param(
    [switch]$SelfContained,
    [switch]$Arm64,
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Locate dotnet even if PATH hasn't been refreshed since the SDK install.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $fallback = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path $fallback) { $dotnet = $fallback }
    else { throw ".NET SDK not found. Install with: winget install Microsoft.DotNet.SDK.8" }
} else {
    $dotnet = $dotnet.Source
}

if ($DebugBuild) {
    & $dotnet build NewsWatch.csproj -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    Write-Host "`nDebug build: bin\Debug\net8.0-windows\NewsWatch.exe" -ForegroundColor Cyan
    exit 0
}

# x64 keeps the historical dist\ path (auto-start entries may point at it);
# ARM64 gets its own folder so the two never overwrite each other.
$rid    = if ($Arm64) { "win-arm64" } else { "win-x64" }
$outDir = if ($Arm64) { "dist-arm64" } else { "dist" }

$publishArgs = @(
    "publish", "NewsWatch.csproj",
    "-c", "Release",
    "-r", $rid,
    "-o", $outDir,
    "--nologo",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" })
)

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exe = Join-Path $PSScriptRoot "$outDir\NewsWatch.exe"
$size = "{0:N1} MB" -f ((Get-Item $exe).Length / 1MB)
Write-Host "`nPublished ($rid): $exe ($size)" -ForegroundColor Cyan
