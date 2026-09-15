$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("hookreplay-history-contract-" + [Guid]::NewGuid().ToString('N'))

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $ArgumentList,
        [Parameter(Mandatory)] [string] $WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $FilePath @ArgumentList 2>&1
    }
    finally {
        Pop-Location
    }

    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine)
    }
}

function Assert-True {
    param([bool] $Condition, [string] $Message)

    if (-not $Condition) {
        throw "History hygiene contract failed: $Message"
    }
}

function Convert-CodePoints {
    param([int[]] $CodePoint)

    return (-join ($CodePoint | ForEach-Object { [char] $_ }))
}

function Get-ActiveWorkflowText {
    param([Parameter(Mandatory)] [string] $Text)

    return (($Text -replace "`r`n", "`n" -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n")
}

function Assert-WorkflowContract {
    param([Parameter(Mandatory)] [string] $Text)

    $activeText = Get-ActiveWorkflowText $Text
    $onMatch = [regex]::Match($activeText, '(?ms)^on:\s*\n(?<body>.*?)(?=^permissions:\s*$)')
    Assert-True $onMatch.Success 'The workflow must declare branch and pull-request triggers.'

    $triggerBody = $onMatch.Groups['body'].Value
    $pushMatch = [regex]::Match($triggerBody, '(?ms)^  push:\s*\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)')
    Assert-True $pushMatch.Success 'The workflow must have a push trigger.'
    $pushBody = $pushMatch.Groups['body'].Value
    Assert-True ($pushBody -match '(?m)^    branches:\s*$') 'Push events must be scoped to branches so tag pushes cannot start this workflow.'
    Assert-True ($pushBody -match '(?m)^      - [''\"]\*\*[''\"]\s*$') 'Push history hygiene must cover every branch.'
    Assert-True ($pushBody -notmatch '(?m)^\s+tags(?:-ignore)?:\s*$') 'Push triggers must not opt back into tag events.'
    Assert-True ($triggerBody -match '(?m)^  pull_request:\s*$') 'Pull requests must remain covered by history hygiene.'

    Assert-True ($activeText -match '(?m)^\s+run:\s+git fetch --all --prune\s*$') 'The ref refresh must omit --tags so an existing local tag cannot be clobbered.'
    Assert-True ($activeText -notmatch '(?m)^\s+run:.*--tags') 'The ref refresh must not use a tag-fetch form that can reject an existing tag.'
    $fetchIndex = $activeText.IndexOf('git fetch --all --prune', [StringComparison]::Ordinal)
    $historyCheckIndex = $activeText.IndexOf('sh .githooks/check-history', [StringComparison]::Ordinal)
    Assert-True ($fetchIndex -ge 0 -and $historyCheckIndex -gt $fetchIndex) 'The all-reachable-commits history check must remain after the ref refresh.'
}

try {
    $workflowPath = Join-Path $repositoryRoot '.github\workflows\history-hygiene.yml'
    Assert-True (Test-Path -LiteralPath $workflowPath -PathType Leaf) 'The history hygiene workflow must exist.'
    $workflowText = Get-ActiveWorkflowText (Get-Content -Raw -LiteralPath $workflowPath)
    Assert-WorkflowContract $workflowText

    $preFixWorkflowText = $workflowText.Replace(
        "  push:`n    branches:`n      - '**'`n",
        "  push:`n")
    $preFixWorkflowText = $preFixWorkflowText.Replace('git fetch --all --prune', 'git fetch --all --tags --prune')
    Assert-True ($preFixWorkflowText -ne $workflowText) 'The negative contract fixture must represent the pre-fix workflow shape.'
    $preFixRejected = $false
    try {
        Assert-WorkflowContract $preFixWorkflowText
    }
    catch {
        $preFixRejected = $true
    }
    Assert-True $preFixRejected 'The workflow contract must reject the pre-fix tag trigger and clobbering fetch shape.'

    $shellCommand = Get-Command 'sh' -ErrorAction SilentlyContinue
    if ($null -eq $shellCommand -and $IsWindows) {
        $gitCommand = Get-Command 'git' -ErrorAction Stop
        $gitCommandDirectory = Split-Path -Parent $gitCommand.Source
        $gitRoot = Split-Path -Parent $gitCommandDirectory
        $env:Path = (Join-Path $gitRoot 'usr\bin') + [IO.Path]::PathSeparator + (Join-Path $gitRoot 'bin') + [IO.Path]::PathSeparator + $env:Path
        $gitShellPath = Join-Path $gitRoot 'usr\bin\sh.exe'
        if (-not (Test-Path -LiteralPath $gitShellPath)) {
            $gitShellPath = Join-Path $gitRoot 'bin\sh.exe'
        }
        if (Test-Path -LiteralPath $gitShellPath) {
            $shellCommand = Get-Command $gitShellPath
        }
    }
    Assert-True ($null -ne $shellCommand) 'A POSIX shell is required to exercise the versioned shell checks.'
    $shellPath = $shellCommand.Source

    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null

    $remoteRepository = Join-Path $temporaryDirectory 'remote.git'
    $sourceRepository = Join-Path $temporaryDirectory 'tag-source'
    $checkoutRepository = Join-Path $temporaryDirectory 'tag-checkout'
    $bareInit = Invoke-Native -FilePath 'git' -ArgumentList @('init', '--bare', '--quiet', $remoteRepository) -WorkingDirectory $temporaryDirectory
    Assert-True ($bareInit.ExitCode -eq 0) "Could not initialize the synthetic bare remote. $($bareInit.Output)"
    New-Item -ItemType Directory -Path $sourceRepository -Force | Out-Null
    $sourceInit = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $sourceRepository, 'init', '--quiet', '--initial-branch=main') -WorkingDirectory $temporaryDirectory
    Assert-True ($sourceInit.ExitCode -eq 0) "Could not initialize the synthetic tag source. $($sourceInit.Output)"
    foreach ($setting in @(@('user.name', 'KeelMatrix'), @('user.email', 'keelmatrix@gmail.com'))) {
        $configured = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $sourceRepository, 'config', $setting[0], $setting[1]) -WorkingDirectory $temporaryDirectory
        Assert-True ($configured.ExitCode -eq 0) "Could not configure synthetic tag source identity. $($configured.Output)"
    }
    $tagMessagePath = Join-Path $temporaryDirectory 'tag-message.txt'
    Set-Content -LiteralPath $tagMessagePath -Value 'Synthetic annotated release tag' -NoNewline -Encoding utf8NoBOM
    $tagCommitResult = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $sourceRepository, 'commit-tree', '4b825dc642cb6eb9a060e54bf8d69288fbee4904', '-F', $tagMessagePath) -WorkingDirectory $temporaryDirectory
    Assert-True ($tagCommitResult.ExitCode -eq 0) "Could not create the synthetic tag target commit. $($tagCommitResult.Output)"
    $tagCommit = $tagCommitResult.Output.Trim()
    $updateTagBranch = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $sourceRepository, 'update-ref', 'refs/heads/main', $tagCommit) -WorkingDirectory $temporaryDirectory
    Assert-True ($updateTagBranch.ExitCode -eq 0) "Could not make the synthetic tag target reachable. $($updateTagBranch.Output)"
    $createAnnotatedTag = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $sourceRepository, 'tag', '-a', 'v0.1.0', '-m', 'Synthetic release tag', $tagCommit) -WorkingDirectory $temporaryDirectory
    Assert-True ($createAnnotatedTag.ExitCode -eq 0) "Could not create the synthetic annotated tag. $($createAnnotatedTag.Output)"
    $pushTagFixture = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $sourceRepository, 'push', '--quiet', $remoteRepository, 'refs/heads/main', 'refs/tags/v0.1.0') -WorkingDirectory $temporaryDirectory
    Assert-True ($pushTagFixture.ExitCode -eq 0) "Could not publish the synthetic remote tag fixture. $($pushTagFixture.Output)"
    $cloneFixture = Invoke-Native -FilePath 'git' -ArgumentList @('clone', '--quiet', $remoteRepository, $checkoutRepository) -WorkingDirectory $temporaryDirectory
    Assert-True ($cloneFixture.ExitCode -eq 0) "Could not create the synthetic tag-push checkout. $($cloneFixture.Output)"

    $remoteTagType = Invoke-Native -FilePath 'git' -ArgumentList @('--git-dir', $remoteRepository, 'cat-file', '-t', 'refs/tags/v0.1.0') -WorkingDirectory $temporaryDirectory
    Assert-True ($remoteTagType.ExitCode -eq 0 -and $remoteTagType.Output.Trim() -eq 'tag') "The synthetic remote tag must be annotated. $($remoteTagType.Output)"
    $localTagRef = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $checkoutRepository, 'update-ref', 'refs/tags/v0.1.0', $tagCommit) -WorkingDirectory $temporaryDirectory
    Assert-True ($localTagRef.ExitCode -eq 0) "Could not simulate the checkout-local tag ref. $($localTagRef.Output)"
    $localTagType = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $checkoutRepository, 'cat-file', '-t', 'refs/tags/v0.1.0') -WorkingDirectory $temporaryDirectory
    Assert-True ($localTagType.ExitCode -eq 0 -and $localTagType.Output.Trim() -eq 'commit') "The synthetic local tag ref must be lightweight. $($localTagType.Output)"
    $legacyFetch = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $checkoutRepository, 'fetch', '--all', '--tags', '--prune') -WorkingDirectory $temporaryDirectory
    Assert-True ($legacyFetch.ExitCode -ne 0) "The pre-fix tag fetch unexpectedly succeeded. $($legacyFetch.Output)"
    Assert-True ($legacyFetch.Output -match 'would clobber existing tag') "The pre-fix tag fetch failed for an unexpected reason. $($legacyFetch.Output)"
    $safeFetch = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $checkoutRepository, 'fetch', '--all', '--prune') -WorkingDirectory $temporaryDirectory
    Assert-True ($safeFetch.ExitCode -eq 0) "The tag-safe ref refresh failed. $($safeFetch.Output)"
    Write-Output "Tag-safe fetch reproduction passed: pre-fix fetch exit $($legacyFetch.ExitCode) with 'would clobber existing tag'; new fetch exit $($safeFetch.ExitCode)."

    New-Item -ItemType Directory -Path (Join-Path $temporaryDirectory '.githooks') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot '.githooks\commit-msg') -Destination (Join-Path $temporaryDirectory '.githooks\commit-msg')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot '.githooks\check-history') -Destination (Join-Path $temporaryDirectory '.githooks\check-history')

    $init = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $temporaryDirectory, 'init', '--quiet', '--initial-branch=main') -WorkingDirectory $temporaryDirectory
    Assert-True ($init.ExitCode -eq 0) "Could not initialize the synthetic repository. $($init.Output)"
    foreach ($setting in @(@('user.name', 'KeelMatrix'), @('user.email', 'keelmatrix@gmail.com'))) {
        $configured = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $temporaryDirectory, 'config', $setting[0], $setting[1]) -WorkingDirectory $temporaryDirectory
        Assert-True ($configured.ExitCode -eq 0) "Could not configure synthetic repository identity. $($configured.Output)"
    }

    $trailerName = Convert-CodePoints @(67, 111, 45, 65, 117, 116, 104, 111, 114, 101, 100, 45, 66, 121)
    $badMessage = "Synthetic history check`n`n${trailerName}: Example Maintainer <bad@example.test>`n"
    $badMessagePath = Join-Path $temporaryDirectory 'bad-message.txt'
    Set-Content -LiteralPath $badMessagePath -Value $badMessage -NoNewline -Encoding utf8NoBOM

    $shellPrefix = 'export PATH=/usr/bin:/bin:$PATH; '
    $directCheck = Invoke-Native -FilePath $shellPath -ArgumentList @('-c', ($shellPrefix + 'exec sh .githooks/commit-msg bad-message.txt')) -WorkingDirectory $temporaryDirectory
    Assert-True ($directCheck.ExitCode -ne 0) "The commit-message check accepted a synthetic identity trailer. $($directCheck.Output)"

    $emptyTree = '4b825dc642cb6eb9a060e54bf8d69288fbee4904'
    $badCommitResult = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $temporaryDirectory, 'commit-tree', $emptyTree, '-F', $badMessagePath) -WorkingDirectory $temporaryDirectory
    Assert-True ($badCommitResult.ExitCode -eq 0) "Could not create the synthetic bad commit. $($badCommitResult.Output)"
    $badCommit = $badCommitResult.Output.Trim()
    $updateBad = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $temporaryDirectory, 'update-ref', 'refs/heads/main', $badCommit) -WorkingDirectory $temporaryDirectory
    Assert-True ($updateBad.ExitCode -eq 0) "Could not make the synthetic bad commit reachable. $($updateBad.Output)"

    $historyCheck = Invoke-Native -FilePath $shellPath -ArgumentList @('-c', ($shellPrefix + 'exec sh .githooks/check-history')) -WorkingDirectory $temporaryDirectory
    Assert-True ($historyCheck.ExitCode -ne 0) "The full reachable-history check accepted a synthetic bad commit. $($historyCheck.Output)"

    $goodMessagePath = Join-Path $temporaryDirectory 'good-message.txt'
    Set-Content -LiteralPath $goodMessagePath -Value "Synthetic history check passed`n" -NoNewline -Encoding utf8NoBOM
    $goodCommitResult = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $temporaryDirectory, 'commit-tree', $emptyTree, '-F', $goodMessagePath) -WorkingDirectory $temporaryDirectory
    Assert-True ($goodCommitResult.ExitCode -eq 0) "Could not create the synthetic clean commit. $($goodCommitResult.Output)"
    $goodCommit = $goodCommitResult.Output.Trim()
    $updateGood = Invoke-Native -FilePath 'git' -ArgumentList @('-C', $temporaryDirectory, 'update-ref', 'refs/heads/main', $goodCommit) -WorkingDirectory $temporaryDirectory
    Assert-True ($updateGood.ExitCode -eq 0) "Could not make the synthetic clean commit reachable. $($updateGood.Output)"

    $cleanHistoryCheck = Invoke-Native -FilePath $shellPath -ArgumentList @('-c', ($shellPrefix + 'exec sh .githooks/check-history')) -WorkingDirectory $temporaryDirectory
    Assert-True ($cleanHistoryCheck.ExitCode -eq 0) "The full reachable-history check rejected a clean synthetic commit. $($cleanHistoryCheck.Output)"

    $currentHistoryCheck = Invoke-Native -FilePath $shellPath -ArgumentList @('-c', ($shellPrefix + 'exec sh .githooks/check-history')) -WorkingDirectory $repositoryRoot
    Assert-True ($currentHistoryCheck.ExitCode -eq 0) "The full reachable-history check rejected the current repository history. $($currentHistoryCheck.Output)"

    Write-Output 'History hygiene contract passed: the pre-fix workflow shape is rejected, the legacy tag fetch fails while the safe refresh succeeds, and identity trailers are rejected directly and through the all-reachable-commits gate.'
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
