#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Tag,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$SourceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw '未配置 GitHub 发布凭据。' }
$repositoryRoot = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory) } else { Split-Path -Parent $PSScriptRoot }
$artifactDirectory = Join-Path $repositoryRoot 'artifacts/release'
# 用同一套验签和摘要规则复核；不依赖自托管主机预装 gh CLI。
& (Join-Path $PSScriptRoot 'Publish-GiteeRelease.ps1') -Tag $Tag -Mode Validate -SourceDirectory $repositoryRoot
$manifest = Get-Content -LiteralPath (Join-Path $artifactDirectory 'update-manifest.json') -Raw | ConvertFrom-Json
$headers = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'FocusIR-Release'
}
$apiRoot = "https://api.github.com/repos/$Repository"
$releaseId = $null
try {
    # 先核对标签已存在。创建 Release 不能隐式生成新标签。
    $null = Invoke-RestMethod -Uri "$apiRoot/git/ref/tags/$Tag" -Headers $headers -TimeoutSec 60
    $existingStatus = 0
    $null = Invoke-RestMethod -Uri "$apiRoot/releases/tags/$Tag" -Headers $headers -SkipHttpErrorCheck -StatusCodeVariable existingStatus -TimeoutSec 60
    if ($existingStatus -ne 404) { throw 'GitHub 已存在同名 Release，或无法确认不存在；拒绝覆盖。' }
    $release = Invoke-RestMethod -Method Post -Uri "$apiRoot/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -TimeoutSec 60 -Body (@{
        tag_name = $Tag; name = "FocusIR Workbench $Tag"; body = $manifest.releaseNotes
        draft = $true; prerelease = $false
    } | ConvertTo-Json -Compress)
    if ($null -eq $release -or [long]$release.id -le 0 -or -not $release.draft) { throw 'GitHub 未确认有效草稿。' }
    $releaseId = [long]$release.id
    foreach ($name in @($manifest.package.fileName, 'update-manifest.sig', 'SHA256SUMS.txt', 'update-manifest.json')) {
        $filePath = Join-Path $artifactDirectory $name
        $encodedName = [Uri]::EscapeDataString($name)
        $asset = Invoke-RestMethod -Method Post -Uri "https://uploads.github.com/repos/$Repository/releases/$releaseId/assets?name=$encodedName" -Headers $headers -InFile $filePath -ContentType 'application/octet-stream' -TimeoutSec 900
        if ($asset.name -cne $name -or [long]$asset.size -ne (Get-Item -LiteralPath $filePath).Length -or $asset.state -cne 'uploaded') {
            throw 'GitHub 未确认附件完整上传。'
        }
        Write-Host "GitHub 附件已上传：$name"
    }
    $result = Invoke-RestMethod -Method Patch -Uri "$apiRoot/releases/$releaseId" -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"prerelease":false}' -TimeoutSec 60
    if ($result.draft -or $result.prerelease) { throw 'GitHub 未确认正式发布。' }
    Write-Host "GitHub 正式版发布成功：https://github.com/$Repository/releases/tag/$Tag"
}
catch {
    # 不回显异常正文：服务端响应或请求可能包含令牌。
    $status = if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) { [int]$_.Exception.Response.StatusCode } else { $null }
    Write-Warning "GitHub 镜像发布失败（类型=$($_.Exception.GetType().FullName)，HTTP=$status，草稿ID=$releaseId）。Gitee 可能已成功，不要重跑整条发布来覆盖。"
    throw 'GitHub 发布未完成；保留已有草稿和附件供人工核对。请参阅 eng/GITHUB_ACTIONS.md。'
}
