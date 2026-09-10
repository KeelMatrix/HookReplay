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

function Assert-JsonObject($Object, [string] $Path) {
    if ($null -eq $Object -or
        $Object -is [System.Array] -or
        $Object -is [string] -or
        $Object -is [System.ValueType]) {
        Fail "the audit report is structurally invalid: '$Path' must be a JSON object."
    }
}

function Get-RequiredProperty($Object, [string] $Name, [string] $Path) {
    Assert-JsonObject $Object $Path
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        Fail "the audit report is structurally incomplete: '$Path.$Name' is required."
    }

    return $property.Value
}

function Get-RequiredString($Object, [string] $Name, [string] $Path) {
    $value = Get-RequiredProperty $Object $Name $Path
    if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
        Fail "the audit report is structurally invalid: '$Path.$Name' must be a non-empty string."
    }

    return $value
}

function Get-RequiredArray($Object, [string] $Name, [string] $Path) {
    Assert-JsonObject $Object $Path
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        Fail "the audit report is structurally incomplete: '$Path.$Name' is required."
    }

    $value = $property.Value
    if ($value -isnot [System.Array]) {
        Fail "the audit report is structurally invalid: '$Path.$Name' must be an array."
    }

    return $value
}

function Get-OptionalArray($Object, [string] $Name, [string] $Path) {
    Assert-JsonObject $Object $Path
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return @()
    }
    if ($null -eq $property.Value -or $property.Value -isnot [System.Array]) {
        Fail "the audit report is structurally invalid: '$Path.$Name' must be an array when present."
    }

    return $property.Value
}

function Get-RequiredAdvisoryUrls($Object, [string] $Path) {
    $value = Get-RequiredProperty $Object "advisoryurl" $Path
    if ($value -is [string]) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            Fail "the audit report is structurally invalid: '$Path.advisoryurl' must not be empty."
        }
        return @($value)
    }

    if ($value -isnot [System.Array] -or $value.Count -eq 0) {
        Fail "the audit report is structurally invalid: '$Path.advisoryurl' must be a non-empty string or array of strings."
    }

    foreach ($advisoryUrl in $value) {
        if ($advisoryUrl -isnot [string] -or [string]::IsNullOrWhiteSpace($advisoryUrl)) {
            Fail "the audit report is structurally invalid: '$Path.advisoryurl' must contain only non-empty strings."
        }
    }

    return @($value)
}

function Assert-AuditReportShape($Report) {
    $rootPath = "report"
    Assert-JsonObject $Report $rootPath

    $version = Get-RequiredProperty $Report "version" $rootPath
    if ($version -is [string] -or
        $version -is [bool] -or
        $version -isnot [System.ValueType] -or
        [decimal]$version -ne 1) {
        Fail "the audit report uses an unsupported or invalid machine-readable format version."
    }

    $projects = @(Get-RequiredArray $Report "projects" $rootPath)
    if ($projects.Count -eq 0) {
        Fail "the audit report is structurally incomplete: '$rootPath.projects' must contain at least one project."
    }
    # The SDK omits problems on a completed, clean report. If present, it must
    # still be a complete array so an error cannot be mistaken for a clean run.
    $problems = @(Get-OptionalArray $Report "problems" $rootPath)

    for ($problemIndex = 0; $problemIndex -lt $problems.Count; $problemIndex++) {
        $problemPath = "$rootPath.problems[$problemIndex]"
        Assert-JsonObject $problems[$problemIndex] $problemPath
        Get-RequiredString $problems[$problemIndex] "text" $problemPath | Out-Null
        Get-RequiredString $problems[$problemIndex] "level" $problemPath | Out-Null
    }

    for ($projectIndex = 0; $projectIndex -lt $projects.Count; $projectIndex++) {
        $projectPath = "$rootPath.projects[$projectIndex]"
        $project = $projects[$projectIndex]
        Assert-JsonObject $project $projectPath
        Get-RequiredString $project "path" $projectPath | Out-Null
        # The SDK emits only project paths when --vulnerable finds no affected
        # packages. A frameworks array, when emitted, must be complete.
        $frameworks = @(Get-OptionalArray $project "frameworks" $projectPath)

        for ($frameworkIndex = 0; $frameworkIndex -lt $frameworks.Count; $frameworkIndex++) {
            $frameworkPath = "$projectPath.frameworks[$frameworkIndex]"
            $framework = $frameworks[$frameworkIndex]
            Assert-JsonObject $framework $frameworkPath
            Get-RequiredString $framework "framework" $frameworkPath | Out-Null

            foreach ($packageGroupName in @("topLevelPackages", "transitivePackages")) {
                $packages = @(Get-RequiredArray $framework $packageGroupName $frameworkPath)
                for ($packageIndex = 0; $packageIndex -lt $packages.Count; $packageIndex++) {
                    $packagePath = "$frameworkPath.$packageGroupName[$packageIndex]"
                    $package = $packages[$packageIndex]
                    Assert-JsonObject $package $packagePath
                    Get-RequiredString $package "id" $packagePath | Out-Null
                    Get-RequiredString $package "resolvedVersion" $packagePath | Out-Null
                    $vulnerabilities = @(Get-RequiredArray $package "vulnerabilities" $packagePath)
                    for ($vulnerabilityIndex = 0; $vulnerabilityIndex -lt $vulnerabilities.Count; $vulnerabilityIndex++) {
                        $vulnerabilityPath = "$packagePath.vulnerabilities[$vulnerabilityIndex]"
                        $vulnerability = $vulnerabilities[$vulnerabilityIndex]
                        Assert-JsonObject $vulnerability $vulnerabilityPath
                        Get-RequiredString $vulnerability "severity" $vulnerabilityPath | Out-Null
                        Get-RequiredAdvisoryUrls $vulnerability $vulnerabilityPath | Out-Null
                    }
                }
            }
        }
    }
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

Assert-AuditReportShape $report

$problems = @(Get-OptionalArray $report "problems" "report")
if ($problems.Count -gt 0) {
    $problemText = ($problems | ForEach-Object {
        $level = Get-StringValue $_ "level"
        $text = Get-StringValue $_ "text"
        if ([string]::IsNullOrWhiteSpace($level)) { $text } else { "$level`: $text" }
    }) -join "; "
    Fail "the audit report contains completion errors: $problemText"
}

$exceptionDocument = Read-JsonFile $ExceptionFile "Vulnerability exception file"
$exceptions = @(Get-RequiredArray $exceptionDocument "exceptions" "exception document")
$exceptionKeys = @{}
foreach ($exception in $exceptions) {
    Assert-JsonObject $exception "exception"
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
foreach ($project in @(Get-RequiredArray $report "projects" "report")) {
    foreach ($framework in @(Get-OptionalArray $project "frameworks" "report project")) {
        $frameworkName = Get-StringValue $framework "framework"
        foreach ($packageGroupName in @("topLevelPackages", "transitivePackages")) {
            foreach ($package in @(Get-RequiredArray $framework $packageGroupName "report framework")) {
                $packageId = Get-StringValue $package "id"
                $resolvedVersion = Get-StringValue $package "resolvedVersion"
                foreach ($vulnerability in @(Get-RequiredArray $package "vulnerabilities" "report package")) {
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
