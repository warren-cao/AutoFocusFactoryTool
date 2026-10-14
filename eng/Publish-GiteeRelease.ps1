#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Tag,

    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = $(if ($env:GITEE_REPOSITORY) { $env:GITEE_REPOSITORY } else { 'warren-cao/auto-focus-factory-tool' }),

    [ValidateSet('Publish', 'Validate', 'CheckRemote', 'Diagnose')]
    [string]$Mode = 'Publish',

    [string]$ArtifactDirectory,
    [string]$TargetCommit,
    [string]$SourceDirectory,
    [ValidateRange(1, 120)]
    [int]$RequestTimeoutMinutes = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory) } else { Split-Path -Parent $PSScriptRoot }
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
    $publicKeyPath = Join-Path $repositoryRoot "eng/keys/$($manifest.keyId).pem"
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
$client.Timeout = [TimeSpan]::FromMinutes($RequestTimeoutMinutes)
$client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $env:GITEE_TOKEN)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('FocusIR-Release/0.2.0')
$apiRoot = "https://gitee.com/api/v5/repos/$Repository"

# 白名单结构化日志：不记录正文、请求头、原始异常消息、代理地址或令牌。
$logDirectory = Join-Path $repositoryRoot 'artifacts/release-logs'
$null = [IO.Directory]::CreateDirectory($logDirectory)
$diagnosticPath = Join-Path $logDirectory ("gitee-{0}-{1}.jsonl" -f $Mode, [guid]::NewGuid().ToString('N'))
function Write-ReleaseDiagnostic {
    param([string]$Event, [hashtable]$Fields = @{})
    $record = [ordered]@{ timestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); event = $Event }
    foreach ($name in $Fields.Keys) { $record[$name] = $Fields[$name] }
    $line = $record | ConvertTo-Json -Compress -Depth 6
    [IO.File]::AppendAllText($diagnosticPath, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host $line
}

# 传输线程仅更新计数；日志仍由 PowerShell 主线程写，避免脚本回调跨 runspace。
if (-not ('FocusIR.Release.UploadContent' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace FocusIR.Release {
    public sealed class UploadContent : HttpContent {
        private readonly FileStream source;
        private long bytesSent;
        public long BytesSent => Interlocked.Read(ref bytesSent);
        public long FileSize => source.Length;
        public UploadContent(string path) {
            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        protected override bool TryComputeLength(out long length) { length = source.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context,
                                                              CancellationToken cancellationToken) {
            byte[] buffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0) {
                await stream.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref bytesSent, count);
            }
        }
        protected override void Dispose(bool disposing) {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}
'@
}

Write-ReleaseDiagnostic 'session' @{
    mode = $Mode; tag = $Tag; hostName = 'gitee.com'; timeoutSeconds = $client.Timeout.TotalSeconds
    runnerName = $env:RUNNER_NAME; runnerOS = $env:RUNNER_OS; runnerArch = $env:RUNNER_ARCH
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    proxyConfigured = [bool]($env:HTTPS_PROXY -or $env:HTTP_PROXY -or $env:ALL_PROXY)
}

function Get-SafeExceptionDetails {
    param([Exception]$Exception)
    $details = [Collections.Generic.List[object]]::new()
    $isTimeout = $false
    $current = $Exception
    for ($depth = 0; $null -ne $current -and $depth -lt 8; $depth++) {
        $item = @{ type = $current.GetType().FullName; hresult = $current.HResult }
        if ($current -is [TimeoutException] -or $current -is [OperationCanceledException]) { $isTimeout = $true }
        if ($current -is [Net.Http.HttpRequestException]) {
            $item['httpRequestError'] = [string]$current.HttpRequestError
            $item['statusCode'] = if ($null -ne $current.StatusCode) { [int]$current.StatusCode } else { $null }
        }
        if ($current -is [Net.Sockets.SocketException]) { $item['socketError'] = [string]$current.SocketErrorCode }
        $details.Add($item)
        $current = $current.InnerException
    }
    return @{ category = $(if ($isTimeout) { 'TimeoutOrCancellation' } else { 'TransportError' }); exceptions = @($details.ToArray()) }
}

function Invoke-GiteeApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body,
        [Net.Http.HttpContent]$Content,
        [object]$UploadContent,
        [string]$FileName,
        [switch]$AllowNotFound
    )
    $requestId = [guid]::NewGuid().ToString('N')
    $safePath = $Path.Split('?')[0]
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$apiRoot/$Path")
    try {
        if ($null -ne $Content) { $request.Content = $Content }
        elseif ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8, 'application/json')
        }
        Write-ReleaseDiagnostic 'request-start' @{
            requestId = $requestId; method = $Method; path = $safePath; fileName = $FileName
            fileSize = $(if ($null -ne $UploadContent) { $UploadContent.FileSize } else { $null })
            contentLength = $(if ($null -ne $request.Content) { $request.Content.Headers.ContentLength } else { 0 })
        }
        try {
            $pending = $client.SendAsync($request)
            $lastProgressSeconds = 0.0
            while (-not $pending.IsCompleted) {
                # 不使用 Task.Wait：失败时让 GetResult 保留完整的内部异常类型。
                Start-Sleep -Milliseconds 250
                if ($null -ne $UploadContent -and $timer.Elapsed.TotalSeconds - $lastProgressSeconds -ge 15) {
                    $lastProgressSeconds = $timer.Elapsed.TotalSeconds
                    Write-ReleaseDiagnostic 'upload-progress' @{
                        requestId = $requestId; fileName = $FileName; fileSize = $UploadContent.FileSize
                        bytesSent = $UploadContent.BytesSent; elapsedSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
                        # 这里只表示已写入传输流，不代表 Gitee 已接收或已落盘。
                        waitingForResponse = ($UploadContent.BytesSent -eq $UploadContent.FileSize)
                    }
                }
            }
            $response = $pending.GetAwaiter().GetResult()
        }
        catch {
            $failure = Get-SafeExceptionDetails $_.Exception
            Write-ReleaseDiagnostic 'request-error' @{
                requestId = $requestId; method = $Method; path = $safePath; fileName = $FileName
                elapsedSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
                bytesSent = $(if ($null -ne $UploadContent) { $UploadContent.BytesSent } else { $null })
                category = $failure.category; exceptions = $failure.exceptions
            }
            throw "Gitee API $Method $safePath 请求失败（$($failure.category)，耗时 $([Math]::Round($timer.Elapsed.TotalSeconds, 1)) 秒，requestId=$requestId）；详见 artifacts/release-logs。"
        }
        try {
            Write-ReleaseDiagnostic 'request-response' @{
                requestId = $requestId; method = $Method; path = $safePath; statusCode = [int]$response.StatusCode
                elapsedSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
                bytesSent = $(if ($null -ne $UploadContent) { $UploadContent.BytesSent } else { $null })
            }
            if ($AllowNotFound -and [int]$response.StatusCode -eq 404) { return $null }
            if (-not $response.IsSuccessStatusCode) {
                # 不输出响应正文或请求体，避免凭证被服务端回显到日志。
                throw "Gitee API $Method $safePath 返回 HTTP $([int]$response.StatusCode)（requestId=$requestId），请检查令牌权限、仓库状态和附件额度。"
            }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($text) {
                try { return ($text | ConvertFrom-Json) }
                catch {
                    Write-ReleaseDiagnostic 'response-parse-error' @{ requestId = $requestId; exceptionType = $_.Exception.GetType().FullName }
                    throw "Gitee 响应不是有效 JSON（requestId=$requestId）；响应正文未写入日志。"
                }
            }
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
    if ($Mode -eq 'Diagnose') {
        if ($null -ne $existing) {
            $attachments = @(Invoke-GiteeApi -Method GET -Path "releases/$($existing.id)/attach_files?page=1&per_page=100")
            Write-ReleaseDiagnostic 'existing-release' @{
                releaseId = $existing.id; prerelease = $existing.prerelease; attachmentCount = $attachments.Count
            }
        }
        Write-Host 'Gitee 只读诊断完成；没有创建、上传、修改或删除 Release。此检查不证明大文件上传一定成功。'
        return
    }
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
                $fileContent = [FocusIR.Release.UploadContent]::new($filePath)
                $fileContent.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
                $form.Add($fileContent, 'file', $fileName)
                $null = Invoke-GiteeApi -Method POST -Path "releases/$releaseId/attach_files" -Content $form -UploadContent $fileContent -FileName $fileName
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
