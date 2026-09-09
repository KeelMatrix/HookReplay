[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $PackagePath = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "..\artifacts\packages") -Filter "KeelMatrix.HookReplay.*.nupkg" -File)
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one HookReplay package when PackagePath is omitted."
    }
    $PackagePath = $candidates[0].FullName
}

$package = (Resolve-Path -LiteralPath $PackagePath).Path
$packageName = [System.IO.Path]::GetFileNameWithoutExtension($package)
if ($packageName -notmatch '^KeelMatrix\.HookReplay\.(?<version>\d+\.\d+\.\d+)$') {
    throw "PackagePath must name a KeelMatrix.HookReplay vX.Y.Z package."
}
$packageVersion = $Matches.version
$packageDirectory = Split-Path -Parent $package
$consumerSource = Join-Path $PSScriptRoot "PackageConsumerSmoke"
$consumerRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("hookreplay-consumer-" + [Guid]::NewGuid().ToString("N"))
$project = Join-Path $consumerRoot "PackageConsumerSmoke.csproj"
$nugetConfig = Join-Path $consumerRoot "NuGet.Config"
$previousTelemetry = $env:KEELMATRIX_NO_TELEMETRY
$previousNugetPackages = $env:NUGET_PACKAGES

New-Item -ItemType Directory -Path $consumerRoot | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $consumerSource "PackageConsumerSmoke.csproj") -Destination $project
    Copy-Item -LiteralPath (Join-Path $consumerSource "Program.cs") -Destination (Join-Path $consumerRoot "Program.cs")

    $env:NUGET_PACKAGES = Join-Path $consumerRoot ".nuget"
    $configContents = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageDirectory" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="KeelMatrix.HookReplay" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="KeelMatrix.Redaction" />
      <package pattern="KeelMatrix.Telemetry" />
      <package pattern="System.*" />
      <package pattern="Microsoft.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    [System.IO.File]::WriteAllText(
        $nugetConfig,
        $configContents,
        [System.Text.UTF8Encoding]::new($false))
    & dotnet restore $project --configfile $nugetConfig --force-evaluate --no-cache -p:HookReplayPackageVersion=$packageVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Package consumer restore failed with exit code $LASTEXITCODE."
    }

    $env:KEELMATRIX_NO_TELEMETRY = "1"
    & dotnet run --project $project -c Release --no-restore -p:HookReplayPackageVersion=$packageVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Package consumer smoke failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($null -eq $previousTelemetry) {
        Remove-Item Env:KEELMATRIX_NO_TELEMETRY -ErrorAction SilentlyContinue
    }
    else {
        $env:KEELMATRIX_NO_TELEMETRY = $previousTelemetry
    }

    if ($null -eq $previousNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNugetPackages
    }

    if (Test-Path -LiteralPath $consumerRoot) {
        Remove-Item -LiteralPath $consumerRoot -Recurse -Force
    }
}
