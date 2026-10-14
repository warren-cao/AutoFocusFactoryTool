[CmdletBinding()]
param(
    [switch]$SkipCleanCheck,
    [switch]$SkipCoverage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'FocusIR.Workbench.sln'
$resultsDirectory = Join-Path $repositoryRoot 'artifacts\test-results'
$buildDirectory = Join-Path $repositoryRoot 'artifacts\build'
$buildLogPath = Join-Path $buildDirectory 'FocusIR.Workbench.binlog'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "命令执行失败（退出码 $LASTEXITCODE）：$FilePath $($Arguments -join ' ')"
    }
}

Push-Location $repositoryRoot
try {
    if (-not $SkipCleanCheck) {
        $changes = @(git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0) {
            throw '无法读取 Git 工作区状态。'
        }

        if ($changes.Count -gt 0) {
            throw "CI 必须从干净工作区运行。检测到以下变更：`n$($changes -join "`n")"
        }
    }

    $sdkVersionText = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw '无法读取 .NET SDK 版本。'
    }

    $sdkVersion = $null
    if (-not [version]::TryParse($sdkVersionText, [ref]$sdkVersion) -or
        $sdkVersion.Major -ne 10 -or
        $sdkVersion.Minor -ne 0) {
        throw "需要 .NET 10.0 SDK，当前版本为 '$sdkVersionText'。"
    }

    Write-Host "使用 .NET SDK $sdkVersionText"

    $projectFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tools\FocusIR.Workbench') -Recurse -Filter '*.csproj' -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    )
    if ($projectFiles.Count -eq 0) {
        throw '未找到任何 .NET 项目。'
    }

    $missingLocks = @(
        $projectFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'packages.lock.json') -PathType Leaf)
        }
    )
    if ($missingLocks.Count -gt 0) {
        $missingPaths = $missingLocks.FullName -join "`n"
        throw "以下项目缺少 packages.lock.json，不能执行锁定还原：`n$missingPaths"
    }

    New-Item -ItemType Directory -Path $resultsDirectory, $buildDirectory -Force | Out-Null

    Invoke-CheckedCommand -FilePath dotnet -Arguments @('restore', $solutionPath, '--locked-mode')
    Invoke-CheckedCommand -FilePath dotnet -Arguments @(
        'build',
        $solutionPath,
        '-c', 'Release',
        '--no-restore',
        '--warnaserror',
        '-p:ContinuousIntegrationBuild=true',
        "-bl:$buildLogPath"
    )

    $testArguments = @(
        'test',
        $solutionPath,
        '-c', 'Release',
        '--no-restore',
        '--no-build',
        '--logger', 'trx;LogFilePrefix=FocusIR.Workbench',
        '--results-directory', $resultsDirectory
    )
    if (-not $SkipCoverage) {
        $testArguments += @('--collect', 'XPlat Code Coverage')
    }

    Invoke-CheckedCommand -FilePath dotnet -Arguments $testArguments
    Write-Host "CI 检查完成，测试结果位于：$resultsDirectory"
    Write-Host "MSBuild 二进制日志位于：$buildLogPath"
}
finally {
    Pop-Location
}
