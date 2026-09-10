[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag
)

$ErrorActionPreference = "Stop"

$componentPattern = '(?:0|[1-9]\d{0,3}|[1-5]\d{4}|6[0-4]\d{3}|65[0-4]\d{2}|655[0-2]\d|6553[0-5])'
if ($Tag -notmatch "^v(?<major>$componentPattern)\.(?<minor>$componentPattern)\.(?<patch>$componentPattern)$") {
    throw "Malformed release tag '$Tag'. Expected vX.Y.Z with no leading zero components and each component between 0 and 65535."
}

"$($Matches.major).$($Matches.minor).$($Matches.patch)"
