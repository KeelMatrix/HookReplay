[CmdletBinding()]
param(
    [string] $SolutionPath = (Join-Path $PSScriptRoot "..\KeelMatrix.HookReplay.sln"),
    [string] $NuGetConfig = (Join-Path $PSScriptRoot "..\NuGet.config"),
    [string] $ExceptionFile = (Join-Path $PSScriptRoot "..\docs\dependency-vulnerability-exceptions.json"),
    [string] $ReportPath = ""
)

$ErrorActionPreference = "Stop"

function Fail([string] $Message) {
    throw "Dependency vulnerability audit failed: $Message"
}

function Get-PropertyValue($Object, [string] $Name) {
    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-StringValue($Object, [string] $Name) {
    $value = Get-PropertyValue $Object $Name
    if ($null -eq $value) {
        return ""
    }

    return [string]$value
}

function Read-JsonFile([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "$Description '$Path' does not exist."
    }

    try {
        return (Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json)
    }
    catch {
        Fail "$Description '$Path' is not valid JSON: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $arguments = @(
        "list",
        $SolutionPath,
        "package",
        "--vulnerable",
        "--include-transitive",
        "--format",
        "json",
        "--configfile",
        $NuGetConfig
    )
    $commandOutput = & dotnet @arguments 2>&1
    $commandExitCode = $LASTEXITCODE
    $reportText = ($commandOutput -join [Environment]::NewLine).Trim()
    if ($commandExitCode -ne 0) {
        Fail "the audit command could not complete (exit code $commandExitCode). Restore, package sources, or the vulnerability service may be unavailable."
    }
    if ([string]::IsNullOrWhiteSpace($reportText)) {
        Fail "the audit command completed without a machine-readable report."
    }
    try {
        $report = $reportText | ConvertFrom-Json
    }
    catch {
        Fail "the audit command returned invalid machine-readable JSON: $($_.Exception.Message)"
    }
}
else {
    $report = Read-JsonFile $ReportPath "Audit report"
}

if ((Get-StringValue $report "version") -ne "1") {
    Fail "the audit report uses an unsupported machine-readable format version."
}

$problems = @((Get-PropertyValue $report "problems")) | Where-Object { $null -ne $_ }
if ($problems.Count -gt 0) {
    $problemText = ($problems | ForEach-Object {
        $level = Get-StringValue $_ "level"
        $text = Get-StringValue $_ "text"
        if ([string]::IsNullOrWhiteSpace($level)) { $text } else { "$level`: $text" }
    }) -join "; "
    Fail "the audit report contains completion errors: $problemText"
}

$exceptionDocument = Read-JsonFile $ExceptionFile "Vulnerability exception file"
$exceptions = @((Get-PropertyValue $exceptionDocument "exceptions")) | Where-Object { $null -ne $_ }
$exceptionKeys = @{}
foreach ($exception in $exceptions) {
    $packageId = Get-StringValue $exception "packageId"
    $advisoryUrl = Get-StringValue $exception "advisoryUrl"
    $scope = Get-StringValue $exception "scope"
    $reason = Get-StringValue $exception "reason"
    $mitigation = Get-StringValue $exception "mitigation"
    if ([string]::IsNullOrWhiteSpace($packageId) -or
        [string]::IsNullOrWhiteSpace($advisoryUrl) -or
        [string]::IsNullOrWhiteSpace($scope) -or
        [string]::IsNullOrWhiteSpace($reason) -or
        [string]::IsNullOrWhiteSpace($mitigation)) {
        Fail "every vulnerability exception must specify packageId, advisoryUrl, scope, reason, and mitigation."
    }
    $exceptionKeys["$packageId|$advisoryUrl"] = $true
}

$findings = @()
foreach ($project in @((Get-PropertyValue $report "projects"))) {
    foreach ($framework in @((Get-PropertyValue $project "frameworks"))) {
        $frameworkName = Get-StringValue $framework "framework"
        foreach ($packageGroupName in @("topLevelPackages", "transitivePackages")) {
            foreach ($package in @((Get-PropertyValue $framework $packageGroupName))) {
                if ($null -eq $package) { continue }
                $packageId = Get-StringValue $package "id"
                $resolvedVersion = Get-StringValue $package "resolvedVersion"
                foreach ($vulnerability in @((Get-PropertyValue $package "vulnerabilities"))) {
                    if ($null -eq $vulnerability) { continue }
                    $advisoryUrls = @((Get-PropertyValue $vulnerability "advisoryurl")) |
                        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
                        ForEach-Object { [string]$_ }
                    $findings += [pscustomobject]@{
                        PackageId = $packageId
                        ResolvedVersion = $resolvedVersion
                        Framework = $frameworkName
                        Group = $packageGroupName
                        Severity = Get-StringValue $vulnerability "severity"
                        AdvisoryUrls = $advisoryUrls
                    }
                }
            }
        }
    }
}

$uncovered = @($findings | Where-Object {
    $finding = $_
    $finding.AdvisoryUrls.Count -eq 0 -or
        @($finding.AdvisoryUrls | Where-Object {
            $exceptionKeys.ContainsKey("$($finding.PackageId)|$_")
        }).Count -eq 0
})

foreach ($finding in $findings) {
    $advisories = if ($finding.AdvisoryUrls.Count -eq 0) { "advisory URL unavailable" } else { $finding.AdvisoryUrls -join ", " }
    Write-Output "Vulnerability: $($finding.PackageId) $($finding.ResolvedVersion) [$($finding.Group), $($finding.Framework), $($finding.Severity)]: $advisories"
}

if ($uncovered.Count -gt 0) {
    Fail "$($uncovered.Count) reported vulnerability finding(s) are not covered by an exact, documented exception."
}

if ($findings.Count -eq 0) {
    Write-Output "Dependency vulnerability audit passed: no direct or transitive vulnerabilities reported."
}
else {
    Write-Output "Dependency vulnerability audit passed with $($findings.Count) documented exception(s)."
}
