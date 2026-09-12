[CmdletBinding()]
param(
    [string] $PackageDirectory = (Join-Path $PSScriptRoot "..\artifacts\packages"),
    [ValidatePattern('^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$')]
    [string] $Version = "0.1.0",
    [string] $ExpectedCommit = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\")).Path
$inspectScript = Join-Path $repositoryRoot "scripts\InspectPackage.ps1"
$packageId = "KeelMatrix.HookReplay"
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("hookreplay-package-inspection-contract-" + [Guid]::NewGuid().ToString("N"))

if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
}

function Fail([string] $Message) {
    throw "Package inspection contract failed: $Message"
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        Fail $Message
    }
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

function Get-ZipEntryText($Archive, [string] $Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) {
        Fail "Expected package entry '$Name' is missing."
    }

    $reader = [IO.StreamReader]::new($entry.Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Replace-ZipEntryText($Archive, [string] $Name, [string] $Text) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) {
        Fail "Expected package entry '$Name' is missing."
    }

    $entry.Delete()
    $replacement = $Archive.CreateEntry($Name)
    $writer = [IO.StreamWriter]::new($replacement.Open(), [Text.UTF8Encoding]::new($false))
    try {
        $writer.Write($Text)
    }
    finally {
        $writer.Dispose()
    }
}

New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
try {
    $packageName = "$packageId.$Version.nupkg"
    $symbolName = "$packageId.$Version.snupkg"
    $packagePath = Join-Path $packageRoot $packageName
    $symbolPath = Join-Path $packageRoot $symbolName
    Assert-True (Test-Path -LiteralPath $packagePath -PathType Leaf) "The expected package '$packageName' is missing."
    Assert-True (Test-Path -LiteralPath $symbolPath -PathType Leaf) "The expected symbol package '$symbolName' is missing."

    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $temporaryDirectory $packageName)
    Copy-Item -LiteralPath $symbolPath -Destination (Join-Path $temporaryDirectory $symbolName)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $mutatedPackagePath = Join-Path $temporaryDirectory $packageName
    $archive = [IO.Compression.ZipFile]::Open($mutatedPackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $nuspecName = "$packageId.nuspec"
        $nuspecText = Get-ZipEntryText $archive $nuspecName
        $mutatedNuspecText = $nuspecText.Replace("<copyright>KeelMatrix</copyright>", "<copyright>keelmatrix</copyright>")
        Assert-True ($mutatedNuspecText -cne $nuspecText) "The package nuspec did not contain the expected correct-case copyright value."
        Replace-ZipEntryText $archive $nuspecName $mutatedNuspecText
    }
    finally {
        $archive.Dispose()
    }

    $inspection = Invoke-Native "pwsh" @(
        "-NoProfile",
        "-File", $inspectScript,
        "-PackageDirectory", $temporaryDirectory,
        "-Version", $Version,
        "-ExpectedCommit", $ExpectedCommit
    ) $repositoryRoot

    Assert-True ($inspection.ExitCode -ne 0) "Inspection accepted a wrong-case copyright value. Output: $($inspection.Output)"
    Assert-True ($inspection.Output -match "Package copyright mismatch") "Inspection did not report the copyright mismatch clearly. Output: $($inspection.Output)"

    Write-Output "Package inspection contract passed: wrong-case copyright metadata fails with a clear diagnostic and non-zero exit."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
