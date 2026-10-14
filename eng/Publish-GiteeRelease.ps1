#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Tag,

    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = $(if ($env:GITEE_REPOSITORY) { $env:GITEE_REPOSITORY } else { 'warren-cao/auto-focus-factory-tool' }),

    [ValidateSet('Publish', 'Validate', 'CheckRemote')]
    [string]$Mode = 'Publish',

    [string]$ArtifactDirectory,
    [string]$TargetCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $repositoryRoot 'artifacts/release' }
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
$version = $Tag.Substring(1)
$packageName = "FocusIR.Workbench-$Tag-win-x64.zip"
$expectedNames = @($packageName, 'update-manifest.json', 'update-manifest.sig', 'SHA256SUMS.txt')

function Assert-LocalArtifacts {
    $files = @(Get-ChildItem -LiteralPath $ArtifactDirectory -File)
    if ($files.Count -ne 4 -or @($files.Name | Where-Object { $_ -notin $expectedNames }).Count -ne 0) {
        throw '发布目录必须且只能包含 ZIP、update-manifest.json、update-manifest.sig 和 SHA256SUMS.txt。'
    }
    $manifestPath = Join-Path $ArtifactDirectory 'update-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $package = Get-Item -LiteralPath (Join-Path $ArtifactDirectory $packageName)
    $expectedUrl = "https://gitee.com/$Repository/releases/download/$Tag/$packageName"
    if ($manifest.schemaVersion -ne 1 -or $manifest.tag -cne $Tag -or $manifest.version -cne $version -or
        $manifest.package.rid -cne 'win-x64' -or $manifest.package.fileName -cne $packageName -or
        $manifest.package.url -cne $expectedUrl -or [long]$manifest.package.size -ne $package.Length) {
        throw '发布清单与目标标签、平台、下载地址或 ZIP 长度不一致。'
    }
    if ($manifest.keyId -cnotmatch '^[a-z0-9][a-z0-9-]{0,63}$') { throw '签名 keyId 无效。' }
    $publicKeyPath = Join-Path $PSScriptRoot "keys/$($manifest.keyId).pem"
    $publicKey = [Security.Cryptography.ECDsa]::Create()
    try {
        $publicKey.ImportFromPem((Get-Content -LiteralPath $publicKeyPath -Raw -Encoding UTF8))
        $signature = [Convert]::FromBase64String((Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'update-manifest.sig') -Raw).Trim())
        if ($publicKey.KeySize -ne 256 -or $signature.Length -ne 64 -or -not $publicKey.VerifyData(
                [IO.File]::ReadAllBytes($manifestPath), $signature,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
            throw '发布清单签名校验失败。'
        }
    }
    finally { $publicKey.Dispose() }

    $checksums = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'SHA256SUMS.txt')) {
        if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw 'SHA256SUMS.txt 格式错误。' }
        $fileName = $Matches[2]
        if ($fileName -notin $expectedNames[0..2] -or $checksums.ContainsKey($fileName)) { throw '校验文件条目无效或重复。' }
        $checksums[$fileName] = $Matches[1]
        $actual = (Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory $fileName) -Algorithm SHA256).Hash
        if ($actual -ine $checksums[$fileName]) { throw "发布文件摘要不匹配：$fileName" }
    }
    if ($checksums.Count -ne 3 -or $manifest.package.sha256 -ine $checksums[$packageName]) { throw '发布摘要不完整或与清单不一致。' }
    return $manifest
}

# 此模式只做本地成品复核，不访问远端、不创建 Release。
if ($Mode -eq 'Validate') {
    $null = Assert-LocalArtifacts
    Write-Host '四个发布文件的结构、清单签名和 SHA-256 校验通过。'
    return
}

if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { throw '请配置 GitHub Actions Secret：GITEE_TOKEN。' }
if (-not $TargetCommit) {
    $TargetCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw '无法取得发布提交。' }
}
if ($TargetCommit -notmatch '^[a-fA-F0-9]{40}$') { throw '发布提交必须是完整的 Git SHA。' }

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMinutes(15)
$client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $env:GITEE_TOKEN)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('FocusIR-Release/0.2.0')
$apiRoot = "https://gitee.com/api/v5/repos/$Repository"

function Invoke-GiteeApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body,
        [Net.Http.HttpContent]$Content,
        [switch]$AllowNotFound
    )
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$apiRoot/$Path")
    try {
        if ($null -ne $Content) { $request.Content = $Content }
        elseif ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8, 'application/json')
        }
        try { $response = $client.SendAsync($request).GetAwaiter().GetResult() }
        catch { throw "Gitee API $Method $Path 网络连接失败；请检查节点网络。" }
        try {
            if ($AllowNotFound -and [int]$response.StatusCode -eq 404) { return $null }
            if (-not $response.IsSuccessStatusCode) {
                # 不输出响应正文或请求体，避免凭证被服务端回显到日志。
                throw "Gitee API $Method $Path 返回 HTTP $([int]$response.StatusCode)，请检查令牌权限、仓库状态和附件额度。"
            }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($text) { return ($text | ConvertFrom-Json) }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

try {
    # GitHub 和 Gitee 必须已有同一源码提交；不自动改写或推送 Gitee 主分支。
    $commit = Invoke-GiteeApi -Method GET -Path "commits/$TargetCommit" -AllowNotFound
    if ($null -eq $commit -or $commit.sha -ine $TargetCommit) {
        throw 'Gitee 尚无对应源码提交。请先向 Gitee 推送 master 和同一版本标签，再运行 GitHub 发布。'
    }
    $remoteTag = $null
    for ($page = 1; $page -le 100; $page++) {
        $tags = @(Invoke-GiteeApi -Method GET -Path "tags?page=$page&per_page=100")
        $matchingTags = @($tags | Where-Object { $_.name -ceq $Tag })
        if ($matchingTags.Count -gt 0) { $remoteTag = $matchingTags[0]; break }
        if ($tags.Count -lt 100) { break }
    }
    if ($null -eq $remoteTag -or $remoteTag.commit.sha -ine $TargetCommit) {
        throw 'Gitee 版本标签缺失或指向不同提交。请先同步同一标签，禁止把不同源码发布为同一版本。'
    }
    $existing = Invoke-GiteeApi -Method GET -Path "releases/tags/$Tag" -AllowNotFound
    if ($null -ne $existing) {
        throw "Gitee 已存在 $Tag 的 Release，禁止覆盖。失败后的预发布残留或 GitHub 镜像补发请按 eng/GITHUB_ACTIONS.md 处理。"
    }
    if ($Mode -eq 'CheckRemote') {
        Write-Host "Gitee 已包含源码提交，$Tag 尚未发布。"
        return
    }

    $manifest = Assert-LocalArtifacts
    # 上传期间保持预发布；客户端不会接收半成品。上传失败保留预发布用于人工核对。
    $release = Invoke-GiteeApi -Method POST -Path 'releases' -Body @{
        access_token = $env:GITEE_TOKEN
        tag_name = $Tag
        target_commitish = $TargetCommit
        name = "FocusIR Workbench $Tag"
        body = $manifest.releaseNotes
        prerelease = $true
    }
    if ($null -eq $release -or [long]$release.id -le 0) { throw 'Gitee 未返回有效 Release ID。' }
    $releaseId = [long]$release.id
    try {
        if ([string]$release.prerelease -ine 'true') { throw 'Gitee 未确认预发布状态，拒绝继续上传。' }
        # 先传 ZIP、签名和校验摘要，最后才传清单。
        foreach ($fileName in @($packageName, 'update-manifest.sig', 'SHA256SUMS.txt', 'update-manifest.json')) {
            $filePath = Join-Path $ArtifactDirectory $fileName
            $form = [Net.Http.MultipartFormDataContent]::new()
            try {
                $form.Add([Net.Http.StringContent]::new($env:GITEE_TOKEN), 'access_token')
                $stream = [IO.File]::OpenRead($filePath)
                $fileContent = [Net.Http.StreamContent]::new($stream)
                $fileContent.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
                $form.Add($fileContent, 'file', $fileName)
                $null = Invoke-GiteeApi -Method POST -Path "releases/$releaseId/attach_files" -Content $form
                Write-Host "附件已上传：$fileName"
            }
            finally { $form.Dispose() }
        }

        $attachments = @(Invoke-GiteeApi -Method GET -Path "releases/$releaseId/attach_files?page=1&per_page=100")
        if ($attachments.Count -ne 4) { throw 'Gitee 返回的附件数不是四个，拒绝转为正式版。' }
        foreach ($fileName in $expectedNames) {
            $matchingAttachments = @($attachments | Where-Object { $_.name -ceq $fileName })
            if ($matchingAttachments.Count -ne 1) { throw "Gitee 缺少附件或附件重名：$fileName" }
            $sizeProperty = $matchingAttachments[0].PSObject.Properties['size']
            if ($null -ne $sizeProperty -and [long]$sizeProperty.Value -ne (Get-Item -LiteralPath (Join-Path $ArtifactDirectory $fileName)).Length) {
                throw "Gitee 附件长度不匹配：$fileName"
            }
        }
        $stableRelease = Invoke-GiteeApi -Method PATCH -Path "releases/$releaseId" -Body @{
            access_token = $env:GITEE_TOKEN
            prerelease = $false
        }
        $stableFlag = [string]$stableRelease.prerelease
        if ($stableFlag -ine 'false') { throw 'Gitee 未确认正式版状态。' }
        Write-Host "Gitee 正式版发布成功：https://gitee.com/$Repository/releases/tag/$Tag"
    }
    catch {
        Write-Warning "Gitee Release $releaseId 可能仍处于预发布状态；请核对附件后再处理，不要盲目覆盖或重试创建。"
        throw
    }
}
finally { $client.Dispose() }
