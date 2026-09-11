[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    [string] $ChangelogPath = (Join-Path $PSScriptRoot "..\CHANGELOG.md"),

    [string] $PackageVersionPath = (Join-Path $PSScriptRoot "..\Directory.Build.props"),

    [AllowEmptyString()]
    [string] $ExpectedPackageVersion = "",

    [AllowEmptyString()]
    [string] $ExpectedCommit = "",

    [string] $RepositoryRoot = (Join-Path $PSScriptRoot ".."),

    [string] $PackageId = "KeelMatrix.HookReplay",

    [string[]] $VersionReferencePaths = @("README.md")
)

$ErrorActionPreference = "Stop"

function Fail([string] $Message) {
    throw "Changelog contract failed: $Message"
}

function Resolve-ExistingPath([string] $Path, [string] $Description) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        Fail "$Description path must not be empty."
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "$Description '$Path' does not exist."
    }

    $resolved = Resolve-Path -LiteralPath $Path
    return [IO.Path]::GetFullPath($resolved.Path)
}

function Get-RepositoryRelativePath([string] $Path) {
    $relative = [IO.Path]::GetRelativePath($repositoryRoot, $Path).Replace([IO.Path]::DirectorySeparatorChar, "/")
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq ".." -or $relative.StartsWith("../", [StringComparison]::Ordinal)) {
        Fail "Path '$Path' must remain inside repository '$repositoryRoot'."
    }

    return $relative
}

function Invoke-Git([string[]] $Arguments) {
    $output = @(& git -C $repositoryRoot @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = ($output -join [Environment]::NewLine).Trim()
        if ([string]::IsNullOrWhiteSpace($details)) {
            $details = "no additional output"
        }
        Fail "git $($Arguments -join ' ') failed with exit code $exitCode ($details)."
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Get-FileText([string] $Path, [string] $Description) {
    $fullPath = Resolve-ExistingPath $Path $Description
    if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
        return [IO.File]::ReadAllText($fullPath)
    }

    $relativePath = Get-RepositoryRelativePath $fullPath
    $trackedPath = Invoke-Git @("ls-tree", "-r", "--name-only", $ExpectedCommit, "--", $relativePath)
    if ($trackedPath -cne $relativePath) {
        Fail "$Description '$relativePath' is not present at commit $ExpectedCommit."
    }

    return Invoke-Git @("show", "${ExpectedCommit}:$relativePath")
}

function Get-CanonicalVersion([string] $Value, [string] $Description) {
    $version = $Value.Trim()
    $component = '(?:0|[1-9]\d{0,3}|[1-5]\d{4}|6[0-4]\d{3}|65[0-4]\d{2}|655[0-2]\d|6553[0-4])'
    if ($version -notmatch "^$component\.$component\.$component$") {
        Fail "$Description '$version' is not a canonical X.Y.Z release version."
    }

    return $version
}

function Get-DeclaredPackageVersion([string] $Text) {
    $packageVersionMatch = [regex]::Match($Text, '(?is)<PackageVersion\b[^>]*>(?<value>[^<]+)</PackageVersion>')
    $versionMatch = [regex]::Match($Text, '(?is)<Version\b[^>]*>(?<value>[^<]+)</Version>')
    if (-not $packageVersionMatch.Success -and -not $versionMatch.Success) {
        Fail "repository package metadata does not declare a Version or PackageVersion."
    }

    $candidate = if ($packageVersionMatch.Success) {
        $packageVersionMatch.Groups["value"].Value.Trim()
    }
    else {
        $versionMatch.Groups["value"].Value.Trim()
    }

    if ($candidate -match '^\$\((?<property>[A-Za-z][A-Za-z0-9_.-]*)\)$') {
        $propertyName = $Matches.property
        $propertyPattern = "(?is)<$([regex]::Escape($propertyName))\b[^>]*>(?<value>[^<]+)</$([regex]::Escape($propertyName))>"
        $propertyMatch = [regex]::Match($Text, $propertyPattern)
        if (-not $propertyMatch.Success) {
            Fail "repository package metadata references '$candidate' but does not declare '$propertyName'."
        }
        $candidate = $propertyMatch.Groups["value"].Value.Trim()
    }

    return Get-CanonicalVersion $candidate "Repository-declared package version"
}

function Get-ReleaseVersion([string] $ReleaseTag) {
    $versionScript = Join-Path $PSScriptRoot "GetReleaseVersion.ps1"
    $output = @(& pwsh -NoProfile -File $versionScript -Tag $ReleaseTag 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Fail "release tag '$ReleaseTag' is invalid."
    }

    return Get-CanonicalVersion (($output -join [Environment]::NewLine).Trim()) "Release tag version"
}

function Assert-ExactCommit([string] $Commit) {
    if ($Commit -notmatch '^[0-9a-fA-F]{40}$') {
        Fail "expected commit '$Commit' must be a full 40-character commit SHA."
    }

    $head = Invoke-Git @("rev-parse", "--verify", "HEAD^{commit}")
    if ($head -cne $Commit) {
        Fail "checked-out commit '$head' does not match expected release commit '$Commit'."
    }
}

function Assert-VersionReferences([string] $Text, [string] $Path, [string] $ReleaseVersion) {
    $escapedPackageId = [regex]::Escape($PackageId)
    $patterns = @(
        "(?im)dotnet\s+add\s+package\s+$escapedPackageId[^\r\n]*?--version\s+(?<version>[^\s`]+)",
        ('(?im)PackageReference\s+Include\s*=\s*["'']' + $escapedPackageId + '["''][^\r\n]*?\bVersion\s*=\s*["''](?<version>[^"'']+)["'']')
    )

    foreach ($pattern in $patterns) {
        foreach ($match in [regex]::Matches($Text, $pattern)) {
            $referencedVersion = $match.Groups["version"].Value
            if ($referencedVersion -notmatch '^\$\(') {
                $canonicalReferencedVersion = Get-CanonicalVersion $referencedVersion "Version reference in '$Path'"
                if ($canonicalReferencedVersion -cne $ReleaseVersion) {
                    Fail "version reference in '$Path' uses '$canonicalReferencedVersion', expected '$ReleaseVersion'."
                }
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    Fail "Repository root '$RepositoryRoot' does not exist."
}
$repositoryRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepositoryRoot).Path)
$changelogPath = if ([IO.Path]::IsPathRooted($ChangelogPath)) {
    [IO.Path]::GetFullPath($ChangelogPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ChangelogPath))
}
$packageVersionPath = if ([IO.Path]::IsPathRooted($PackageVersionPath)) {
    [IO.Path]::GetFullPath($PackageVersionPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackageVersionPath))
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = $ExpectedCommit.Trim()
    Assert-ExactCommit $ExpectedCommit
}

$releaseVersion = Get-ReleaseVersion $Tag
$changelogText = Get-FileText $changelogPath "Changelog"
$packageVersionText = Get-FileText $packageVersionPath "Package version metadata"
$declaredPackageVersion = Get-DeclaredPackageVersion $packageVersionText

if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageVersion)) {
    $expectedPackageVersion = Get-CanonicalVersion $ExpectedPackageVersion "Expected package version"
    if ($expectedPackageVersion -cne $releaseVersion) {
        Fail "expected package version '$expectedPackageVersion' does not match release tag version '$releaseVersion'."
    }
}

if ($declaredPackageVersion -cne $releaseVersion) {
    Fail "repository-declared package version '$declaredPackageVersion' does not match release tag version '$releaseVersion'."
}

$lines = $changelogText -split "`r?`n"
$headingPattern = '^(?<indent>\s*)(?<marks>#{1,6})\s+(?<title>.+?)\s*$'
$targetHeadings = @()
for ($index = 0; $index -lt $lines.Count; $index++) {
    $headingMatch = [regex]::Match($lines[$index], $headingPattern)
    if (-not $headingMatch.Success) {
        continue
    }

    $title = $headingMatch.Groups["title"].Value.Trim()
    $versionMatch = [regex]::Match($title, '^\[(?<version>[^\]]+)\](?:\s*-\s*(?<suffix>.*))?$')
    if (-not $versionMatch.Success) {
        $versionMatch = [regex]::Match($title, '^(?<version>\d+\.\d+\.\d+)(?:\s*-\s*(?<suffix>.*))?$')
    }
    if ($versionMatch.Success -and $versionMatch.Groups["version"].Value -ceq $releaseVersion) {
        $targetHeadings += [pscustomobject]@{
            Index = $index
            Level = $headingMatch.Groups["marks"].Value.Length
            Title = $title
            Suffix = $versionMatch.Groups["suffix"].Value.Trim()
        }
    }
}

if ($targetHeadings.Count -eq 0) {
    Fail "release version '$releaseVersion' is absent from the changelog as a release heading."
}
if ($targetHeadings.Count -ne 1) {
    Fail "release version '$releaseVersion' must have exactly one changelog release heading."
}

$targetHeading = $targetHeadings[0]
if ($targetHeading.Title -match '(?i)\b(planned|unreleased|tbd|not\s+yet\s+published|not\s+published|pre[-\s]?release|to\s+be\s+determined|coming\s+soon)\b') {
    Fail "release heading '$($targetHeading.Title)' still uses pre-release wording."
}

for ($index = $targetHeading.Index - 1; $index -ge 0; $index--) {
    $ancestorMatch = [regex]::Match($lines[$index], $headingPattern)
    if (-not $ancestorMatch.Success) {
        continue
    }

    $ancestorLevel = $ancestorMatch.Groups["marks"].Value.Length
    if ($ancestorLevel -lt $targetHeading.Level) {
        $ancestorTitle = $ancestorMatch.Groups["title"].Value.Trim()
        if ($ancestorTitle -match '(?i)\bunreleased\b') {
            Fail "release version '$releaseVersion' is nested inside the Unreleased section."
        }
        break
    }
}

$dateMatch = [regex]::Match($targetHeading.Suffix, '^(?<date>\d{4}-\d{2}-\d{2})(?:\s|$)')
if (-not $dateMatch.Success) {
    Fail "release heading '$($targetHeading.Title)' must contain a release date in yyyy-MM-dd form."
}

[datetime] $releaseDate = [datetime]::MinValue
if (-not [datetime]::TryParseExact(
        $dateMatch.Groups["date"].Value,
        "yyyy-MM-dd",
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref] $releaseDate)) {
    Fail "release date '$($dateMatch.Groups["date"].Value)' is invalid."
}
if ($releaseDate.Date -gt [datetime]::UtcNow.Date) {
    Fail "release date '$($dateMatch.Groups["date"].Value)' is later than the current UTC date."
}

foreach ($referencePath in $VersionReferencePaths) {
    $referenceFullPath = if ([IO.Path]::IsPathRooted($referencePath)) {
        [IO.Path]::GetFullPath($referencePath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot $referencePath))
    }
    $referenceText = Get-FileText $referenceFullPath "Version reference"
    Assert-VersionReferences $referenceText $referencePath $releaseVersion
}

$commitDescription = if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) { "working tree" } else { $ExpectedCommit }
Write-Output "Changelog contract passed for $Tag ($releaseVersion) at commit $commitDescription."
