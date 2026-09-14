[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

function Fail([string] $Message) {
    throw "Release build contract failed: $Message"
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        Fail $Message
    }
}

function Invoke-Native([string] $Command, [string[]] $Arguments) {
    $output = @(& $Command @Arguments 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    }
}

$gateScript = Join-Path $RepositoryRoot "scripts\Test-ReleaseBuild.ps1"
$ciWorkflow = Join-Path $RepositoryRoot ".github\workflows\ci.yml"
$releaseWorkflow = Join-Path $RepositoryRoot ".github\workflows\release.yml"
Assert-True (Test-Path -LiteralPath $gateScript -PathType Leaf) "The reusable Release build gate script must exist."
Assert-True (Test-Path -LiteralPath $ciWorkflow -PathType Leaf) "The CI workflow must exist."
Assert-True (Test-Path -LiteralPath $releaseWorkflow -PathType Leaf) "The release workflow must exist."

$gateText = Get-Content -Raw -LiteralPath $gateScript
Assert-True ($gateText -match '(?m)--configuration') "The gate must select a configuration."
Assert-True ($gateText -match '(?m)"Release"') "The gate must build Release configuration."
Assert-True ($gateText -match '(?m)--no-restore') "The gate must run after the repository-controlled restore."
Assert-True ($gateText -match '(?m)-p:TreatWarningsAsErrors=true') "The gate must enable warnings-as-errors explicitly."
Assert-True ($gateText -match 'Get-SummaryCount\s+"Warning"') "The gate must inspect the warning summary."
Assert-True ($gateText -match 'Get-SummaryCount\s+"Error"') "The gate must inspect the error summary."
Assert-True ($gateText -match 'could not find the final') "The gate must fail closed when the summary is missing."

$ciText = Get-Content -Raw -LiteralPath $ciWorkflow
$releaseText = Get-Content -Raw -LiteralPath $releaseWorkflow
$gateInvocation = 'scripts[/\\]Test-ReleaseBuild\.ps1'
Assert-True ($ciText -match $gateInvocation) "CI must invoke the reusable Release build gate."
Assert-True ($releaseText -match $gateInvocation) "The release workflow must invoke the reusable Release build gate."
$ciRestoreIndex = $ciText.IndexOf("- name: Restore", [StringComparison]::Ordinal)
$ciGateIndex = $ciText.IndexOf("scripts/Test-ReleaseBuild.ps1", [StringComparison]::Ordinal)
Assert-True ($ciRestoreIndex -ge 0 -and $ciGateIndex -gt $ciRestoreIndex) "CI must run the strict gate after restore."
$releaseRestoreIndex = $releaseText.IndexOf("- name: Restore from nuget.org", [StringComparison]::Ordinal)
$releaseGateIndex = $releaseText.IndexOf("scripts/Test-ReleaseBuild.ps1", [StringComparison]::Ordinal)
$releaseTestIndex = $releaseText.IndexOf("- name: Test Release", [StringComparison]::Ordinal)
Assert-True ($releaseRestoreIndex -ge 0 -and $releaseGateIndex -gt $releaseRestoreIndex -and $releaseTestIndex -gt $releaseGateIndex) "The release workflow must gate the Release build before tests."

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("hookreplay-release-build-contract-" + [Guid]::NewGuid().ToString("N"))
$solution = Join-Path $temporaryDirectory "WarningGate.sln"
$projectDirectory = Join-Path $temporaryDirectory "SyntheticWarning"
$sourcePath = Join-Path $projectDirectory "Class1.cs"
$utf8Bom = [Text.UTF8Encoding]::new($true)

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    $result = Invoke-Native "dotnet" @("new", "sln", "--format", "sln", "--name", "WarningGate", "--output", $temporaryDirectory)
    Assert-True ($result.ExitCode -eq 0) "The synthetic solution could not be created: $($result.Output)"

    $result = Invoke-Native "dotnet" @(
        "new", "classlib", "--framework", "net8.0", "--no-restore",
        "--name", "SyntheticWarning", "--output", $projectDirectory
    )
    Assert-True ($result.ExitCode -eq 0) "The synthetic project could not be created: $($result.Output)"

    [IO.File]::WriteAllText(
        $sourcePath,
        @"
#warning Synthetic warning for the Release gate contract.

namespace SyntheticWarning;

public static class Class1
{
    public static int Value => 1;
}
"@,
        $utf8Bom)

    $projectPath = Join-Path $projectDirectory "SyntheticWarning.csproj"
    $result = Invoke-Native "dotnet" @("sln", $solution, "add", $projectPath)
    Assert-True ($result.ExitCode -eq 0) "The synthetic project could not be added to the solution: $($result.Output)"

    $result = Invoke-Native "dotnet" @("restore", $solution)
    Assert-True ($result.ExitCode -eq 0) "The synthetic solution could not be restored: $($result.Output)"

    $negative = Invoke-Native "pwsh" @(
        "-NoProfile", "-File", $gateScript, "-SolutionPath", $solution
    )
    Assert-True ($negative.ExitCode -ne 0) "A reintroduced compiler warning must fail the Release gate."
    Assert-True ($negative.Output -match "CS1030") "The negative gate case must expose the synthetic warning diagnostic."

    [IO.File]::WriteAllText(
        $sourcePath,
        @"
namespace SyntheticWarning;

public static class Class1
{
    public static int Value => 1;
}
"@,
        $utf8Bom)

    $positive = Invoke-Native "pwsh" @(
        "-NoProfile", "-File", $gateScript, "-SolutionPath", $solution
    )
    Assert-True ($positive.ExitCode -eq 0) "The clean synthetic solution must pass the Release gate: $($positive.Output)"
    Assert-True ($positive.Output -match "0 Warning\(s\)") "The passing gate must report zero warnings."
    Assert-True ($positive.Output -match "0 Error\(s\)") "The passing gate must report zero errors."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Write-Output "Release build contract passed: the Release solution gate is wired into CI and release, fails on a synthetic warning, and passes after the warning is removed."
