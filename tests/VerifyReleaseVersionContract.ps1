[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\")).Path
)

$ErrorActionPreference = "Stop"
$versionScript = Join-Path $RepositoryRoot "scripts\GetReleaseVersion.ps1"
$releaseWorkflow = Join-Path $RepositoryRoot ".github\workflows\release.yml"

function Fail([string] $Message) {
    throw "Release version contract failed: $Message"
}

function Assert-Equal([string] $Expected, [string] $Actual, [string] $Message) {
    if ($Expected -cne $Actual) {
        Fail "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        Fail $Message
    }
}

function Invoke-VersionScript([string] $Tag) {
    $output = (& pwsh -NoProfile -File $versionScript -Tag $Tag 2>&1 | Out-String).Trim()
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

foreach ($case in @(
    @{ Tag = "v0.1.0"; Version = "0.1.0" },
    @{ Tag = "v1.2.3"; Version = "1.2.3" },
    @{ Tag = "v10.20.30"; Version = "10.20.30" },
    @{ Tag = "v65534.65534.65534"; Version = "65534.65534.65534" }
)) {
    $result = Invoke-VersionScript $case.Tag
    Assert-Equal "0" $result.ExitCode "A valid release tag should be accepted."
    Assert-Equal $case.Version $result.Output "The validated tag should produce its canonical version."
}

foreach ($tag in @(
    "v01.2.3",
    "v1.02.3",
    "v1.2.03",
    "v65535.0.0",
    "v0.65535.0",
    "v0.0.65535",
    "v65535.65535.65535",
    "v65536.0.0",
    "v0.65536.0",
    "v0.0.65536",
    "v999999999999999999999999.0.0",
    'v1.2.3$(whoami)',
    "1.2.3",
    "v1.2"
)) {
    $result = Invoke-VersionScript $tag
    Assert-True ($result.ExitCode -ne 0) "Malformed or non-canonical tag '$tag' should be rejected."
}

if (-not (Test-Path -LiteralPath $releaseWorkflow -PathType Leaf)) {
    Fail "Release workflow '$releaseWorkflow' does not exist."
}

$workflowText = Get-Content -Raw -LiteralPath $releaseWorkflow
Assert-True (
    $workflowText -match '(?m)^\s+RELEASE_TAG:\s+\$\{\{\s*github\.ref_name\s*\}\}\s*$'
) "The release tag must be passed through an environment variable."
Assert-True (
    $workflowText -match '(?m)^\s+\$version\s*=.*-Tag\s+\$env:RELEASE_TAG\s+\|'
) "The release script must receive the environment variable rather than an interpolated PowerShell string."
Assert-True (
    $workflowText -notmatch '(?m)^\s*\$version\s*=.*\$\{\{\s*github\.ref_name\s*\}\}'
) "The release tag must not be interpolated inside a PowerShell run command."

$validationIndex = $workflowText.IndexOf("- name: Validate release tag", [StringComparison]::Ordinal)
$restoreIndex = $workflowText.IndexOf("- name: Restore from nuget.org", [StringComparison]::Ordinal)
$publishIndex = $workflowText.IndexOf("- name: Publish the expected package", [StringComparison]::Ordinal)
Assert-True ($validationIndex -ge 0 -and $restoreIndex -gt $validationIndex -and $publishIndex -gt $validationIndex) "Release tag validation must precede restore and publication."

Write-Output "Release version contract passed: canonical tags, leading-zero rejection, safe workflow tag transport, and validation ordering verified."
