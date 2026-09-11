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

try {
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

    Write-Output 'History hygiene contract passed: identity trailers are rejected directly and through the all-reachable-commits gate without leaving synthetic repository files.'
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
