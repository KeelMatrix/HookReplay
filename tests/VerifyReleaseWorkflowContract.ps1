param(
    [string] $WorkflowPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($WorkflowPath)) {
    $WorkflowPath = Join-Path $repositoryRoot '.github\workflows\release.yml'
}

function Assert-True {
    param([bool] $Condition, [string] $Message)

    if (-not $Condition) {
        throw "Release workflow contract failed: $Message"
    }
}

function Get-ActiveWorkflowText {
    param([string] $Text)

    return (($Text -split "`r?`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n")
}

function Get-JobText {
    param(
        [string] $Text,
        [string] $JobName
    )

    $escapedName = [regex]::Escape($JobName)
    $match = [regex]::Match(
        $Text,
        "(?ms)^  ${escapedName}:\s*(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)")
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups['body'].Value
}

function Assert-WorkflowContract {
    param([string] $Text)

    $activeText = Get-ActiveWorkflowText $Text
    $validationJob = Get-JobText $activeText 'validate-release'
    $publishJob = Get-JobText $activeText 'publish'
    Assert-True ($null -ne $validationJob) 'The release workflow must have a validation job.'
    Assert-True ($null -ne $publishJob) 'The release workflow must have a publication job.'
    Assert-True ($publishJob -match '(?m)^\s*needs:\s*validate-release\s*$') 'Publication must depend on successful validation.'
    Assert-True ($validationJob -notmatch '(?m)^\s+id-token:\s*write\s*$') 'The validation job must not request OIDC identity tokens.'
    Assert-True ($publishJob -match '(?m)^\s+id-token:\s*write\s*$') 'Only the publication job must request OIDC identity tokens.'
    Assert-True ([regex]::Matches($activeText, '(?m)^\s+id-token:\s*write\s*$').Count -eq 1) 'Exactly one job may request OIDC identity tokens.'
    $validationTimeout = Get-JobTimeoutMinutes $validationJob 'validate-release'
    $publishTimeout = Get-JobTimeoutMinutes $publishJob 'publish'
    Assert-True ($validationTimeout -ge 5 -and $validationTimeout -le 60) 'Validation must have a deliberate timeout from 5 through 60 minutes.'
    Assert-True ($publishTimeout -ge 5 -and $publishTimeout -le 30) 'Publication must have a deliberate timeout from 5 through 30 minutes.'
    Assert-True ($publishJob -notmatch 'actions/checkout@') 'The publication job must not check out the repository.'
    Assert-True ($publishJob -notmatch '(?i)(?:dotnet\s+(?:restore|build|test|pack)|scripts[/\\])') 'The publication job must not execute product or build scripts.'

    Assert-True ($activeText -match 'NuGet/login@v1') 'Trusted Publishing login must use the approved action.'
    Assert-True ($activeText -match '(?m)^\s+user:\s*dmitriyzen\s*$') 'Trusted Publishing must use the approved NuGet.org username.'
    Assert-True ($activeText -match 'KEELMATRIX_NO_TELEMETRY:\s*["'']1["'']') 'Release telemetry suppression must remain enabled.'
    Assert-True ($activeText -match 'https://api\.nuget\.org/v3/index\.json') 'Publication must use the controlled NuGet.org V3 source.'
    Assert-True ($activeText -match '\$\{\{\s*steps\.nuget-login\.outputs\.NUGET_API_KEY\s*\}\}') 'Publication must use the short-lived login output.'
    Assert-True ($activeText -notmatch '(?i)secrets\.NUGET_API_KEY|skip-duplicate') 'Publication must fail closed and must not use a long-lived secret or duplicate-skipping behavior.'
    Assert-True ($validationJob -match 'actions/upload-artifact@v7') 'The validation job must upload the validated publication artifacts.'
    Assert-True ($validationJob -match 'KeelMatrix\.HookReplay\.\$\{\{\s*steps\.release-version\.outputs\.version\s*\}\}\.nupkg') 'Validation must upload the exact primary package.'
    Assert-True ($validationJob -match 'KeelMatrix\.HookReplay\.\$\{\{\s*steps\.release-version\.outputs\.version\s*\}\}\.snupkg') 'Validation must upload the exact symbol package.'
    Assert-True ($publishJob -match 'actions/download-artifact@v7') 'The publication job must download validated artifacts.'
    Assert-True ($publishJob -match '(?m)^\s+name:\s*nuget-release-packages\s*$') 'Publication must download the named validated artifact bundle.'
    Assert-True ($publishJob -match '\$\{\{\s*needs\.validate-release\.outputs\.version\s*\}\}') 'Publication must use the version emitted by validation.'
    Assert-True ($publishJob -match 'KeelMatrix\.HookReplay\.\$version\.nupkg') 'Publication must verify and publish the exact versioned primary package.'
    Assert-True ($publishJob -match 'KeelMatrix\.HookReplay\.\$version\.snupkg') 'Publication must verify the exact versioned symbol package.'

    $steps = @($publishJob -split '(?m)(?=^\s{6}- name:\s*)')
    $publishSteps = @($steps | Where-Object { $_ -match '(?m)^\s*dotnet\s+nuget\s+push\b' })
    Assert-True ($publishSteps.Count -eq 1) 'Exactly one package push command must be present, so symbols have one deterministic publication path.'

    $publishStep = $publishSteps[0]
    Assert-True ($publishStep -match 'KeelMatrix\.HookReplay\.\$env:RELEASE_VERSION\.nupkg') 'The single push must target the exact validated primary package.'
    Assert-True ($publishStep -notmatch '(?i)\.snupkg\b') 'The selected strategy must not add a second explicit symbol push.'
    Assert-True ($publishStep -notmatch '(?i)--no-symbols\b') 'The selected strategy relies on the adjacent validated symbol package.'

    Assert-True ($validationJob -match '- name: Inspect package artifacts before publication') 'Validation must fully inspect the exact packages before upload.'
}

function Get-JobTimeoutMinutes {
    param(
        [string] $JobText,
        [string] $JobName
    )

    $match = [regex]::Match($JobText, '(?m)^[ \t]+timeout-minutes:[ \t]*(?<value>\d+)[ \t]*$')
    if (-not $match.Success) {
        throw "Release workflow contract failed: Job '$JobName' must declare timeout-minutes."
    }

    return [int] $match.Groups['value'].Value
}

Assert-True (Test-Path -LiteralPath $WorkflowPath) "Release workflow '$WorkflowPath' does not exist."
$workflowText = Get-Content -Raw -LiteralPath $WorkflowPath
Assert-WorkflowContract $workflowText

$twoPathWorkflow = $workflowText + @"

      - name: Publish an additional symbol artifact
        run: >
          dotnet nuget push
          ./artifacts/packages/KeelMatrix.HookReplay.synthetic.snupkg
          --source https://api.nuget.org/v3/index.json
"@
$rejected = $false
try {
    Assert-WorkflowContract $twoPathWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'A workflow with a second symbol-publication path must be rejected.'

$validationOidcWorkflow = $workflowText.Replace(
    "      contents: read",
    "      contents: read`n      id-token: write")
$rejected = $false
try {
    Assert-WorkflowContract $validationOidcWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'A validation job with OIDC permission must be rejected.'

$missingDownloadWorkflow = $workflowText.Replace('actions/download-artifact@v7', 'actions/download-artifact@v6')
$rejected = $false
try {
    Assert-WorkflowContract $missingDownloadWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'A publication job without the validated artifact download must be rejected.'

$missingDependencyWorkflow = $workflowText.Replace('needs: validate-release', 'needs: another-job')
$rejected = $false
try {
    Assert-WorkflowContract $missingDependencyWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'A publication job without the validation dependency must be rejected.'

$missingValidationTimeoutWorkflow = $workflowText -replace '(?m)^    timeout-minutes:[ \t]*30[ \t]*\r?\n', ''
$rejected = $false
try {
    Assert-WorkflowContract $missingValidationTimeoutWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'A validation job without a timeout must be rejected.'

$missingPublishTimeoutWorkflow = $workflowText -replace '(?m)^    timeout-minutes:[ \t]*15[ \t]*\r?\n', ''
$rejected = $false
try {
    Assert-WorkflowContract $missingPublishTimeoutWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'A publication job without a timeout must be rejected.'

$unreasonableValidationTimeoutWorkflow = $workflowText -replace '(?m)^    timeout-minutes:[ \t]*30[ \t]*$', '    timeout-minutes: 1'
$rejected = $false
try {
    Assert-WorkflowContract $unreasonableValidationTimeoutWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'An unreasonable validation timeout must be rejected.'

$unreasonablePublishTimeoutWorkflow = $workflowText -replace '(?m)^    timeout-minutes:[ \t]*15[ \t]*$', '    timeout-minutes: 31'
$rejected = $false
try {
    Assert-WorkflowContract $unreasonablePublishTimeoutWorkflow
}
catch {
    $rejected = $true
}
Assert-True $rejected 'An unreasonable publication timeout must be rejected.'

Write-Output 'Release workflow contract passed: validation and publication are separated, OIDC is scoped to publication, artifacts are revalidated, and one exact primary push publishes the adjacent validated symbol package.'
