[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\")).Path
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifestPath = Join-Path $repositoryRoot "keelmatrix.website.json"
$sourceDirectory = Join-Path $repositoryRoot "src"
$expectedRepositoryUrl = "https://github.com/KeelMatrix/HookReplay"
$expectedPackageId = "KeelMatrix.HookReplay"

function Fail([string] $Message) {
    throw "Website package metadata contract failed: $Message"
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        Fail $Message
    }
}

function Assert-PropertyNames($Value, [string[]] $Expected, [string] $Description) {
    $actual = @($Value.Keys | ForEach-Object { [string] $_ } | Sort-Object -CaseSensitive)
    $expected = @($Expected | Sort-Object -CaseSensitive)
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        Fail "$Description must contain exactly: $($expected -join ', ')."
    }
}

function Get-ProjectProperty([xml] $Project, [string] $Name) {
    $node = $Project.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$Name']")
    if ($null -eq $node) {
        return ""
    }

    return $node.InnerText.Trim()
}

Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Root keelmatrix.website.json is missing."
Assert-True (Test-Path -LiteralPath $sourceDirectory -PathType Container) "The source directory is missing."

try {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -AsHashtable
}
catch {
    Fail "The root website manifest is not valid JSON: $($_.Exception.Message)"
}

Assert-PropertyNames $manifest @("schemaVersion", "packages") "The root website manifest"
Assert-True ($manifest.schemaVersion -eq 1) "Only schemaVersion 1 is supported."
Assert-True ($manifest.packages -is [System.Collections.IDictionary]) "The manifest packages value must be an object."

$projectFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.csproj" -File -Recurse)
$packableProjects = @()
foreach ($projectFile in $projectFiles) {
    try {
        $projectXml = [xml](Get-Content -Raw -LiteralPath $projectFile.FullName)
    }
    catch {
        Fail "Package project '$($projectFile.FullName)' is not valid XML: $($_.Exception.Message)"
    }

    $isPackable = Get-ProjectProperty $projectXml "IsPackable"
    if ($isPackable -ine "false") {
        $packableProjects += [pscustomobject]@{
            Path = $projectFile.FullName
            Xml = $projectXml
        }
    }
}

Assert-True ($packableProjects.Count -eq 1) "Expected exactly one packable project under src."
$project = $packableProjects[0].Xml
$packageId = Get-ProjectProperty $project "PackageId"
Assert-True ($packageId -ceq $expectedPackageId) "The shipping project PackageId must be '$expectedPackageId'."
$manifestPackageIds = @($manifest.packages.Keys | ForEach-Object { [string] $_ })
Assert-True ($manifestPackageIds.Count -eq 1 -and $manifestPackageIds[0] -ceq $packageId) "The manifest must classify exactly the package owned by this repository using its exact package ID."

$entry = $manifest.packages[$packageId]
Assert-True ($entry -is [System.Collections.IDictionary]) "The package manifest entry must be an object."
Assert-PropertyNames $entry @("visibility", "role") "The '$packageId' manifest entry"
Assert-True ([string]$entry.visibility -ceq "public-product") "'$packageId' must remain classified as public-product."
Assert-True ([string]$entry.role -ceq "primary") "'$packageId' must remain the primary artifact."

$packageTags = Get-ProjectProperty $project "PackageTags"
$tags = @($packageTags -split '[;\s,]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$visibilitySentinels = @($tags | Where-Object { $_ -cin @("keelmatrix-public-product", "keelmatrix-internal-package") })
$roleSentinels = @($tags | Where-Object { $_ -cin @("keelmatrix-primary", "keelmatrix-component") })
$knownSentinels = @("keelmatrix-public-product", "keelmatrix-internal-package", "keelmatrix-primary", "keelmatrix-component")
$unknownSentinels = @($tags | Where-Object { $_ -imatch '^keelmatrix-' -and $_ -cnotin $knownSentinels })

Assert-True ($visibilitySentinels.Count -eq 1) "PackageTags must contain exactly one lowercase website visibility sentinel."
Assert-True ($roleSentinels.Count -eq 1) "PackageTags must contain exactly one lowercase website role sentinel."
Assert-True ($visibilitySentinels[0] -ceq "keelmatrix-$($entry.visibility)") "The visibility sentinel must match the repository manifest."
Assert-True ($roleSentinels[0] -ceq "keelmatrix-$($entry.role)") "The role sentinel must match the repository manifest."
Assert-True ($unknownSentinels.Count -eq 0) "PackageTags contains an unknown or incorrectly cased KeelMatrix sentinel."

Assert-True ((Get-ProjectProperty $project "Description").Length -gt 0) "The package description must not be empty."
Assert-True ((Get-ProjectProperty $project "PackageProjectUrl") -ceq $expectedRepositoryUrl) "PackageProjectUrl must be the canonical public GitHub repository URL."
Assert-True ((Get-ProjectProperty $project "RepositoryUrl") -ceq $expectedRepositoryUrl) "RepositoryUrl must match the canonical public GitHub repository URL."
Assert-True ((Get-ProjectProperty $project "RepositoryType") -ceq "git") "RepositoryType must be git."
Assert-True ((Get-ProjectProperty $project "PackageType") -cin @("", "Dependency")) "The NuGet package type must remain Dependency."

Write-Output "Website package metadata contract passed: $packageId is public-product/primary, source sentinels match the manifest, and repository metadata is canonical."
