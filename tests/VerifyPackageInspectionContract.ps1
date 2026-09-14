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

function Replace-ZipEntryBytes($Archive, [string] $Name, [byte[]] $Bytes) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) {
        Fail "Expected package entry '$Name' is missing."
    }

    $entry.Delete()
    $replacement = $Archive.CreateEntry($Name)
    $stream = $replacement.Open()
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
}

function Copy-PackageArtifacts([string] $Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $Destination $packageName)
    Copy-Item -LiteralPath $symbolPath -Destination (Join-Path $Destination $symbolName)
}

function Invoke-PackageInspection([string] $InspectionDirectory) {
    return Invoke-Native "pwsh" @(
        "-NoProfile",
        "-File", $inspectScript,
        "-PackageDirectory", $InspectionDirectory,
        "-Version", $Version,
        "-ExpectedCommit", $ExpectedCommit
    ) $repositoryRoot
}

New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
try {
    $packageName = "$packageId.$Version.nupkg"
    $symbolName = "$packageId.$Version.snupkg"
    $packagePath = Join-Path $packageRoot $packageName
    $symbolPath = Join-Path $packageRoot $symbolName
    Assert-True (Test-Path -LiteralPath $packagePath -PathType Leaf) "The expected package '$packageName' is missing."
    Assert-True (Test-Path -LiteralPath $symbolPath -PathType Leaf) "The expected symbol package '$symbolName' is missing."

    $projectReadmePath = Join-Path $repositoryRoot "src\KeelMatrix.HookReplay\README.md"
    $rootReadmePath = Join-Path $repositoryRoot "README.md"
    Assert-True (Test-Path -LiteralPath $projectReadmePath -PathType Leaf) "The project-local README is missing."
    Assert-True (Test-Path -LiteralPath $rootReadmePath -PathType Leaf) "The repository-root README is missing."
    $projectReadmeBytes = [IO.File]::ReadAllBytes($projectReadmePath)
    $rootReadmeBytes = [IO.File]::ReadAllBytes($rootReadmePath)
    Assert-True (-not [Linq.Enumerable]::SequenceEqual($projectReadmeBytes, $rootReadmeBytes)) "The project-local and repository-root README files must remain distinct so the negative package-source case is meaningful."

    $copyrightCaseDirectory = Join-Path $temporaryDirectory "copyright-case"
    Copy-PackageArtifacts $copyrightCaseDirectory

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $mutatedPackagePath = Join-Path $copyrightCaseDirectory $packageName
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

    $inspection = Invoke-PackageInspection $copyrightCaseDirectory

    Assert-True ($inspection.ExitCode -ne 0) "Inspection accepted a wrong-case copyright value. Output: $($inspection.Output)"
    Assert-True ($inspection.Output -match "Package copyright mismatch") "Inspection did not report the copyright mismatch clearly. Output: $($inspection.Output)"

    $rootReadmeCaseDirectory = Join-Path $temporaryDirectory "root-readme-case"
    Copy-PackageArtifacts $rootReadmeCaseDirectory
    $rootReadmePackagePath = Join-Path $rootReadmeCaseDirectory $packageName
    $rootReadmeArchive = [IO.Compression.ZipFile]::Open($rootReadmePackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        Replace-ZipEntryBytes $rootReadmeArchive "README.md" $rootReadmeBytes
    }
    finally {
        $rootReadmeArchive.Dispose()
    }

    $rootReadmeInspection = Invoke-PackageInspection $rootReadmeCaseDirectory
    Assert-True ($rootReadmeInspection.ExitCode -ne 0) "Inspection accepted a package README copied from the repository root. Output: $($rootReadmeInspection.Output)"
    Assert-True ($rootReadmeInspection.Output -match "byte-identical to the project-local README") "Inspection did not report the project-local README mismatch clearly. Output: $($rootReadmeInspection.Output)"

    Write-Output "Package inspection contract passed: wrong-case copyright metadata and repository-root README substitutions fail with clear diagnostics and non-zero exits."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
