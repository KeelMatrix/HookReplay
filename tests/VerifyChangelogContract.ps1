[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\")).Path
)

$ErrorActionPreference = "Stop"
$contractScript = Join-Path $RepositoryRoot "scripts\Test-ChangelogContract.ps1"
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("hookreplay-changelog-contract-" + [Guid]::NewGuid().ToString("N"))

function Fail([string] $Message) {
    throw "Changelog contract tests failed: $Message"
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        Fail $Message
    }
}

function Invoke-Contract {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Tag,
        [string] $ExpectedCommit = ""
    )

    $arguments = @(
        "-NoProfile",
        "-File", $contractScript,
        "-Tag", $Tag,
        "-RepositoryRoot", $Root,
        "-ChangelogPath", (Join-Path $Root "CHANGELOG.md"),
        "-PackageVersionPath", (Join-Path $Root "Directory.Build.props"),
        "-VersionReferencePaths", (Join-Path $Root "README.md"),
        "-ExpectedPackageVersion", "1.2.3"
    )
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
        $arguments += @("-ExpectedCommit", $ExpectedCommit)
    }

    $output = @(& pwsh @arguments 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine).Trim()
    }
}

function Write-Fixture {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $ReleaseHeading,
        [string] $PackageVersion = "1.2.3",
        [string] $InstallVersion = "1.2.3",
        [string] $InstallExample = "",
        [string] $UnreleasedHeading = "## [Unreleased]"
    )

    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $changelog = @"
# Changelog

$UnreleasedHeading

$ReleaseHeading

- Synthetic release notes.
"@
    $props = @"
<Project>
  <PropertyGroup>
    <Version Condition="'`$(Version)'==''">$PackageVersion</Version>
    <PackageVersion Condition="'`$(PackageVersion)'==''">`$(Version)</PackageVersion>
  </PropertyGroup>
</Project>
"@
    $readme = if ([string]::IsNullOrWhiteSpace($InstallExample)) {
        "dotnet add package KeelMatrix.HookReplay --version $InstallVersion`n"
    }
    else {
        $InstallExample
    }
    [IO.File]::WriteAllText((Join-Path $Root "CHANGELOG.md"), $changelog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $Root "Directory.Build.props"), $props, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $Root "README.md"), $readme, [Text.UTF8Encoding]::new($false))
}

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $ArgumentList,
        [Parameter(Mandatory)] [string] $WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = @(& $FilePath @ArgumentList 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output -join [Environment]::NewLine).Trim()
    }
}

New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
try {
    $today = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")

    $plannedRoot = Join-Path $temporaryDirectory "planned"
    Write-Fixture $plannedRoot "## [1.2.3] - Planned (not yet published)"
    $result = Invoke-Contract $plannedRoot "v1.2.3"
    Assert-True ($result.ExitCode -ne 0) "A Planned target version must fail the publication gate. Output: $($result.Output)"

    $nestedRoot = Join-Path $temporaryDirectory "nested-unreleased"
    Write-Fixture $nestedRoot "## [1.2.3] - $today" -UnreleasedHeading "# [Unreleased]"
    $result = Invoke-Contract $nestedRoot "v1.2.3"
    Assert-True ($result.ExitCode -ne 0) "A target nested under Unreleased must fail the publication gate. Output: $($result.Output)"

    $invalidDateRoot = Join-Path $temporaryDirectory "invalid-date"
    Write-Fixture $invalidDateRoot "## [1.2.3] - 2026-99-99"
    $result = Invoke-Contract $invalidDateRoot "v1.2.3"
    Assert-True ($result.ExitCode -ne 0) "An invalid release date must fail the publication gate. Output: $($result.Output)"

    $futureDateRoot = Join-Path $temporaryDirectory "future-date"
    $tomorrow = (Get-Date).ToUniversalTime().Date.AddDays(1).ToString("yyyy-MM-dd")
    Write-Fixture $futureDateRoot "## [1.2.3] - $tomorrow"
    $result = Invoke-Contract $futureDateRoot "v1.2.3"
    Assert-True ($result.ExitCode -ne 0) "A future release date must fail the publication gate. Output: $($result.Output)"

    $finalizedRoot = Join-Path $temporaryDirectory "finalized"
    $consistentInstallExample = @'
dotnet add package KeelMatrix.HookReplay \
  --version 1.2.3
'@
    Write-Fixture $finalizedRoot "## [1.2.3] - $today" -InstallExample $consistentInstallExample
    $result = Invoke-Contract $finalizedRoot "v1.2.3"
    Assert-True ($result.ExitCode -eq 0) "A finalized target with consistent multiline package and install metadata must pass. Output: $($result.Output)"

    $changelogMismatchRoot = Join-Path $temporaryDirectory "changelog-mismatch"
    Write-Fixture $changelogMismatchRoot "## [1.2.4] - $today"
    $result = Invoke-Contract $changelogMismatchRoot "v1.2.3"
    Assert-True ($result.ExitCode -ne 0) "A changelog/tag version mismatch must fail closed. Output: $($result.Output)"

    $packageMismatchRoot = Join-Path $temporaryDirectory "package-mismatch"
    Write-Fixture $packageMismatchRoot "## [1.2.3] - $today" "1.2.4"
    $result = Invoke-Contract $packageMismatchRoot "v1.2.3"
    Assert-True ($result.ExitCode -ne 0) "A package/tag version mismatch must fail closed. Output: $($result.Output)"

    $installMismatchRoot = Join-Path $temporaryDirectory "install-mismatch"
    $mismatchedInstallExample = @'
dotnet add package KeelMatrix.HookReplay `
  --version 1.2.4
'@
    Write-Fixture $installMismatchRoot "## [1.2.3] - $today" -InstallExample $mismatchedInstallExample
    $result = Invoke-Contract $installMismatchRoot "v1.2.3"
    Assert-True ($result.ExitCode -ne 0) "A multiline install-example/version mismatch must fail closed. Output: $($result.Output)"

    $exactCommitRoot = Join-Path $temporaryDirectory "exact-commit"
    Write-Fixture $exactCommitRoot "## [1.2.3] - $today"
    $init = Invoke-Native "git" @("init", "--quiet", "--initial-branch=main") $exactCommitRoot
    Assert-True ($init.ExitCode -eq 0) "Could not initialize the exact-commit fixture. $($init.Output)"
    foreach ($setting in @(@("user.name", "KeelMatrix"), @("user.email", "keelmatrix@gmail.com"))) {
        $configured = Invoke-Native "git" @("config", $setting[0], $setting[1]) $exactCommitRoot
        Assert-True ($configured.ExitCode -eq 0) "Could not configure the exact-commit fixture. $($configured.Output)"
    }
    $added = Invoke-Native "git" @("add", "CHANGELOG.md", "Directory.Build.props", "README.md") $exactCommitRoot
    Assert-True ($added.ExitCode -eq 0) "Could not stage the exact-commit fixture. $($added.Output)"
    $firstCommitResult = Invoke-Native "git" @("commit", "--quiet", "-m", "Synthetic changelog contract fixture") $exactCommitRoot
    Assert-True ($firstCommitResult.ExitCode -eq 0) "Could not create the exact-commit fixture commit. $($firstCommitResult.Output)"
    $firstCommit = (Invoke-Native "git" @("rev-parse", "HEAD") $exactCommitRoot).Output.Trim()

    $result = Invoke-Contract $exactCommitRoot "v1.2.3" $firstCommit
    Assert-True ($result.ExitCode -eq 0) "The exact checked-out commit should pass when all metadata is consistent. Output: $($result.Output)"

    [IO.File]::AppendAllText((Join-Path $exactCommitRoot "README.md"), "# second commit`n", [Text.UTF8Encoding]::new($false))
    $added = Invoke-Native "git" @("add", "README.md") $exactCommitRoot
    Assert-True ($added.ExitCode -eq 0) "Could not stage the second exact-commit fixture revision. $($added.Output)"
    $secondCommitResult = Invoke-Native "git" @("commit", "--quiet", "-m", "Synthetic changelog contract revision") $exactCommitRoot
    Assert-True ($secondCommitResult.ExitCode -eq 0) "Could not create the second exact-commit fixture revision. $($secondCommitResult.Output)"

    $result = Invoke-Contract $exactCommitRoot "v1.2.3" $firstCommit
    Assert-True ($result.ExitCode -ne 0) "A release check must reject a checked-out commit different from the expected release commit. Output: $($result.Output)"

    Write-Output "Changelog contract tests passed: planned and nested targets reject, finalized metadata passes, mismatches fail closed, and the check binds to the exact commit."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
