[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag
)

$ErrorActionPreference = "Stop"

if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') {
    throw "Malformed release tag '$Tag'. Expected vX.Y.Z."
}

$Matches.version
