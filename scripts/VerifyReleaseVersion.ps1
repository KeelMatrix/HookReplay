[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\")).Path
$versionScript = Join-Path $PSScriptRoot "GetReleaseVersion.ps1"
$version = (& pwsh -NoProfile -File $versionScript -Tag $Tag | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
    throw "Could not derive a release version from tag '$Tag'."
}

$expectedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$outputDirectory = Join-Path $repositoryRoot ("artifacts\release-version-verification-" + $version + "-" + [Guid]::NewGuid().ToString("N"))

function Invoke-CommandChecked([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $outputDirectory | Out-Null
try {
    $solution = Join-Path $repositoryRoot "KeelMatrix.HookReplay.sln"
    $project = Join-Path $repositoryRoot "src\KeelMatrix.HookReplay\KeelMatrix.HookReplay.csproj"
    Invoke-CommandChecked "dotnet" @(
        "restore", $solution, "--configfile", (Join-Path $repositoryRoot "NuGet.config")
    )
    Invoke-CommandChecked "dotnet" @(
        "build", $solution, "--configuration", "Release", "--no-restore", "-p:Version=$version"
    )
    Invoke-CommandChecked "dotnet" @(
        "test", $solution, "--configuration", "Release", "--no-build", "-p:Version=$version"
    )
    Invoke-CommandChecked "dotnet" @(
        "pack", $project, "--configuration", "Release", "--no-build", "--include-symbols",
        "-p:SymbolPackageFormat=snupkg", "-p:Version=$version", "--output", $outputDirectory
    )
    Invoke-CommandChecked "pwsh" @(
        "-NoProfile", "-File", (Join-Path $PSScriptRoot "InspectPackage.ps1"),
        "-PackageDirectory", $outputDirectory, "-Version", $version, "-ExpectedCommit", $expectedCommit
    )
    Write-Output "Release version verification passed for simulated tag $Tag ($version); no publication was attempted."
}
finally {
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }
}
