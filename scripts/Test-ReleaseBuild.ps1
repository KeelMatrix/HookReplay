[CmdletBinding()]
param(
    [string] $SolutionPath = (Join-Path $PSScriptRoot "..\KeelMatrix.HookReplay.sln"),
    [string] $Version = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $SolutionPath -PathType Leaf)) {
    throw "Release build gate cannot find solution '$SolutionPath'."
}

$resolvedSolutionPath = (Resolve-Path -LiteralPath $SolutionPath).Path
$arguments = @(
    "build",
    $resolvedSolutionPath,
    "--configuration",
    "Release",
    "--no-restore",
    "--disable-build-servers",
    "-m:1",
    "-p:TreatWarningsAsErrors=true",
    "-p:UseSharedCompilation=false"
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $arguments += "-p:Version=$Version"
}

$buildOutput = @(& dotnet @arguments 2>&1)
$buildExitCode = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Output $_ }
$buildText = ($buildOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine

if ($buildExitCode -ne 0) {
    throw "Release solution warnings-as-errors gate failed with exit code $buildExitCode."
}

function Get-SummaryCount([string] $Name) {
    $pattern = "(?m)^\s*(?<count>\d+)\s+$([regex]::Escape($Name))\(s\)\s*$"
    $matches = [regex]::Matches($buildText, $pattern)
    if ($matches.Count -eq 0) {
        throw "Release solution warnings-as-errors gate could not find the final $Name summary; refusing to pass."
    }

    return [int]$matches[$matches.Count - 1].Groups['count'].Value
}

$warningCount = Get-SummaryCount "Warning"
$errorCount = Get-SummaryCount "Error"
if ($warningCount -ne 0 -or $errorCount -ne 0) {
    throw "Release solution warnings-as-errors gate found $warningCount warning(s) and $errorCount error(s); refusing to pass."
}

Write-Output "Release solution warnings-as-errors gate passed: 0 Warning(s), 0 Error(s)."
