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

function Assert-WorkflowContract {
    param([string] $Text)

    $activeText = Get-ActiveWorkflowText $Text
    Assert-True ($activeText -match 'NuGet/login@v1') 'Trusted Publishing login must use the approved action.'
    Assert-True ($activeText -match '(?m)^\s+user:\s*dmitriyzen\s*$') 'Trusted Publishing must use the approved NuGet.org username.'
    Assert-True ($activeText -match '(?m)^\s+id-token:\s*write\s*$') 'The release job must request OIDC identity tokens.'
    Assert-True ($activeText -match 'KEELMATRIX_NO_TELEMETRY:\s*["'']1["'']') 'Release telemetry suppression must remain enabled.'
    Assert-True ($activeText -match 'https://api\.nuget\.org/v3/index\.json') 'Publication must use the controlled NuGet.org V3 source.'
    Assert-True ($activeText -match '\$\{\{\s*steps\.nuget-login\.outputs\.NUGET_API_KEY\s*\}\}') 'Publication must use the short-lived login output.'
    Assert-True ($activeText -notmatch '(?i)secrets\.NUGET_API_KEY|skip-duplicate') 'Publication must fail closed and must not use a long-lived secret or duplicate-skipping behavior.'

    $steps = @($activeText -split '(?m)(?=^\s{6}- name:\s*)')
    $publishSteps = @($steps | Where-Object { $_ -match '(?m)^\s*dotnet\s+nuget\s+push\b' })
    Assert-True ($publishSteps.Count -eq 1) 'Exactly one package push command must be present, so symbols have one deterministic publication path.'

    $publishStep = $publishSteps[0]
    Assert-True ($publishStep -match 'KeelMatrix\.HookReplay\.\$\{\{\s*steps\.release-version\.outputs\.version\s*\}\}\.nupkg') 'The single push must target the exact validated primary package.'
    Assert-True ($publishStep -notmatch '(?i)\.snupkg\b') 'The selected strategy must not add a second explicit symbol push.'
    Assert-True ($publishStep -notmatch '(?i)--no-symbols\b') 'The selected strategy relies on the adjacent validated symbol package.'

    $inspectionIndex = $activeText.IndexOf('- name: Inspect package artifacts before publication', [StringComparison]::Ordinal)
    $publishIndex = $activeText.IndexOf('dotnet nuget push', [StringComparison]::Ordinal)
    Assert-True ($inspectionIndex -ge 0 -and $publishIndex -gt $inspectionIndex) 'Exact artifact inspection must precede publication.'
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

Write-Output 'Release workflow contract passed: one exact primary push publishes the adjacent validated symbol package, and the two-path shape is rejected.'
