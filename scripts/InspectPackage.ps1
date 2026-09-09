[CmdletBinding()]
param(
    [string] $PackageDirectory = (Join-Path $PSScriptRoot "..\artifacts\packages"),
    [string] $Version = "0.1.0",
    [string] $ExpectedCommit = ""
)

$ErrorActionPreference = "Stop"
$packageId = "KeelMatrix.HookReplay"
$expectedNupkg = "$packageId.$Version.nupkg"
$expectedSnupkg = "$packageId.$Version.snupkg"
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path

if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = (& git rev-parse HEAD).Trim()
}

function Fail([string] $Message) {
    throw "Package inspection failed: $Message"
}

function Assert-Equal([object] $Expected, [object] $Actual, [string] $Message) {
    if ($Expected -ne $Actual) {
        Fail "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Get-ZipEntryText($Archive, [string] $Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) {
        Fail "Missing archive entry '$Name'."
    }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-Contains([string[]] $Entries, [string] $Name) {
    if ($Entries -notcontains $Name) {
        Fail "Missing required package entry '$Name'."
    }
}

function Get-EntryBytes($Archive, [string] $Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) {
        Fail "Missing archive entry '$Name'."
    }
    $stream = $entry.Open()
    try {
        $bytes = [byte[]]::new($entry.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { break }
            $offset += $read
        }
        if ($offset -ne $bytes.Length) {
            Fail "Could not read archive entry '$Name'."
        }
        return $bytes
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ExactDependencies($Group, [string[]] $Expected) {
    $actual = @($Group.SelectNodes("n:dependency", $script:Namespace) | ForEach-Object {
        "$($_.id)|$($_.version)|$($_.exclude)"
    })
    $sortedActual = @($actual | Sort-Object)
    $sortedExpected = @($Expected | Sort-Object)
    if (($sortedActual -join "`n") -ne ($sortedExpected -join "`n")) {
        Fail "Dependency group '$($Group.targetFramework)' differs. Expected '$($sortedExpected -join ', ')', actual '$($sortedActual -join ', ')'."
    }
}

$packages = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Extension -eq ".nupkg" })
$symbols = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Extension -eq ".snupkg" })
Assert-Equal 1 $packages.Count "Expected exactly one .nupkg."
Assert-Equal 1 $symbols.Count "Expected exactly one .snupkg."
Assert-Equal $expectedNupkg $packages[0].Name "Unexpected package artifact name."
Assert-Equal $expectedSnupkg $symbols[0].Name "Unexpected symbol artifact name."

$maxArchiveBytes = 5MB
if ($packages[0].Length -gt $maxArchiveBytes -or $symbols[0].Length -gt $maxArchiveBytes) {
    Fail "Package artifacts must each be no larger than 5 MiB."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageArchive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
$symbolArchive = [System.IO.Compression.ZipFile]::OpenRead($symbols[0].FullName)
try {
    $packageEntries = @($packageArchive.Entries | ForEach-Object FullName)
    $symbolEntries = @($symbolArchive.Entries | ForEach-Object FullName)
    foreach ($required in @(
        "README.md",
        "LICENSE",
        "icon.png",
        "$packageId.nuspec",
        "lib/net8.0/$packageId.dll",
        "lib/net8.0/$packageId.xml",
        "lib/netstandard2.0/$packageId.dll",
        "lib/netstandard2.0/$packageId.xml"
    )) {
        Assert-Contains $packageEntries $required
    }

    foreach ($entry in $packageEntries) {
        if ($entry -match "(?i)(^|/)(src|tests|research|internal|\.github|\.git)(/|$)|(^|/)(\.env[^/]*|local\.settings\.json|appsettings\.(development|local)\.json|keelmatrix\.telemetry\.json|.*\.(secret|secrets)\.json|.*\.(pfx|p12|pem|key|crt|cer))$") {
            Fail "Forbidden repository, test, research, local-config, or secret-bearing entry '$entry'."
        }
        if ($entry -notmatch "^(README\.md|LICENSE|icon\.png|$([regex]::Escape($packageId))\.nuspec|_rels/.*|\[Content_Types\]\.xml|package/services/metadata/.*|lib/(net8\.0|netstandard2\.0)/$([regex]::Escape($packageId))\.(dll|xml))$") {
            Fail "Unexpected package entry '$entry'."
        }
    }

    foreach ($entry in $symbolEntries) {
        if ($entry -match "(?i)(^|/)(src|tests|research|internal|\.github|\.git)(/|$)|(^|/)(\.env[^/]*|local\.settings\.json|appsettings\.(development|local)\.json|keelmatrix\.telemetry\.json|.*\.(secret|secrets)\.json|.*\.(pfx|p12|pem|key|crt|cer))$") {
            Fail "Forbidden repository, test, research, local-config, or secret-bearing symbol entry '$entry'."
        }
    }

    $nuspecText = Get-ZipEntryText $packageArchive "$packageId.nuspec"
    $nuspec = [xml]$nuspecText
    $script:Namespace = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $script:Namespace.AddNamespace("n", "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd")
    $metadata = $nuspec.SelectSingleNode("/n:package/n:metadata", $script:Namespace)
    if ($null -eq $metadata) { Fail "The nuspec has no metadata element." }
    Assert-Equal $packageId $metadata.id "Package ID mismatch."
    Assert-Equal $Version $metadata.version "Package version mismatch."
    Assert-Equal "KeelMatrix" $metadata.authors "Package authors mismatch."
    Assert-Equal "expression" $metadata.license.type "Package license metadata type mismatch."
    Assert-Equal "MIT" $metadata.license.InnerText "Package license mismatch."
    Assert-Equal "README.md" $metadata.readme "README metadata mismatch."
    Assert-Equal "icon.png" $metadata.icon "Icon metadata mismatch."
    Assert-Equal "https://github.com/KeelMatrix/HookReplay" $metadata.repository.url "Repository URL mismatch."
    if ([string]::IsNullOrWhiteSpace([string]$metadata.repository.commit)) {
        Fail "Repository commit metadata is missing."
    }
    if ([string]$metadata.repository.commit -ne $ExpectedCommit) {
        Fail "Repository commit metadata does not match the checked-out commit."
    }

    $groups = @($metadata.SelectNodes("n:dependencies/n:group", $script:Namespace))
    Assert-Equal 2 $groups.Count "Expected exactly net8.0 and netstandard2.0 dependency groups."
    $net8 = $groups | Where-Object targetFramework -eq "net8.0"
    $netstandard = $groups | Where-Object targetFramework -eq ".NETStandard2.0"
    if ($null -eq $net8 -or $null -eq $netstandard) { Fail "Expected target framework dependency groups are missing." }
    Assert-ExactDependencies $net8 @(
        "KeelMatrix.Redaction|[0.1.0]|Build,Analyzers",
        "KeelMatrix.Telemetry|[0.1.0]|Build,Analyzers"
    )
    Assert-ExactDependencies $netstandard @(
        "KeelMatrix.Redaction|[0.1.0]|Build,Analyzers",
        "KeelMatrix.Telemetry|[0.1.0]|Build,Analyzers",
        "System.Text.Json|10.0.10|Build,Analyzers"
    )

    $iconBytes = Get-EntryBytes $packageArchive "icon.png"
    if ($iconBytes.Length -lt 24 -or $iconBytes[0] -ne 0x89 -or $iconBytes[1] -ne 0x50 -or $iconBytes[2] -ne 0x4E -or $iconBytes[3] -ne 0x47) {
        Fail "Package icon is not a PNG."
    }
    $width = ($iconBytes[16] * 16777216) + ($iconBytes[17] * 65536) + ($iconBytes[18] * 256) + $iconBytes[19]
    $height = ($iconBytes[20] * 16777216) + ($iconBytes[21] * 65536) + ($iconBytes[22] * 256) + $iconBytes[23]
    Assert-Equal 512 $width "Package icon width mismatch."
    Assert-Equal 512 $height "Package icon height mismatch."

    $textEntries = @($packageEntries | Where-Object { $_ -match "\.(md|xml|nuspec)$" })
    foreach ($entry in $textEntries) {
        $text = Get-ZipEntryText $packageArchive $entry
        if ($text -match "(?i)AKIA[0-9A-Z]{16}|Bearer\s+[A-Za-z0-9._-]{12,}|-----BEGIN (RSA |EC )?PRIVATE KEY-----|ghp_[A-Za-z0-9]{20,}") {
            Fail "Secret-like content found in package entry '$entry'."
        }
    }

    $expectedSymbols = @(
        "_rels/.rels",
        "$packageId.nuspec",
        "lib/net8.0/$packageId.pdb",
        "lib/netstandard2.0/$packageId.pdb",
        "[Content_Types].xml",
        "package/services/metadata/core-properties/nuget.psmdcp"
    )
    foreach ($required in $expectedSymbols) { Assert-Contains $symbolEntries $required }
    if (@($symbolEntries | Where-Object { $_ -notin $expectedSymbols }).Count -ne 0) {
        Fail "Symbol package contains unexpected entries."
    }
    foreach ($pdb in @("lib/net8.0/$packageId.pdb", "lib/netstandard2.0/$packageId.pdb")) {
        $pdbText = [System.Text.Encoding]::UTF8.GetString((Get-EntryBytes $symbolArchive $pdb))
        if (-not $pdbText.Contains("raw.githubusercontent.com/KeelMatrix/HookReplay/$ExpectedCommit/*", [System.StringComparison]::Ordinal)) {
            Fail "SourceLink mapping is missing from '$pdb'."
        }
    }
}
finally {
    $packageArchive.Dispose()
    $symbolArchive.Dispose()
}

Write-Output "Package inspection passed: $expectedNupkg and $expectedSnupkg; commit $ExpectedCommit; net8.0/netstandard2.0 assets; metadata, dependencies, SourceLink, symbols, icon, size, and content hygiene verified."
