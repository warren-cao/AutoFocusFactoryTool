#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Tag,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$SourceDirectory,
    [string]$ArtifactDirectory,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$GiteeRepository = $(if ($env:GITEE_REPOSITORY) { $env:GITEE_REPOSITORY } else { 'warren-cao/focusir-workbench-releases' }),
    [ValidateRange(0, [long]::MaxValue)]
    [long]$ExistingReleaseId = 0,
    [ValidateRange(0, [long]::MaxValue)]
    [long]$FailedUploadAssetId = 0,
    [string]$UploadHostAddress = $env:GH_UPLOAD_HOST_ADDRESS
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw '未配置 GitHub 发布凭据。' }
$repositoryRoot = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory) } else { Split-Path -Parent $PSScriptRoot }
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $repositoryRoot 'artifacts/release' }
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
# 用同一套验签和摘要规则复核；不依赖自托管主机预装 gh CLI。
& (Join-Path $PSScriptRoot 'Publish-GiteeRelease.ps1') -Tag $Tag -Repository $GiteeRepository -Mode Validate -SourceDirectory $repositoryRoot -ArtifactDirectory $ArtifactDirectory
$manifest = Get-Content -LiteralPath (Join-Path $artifactDirectory 'update-manifest.json') -Raw | ConvertFrom-Json
$headers = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'FocusIR-Release'
}
$apiRoot = "https://api.github.com/repos/$Repository"
$releaseId = $null
$names = @('update-manifest.sig', 'SHA256SUMS.txt', 'update-manifest.json', $manifest.package.fileName)
$logRoot = Join-Path $repositoryRoot 'artifacts/release-logs'
$null = [IO.Directory]::CreateDirectory($logRoot)
$logPath = Join-Path $logRoot ("github-{0}.jsonl" -f [guid]::NewGuid().ToString('N'))
function Write-GitHubDiagnostic {
    param([string]$Event, [hashtable]$Fields)
    $record = [ordered]@{ timestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); event = $Event }
    foreach ($key in $Fields.Keys) { $record[$key] = $Fields[$key] }
    $line = $record | ConvertTo-Json -Depth 5 -Compress
    [IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host $line
}
function Assert-Asset {
    param([object]$Asset, [string]$Name)
    $path = Join-Path $ArtifactDirectory $Name
    $digest = $Asset.PSObject.Properties['digest']
    $expected = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    # 上传响应中的摘要可能尚未生成；只重查元数据，不重传附件。
    for ($attempt = 0; ($null -eq $digest -or -not $digest.Value) -and $attempt -lt 5; $attempt++) {
        Start-Sleep -Seconds 1
        $Asset = Invoke-RestMethod -Uri "$apiRoot/releases/assets/$($Asset.id)" -Headers $headers -TimeoutSec 60
        $digest = $Asset.PSObject.Properties['digest']
    }
    if ($Asset.name -cne $Name -or [long]$Asset.size -ne (Get-Item -LiteralPath $path).Length -or
        $Asset.state -cne 'uploaded' -or $null -eq $digest -or $digest.Value -ine $expected) {
        Write-GitHubDiagnostic 'asset-verification-failed' @{
            fileName = $Name; remoteSize = $Asset.size; state = $Asset.state
            nameMatches = ($Asset.name -ceq $Name); digestPresent = ($null -ne $digest -and [bool]$digest.Value)
            digestMatches = ($null -ne $digest -and $digest.Value -ieq $expected)
        }
        throw 'GitHub 附件的名称、长度、状态或 SHA-256 不匹配；不会覆盖或删除已有附件。'
    }
}
function Quote-CurlValue {
    param([string]$Value)
    if ($Value -match '[\r\n]') { throw '上传参数包含非法换行。' }
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}
function Send-GitHubAsset {
    param([string]$Name)
    $curl = Get-Command curl.exe -ErrorAction Stop
    $file = Get-Item -LiteralPath (Join-Path $ArtifactDirectory $Name)
    $uri = "https://uploads.github.com/repos/$Repository/releases/$releaseId/assets?name=$([Uri]::EscapeDataString($Name))"
    # 禁用 curlrc；凭据只经 stdin 传入，不放命令行、临时文件、响应日志或重定向。
    $config = @(
        'silent', 'show-error', 'http1.1', 'proto = "=https"',
        'request = "POST"', ('url = ' + (Quote-CurlValue $uri)),
        ('header = ' + (Quote-CurlValue "Authorization: Bearer $env:GH_TOKEN")),
        'header = "Accept: application/vnd.github+json"',
        'header = "X-GitHub-Api-Version: 2022-11-28"',
        'header = "Content-Type: application/octet-stream"', 'header = "Expect:"',
        ('upload-file = ' + (Quote-CurlValue $file.FullName)),
        'connect-timeout = 20', 'max-time = 1200', 'speed-limit = 1024', 'speed-time = 90',
        'write-out = "\nFOCUSIR_HTTP:%{http_code}|%{size_upload}|%{time_total}\n"'
    )
    if ($UploadHostAddress) {
        $address = [Net.IPAddress]::Parse($UploadHostAddress)
        if ($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw '上传地址覆盖仅支持单个有效 IPv4。' }
        # 只覆盖该次上传的解析，仍校验 uploads.github.com 的 HTTPS 证书。
        $config += 'resolve = ' + (Quote-CurlValue "uploads.github.com:443:$address")
    }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $curl.Source
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($arg in @('-q', '--config', '-')) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = $false
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        Write-GitHubDiagnostic 'upload-start' @{ fileName = $Name; size = $file.Length; addressOverride = [bool]$UploadHostAddress }
        $started = $process.Start()
        if (-not $started) { throw '无法启动系统上传工具。' }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine(($config -join "`n"))
        $process.StandardInput.Close()
        $config = $null
        $last = 0.0
        while (-not $process.HasExited) {
            Start-Sleep -Milliseconds 250
            if ($timer.Elapsed.TotalSeconds - $last -ge 15) {
                $last = $timer.Elapsed.TotalSeconds
                Write-GitHubDiagnostic 'upload-wait' @{ fileName = $Name; elapsedSeconds = [Math]::Round($last, 1) }
            }
        }
        $output = $outputTask.GetAwaiter().GetResult()
        $null = $errorTask.GetAwaiter().GetResult() # 不回显原始错误，避免代理配置等敏感数据进入日志。
        $marker = $output.LastIndexOf("`nFOCUSIR_HTTP:", [StringComparison]::Ordinal)
        if ($marker -lt 0) { throw '上传工具未返回可验证的状态。' }
        $metrics = $output.Substring($marker).Trim() -replace '^FOCUSIR_HTTP:', ''
        $values = $metrics.Split('|')
        if ($values.Count -ne 3 -or $values[0] -notmatch '^\d{3}$') { throw '上传状态格式无效。' }
        $status = [int]$values[0]
        Write-GitHubDiagnostic 'upload-result' @{
            fileName = $Name; curlExitCode = $process.ExitCode; httpStatus = $status
            bytesSent = [double]::Parse($values[1], [Globalization.CultureInfo]::InvariantCulture)
            elapsedSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
        }
        if ($process.ExitCode -ne 0 -or $status -ne 201) { throw '上传未确认成功；须先核对草稿附件，不自动重试 POST。' }
        try { return ($output.Substring(0, $marker) | ConvertFrom-Json) }
        catch { throw '上传响应不是有效 JSON，未回显响应正文。' }
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
try {
    $stage = 'prepare'
    $activeName = ''
    # 先核对标签已存在。创建 Release 不能隐式生成新标签。
    $null = Invoke-RestMethod -Uri "$apiRoot/git/ref/tags/$Tag" -Headers $headers -TimeoutSec 60
    if ($ExistingReleaseId -gt 0) {
        $release = Invoke-RestMethod -Uri "$apiRoot/releases/$ExistingReleaseId" -Headers $headers -TimeoutSec 60
        if ([long]$release.id -ne $ExistingReleaseId -or $release.tag_name -cne $Tag -or $release.prerelease) { throw '指定 GitHub Release ID 与正式标签不匹配。' }
    } else {
        $existingStatus = 0
        $null = Invoke-RestMethod -Uri "$apiRoot/releases/tags/$Tag" -Headers $headers -SkipHttpErrorCheck -StatusCodeVariable existingStatus -TimeoutSec 60
        if ($existingStatus -ne 404) { throw 'GitHub 已存在同名 Release，或无法确认不存在；拒绝覆盖。' }
        $release = Invoke-RestMethod -Method Post -Uri "$apiRoot/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -TimeoutSec 60 -Body (@{
            tag_name = $Tag; name = "FocusIR Workbench $Tag"; body = $manifest.releaseNotes
            draft = $true; prerelease = $false
        } | ConvertTo-Json -Compress)
        if ($null -eq $release -or [long]$release.id -le 0 -or -not $release.draft) { throw 'GitHub 未确认有效草稿。' }
    }
    $releaseId = [long]$release.id
    foreach ($name in $names) {
        $activeName = $name
        $stage = 'read-assets'
        $assetResponse = Invoke-RestMethod -Uri "$apiRoot/releases/$releaseId/assets?per_page=100" -Headers $headers -TimeoutSec 60
        $assetMatches = @($assetResponse | Where-Object { $_.name -ceq $name })
        if ($assetMatches.Count -gt 1) { throw 'GitHub 存在重名附件，未改动。' }
        if ($assetMatches.Count -eq 1 -and $FailedUploadAssetId -gt 0 -and [long]$assetMatches[0].id -eq $FailedUploadAssetId) {
            # 必须由操作者显式选定，且二次读取仍为失败 ZIP 占位；绝不清理 uploaded 附件。
            $failed = Invoke-RestMethod -Uri "$apiRoot/releases/assets/$FailedUploadAssetId" -Headers $headers -TimeoutSec 60
            $failedDigest = $failed.PSObject.Properties['digest']
            if (-not $release.draft -or $name -cne $manifest.package.fileName -or $failed.name -cne $name -or
                $failed.state -cne 'starter' -or ($null -ne $failedDigest -and $failedDigest.Value) -or
                [long]$failed.size -ne (Get-Item -LiteralPath (Join-Path $ArtifactDirectory $name)).Length) {
                throw '指定附件不是本次失败 ZIP 的未完成占位，拒绝清理。'
            }
            $deleteStatus = 0
            $null = Invoke-RestMethod -Method Delete -Uri "$apiRoot/releases/assets/$FailedUploadAssetId" -Headers $headers -StatusCodeVariable deleteStatus -TimeoutSec 60
            if ($deleteStatus -ne 204) { throw '未确认占位附件清理完成，停止上传。' }
            Write-GitHubDiagnostic 'failed-placeholder-removed' @{ fileName = $name; assetId = $FailedUploadAssetId; state = 'starter' }
            $assetMatches = @()
        }
        if ($assetMatches.Count -eq 1) {
            $stage = 'verify-existing-asset'
            Assert-Asset -Asset $assetMatches[0] -Name $name
            Write-GitHubDiagnostic 'asset-verified-existing' @{ fileName = $name; releaseId = $releaseId }
        } else {
            if (-not $release.draft) { throw '已正式发布的版本缺少附件，禁止补写或覆盖。' }
            $stage = 'upload-asset'
            $asset = Send-GitHubAsset -Name $name
            $stage = 'verify-uploaded-asset'
            Assert-Asset -Asset $asset -Name $name
        }
    }
    $stage = 'verify-final-asset-set'
    $allAssets = Invoke-RestMethod -Uri "$apiRoot/releases/$releaseId/assets?per_page=100" -Headers $headers -TimeoutSec 60
    if (@($allAssets).Count -ne 4 -or @($allAssets | Where-Object { $_.name -cnotin $names }).Count -gt 0) { throw 'GitHub 附件集合不是约定的四个文件，拒绝转正式版。' }
    foreach ($asset in $allAssets) { Assert-Asset -Asset $asset -Name $asset.name }
    if ($release.draft) {
        $stage = 'publish-draft'
        $result = Invoke-RestMethod -Method Patch -Uri "$apiRoot/releases/$releaseId" -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"prerelease":false}' -TimeoutSec 60
        if ([long]$result.id -ne $releaseId -or $result.tag_name -cne $Tag -or $result.draft -or $result.prerelease) { throw 'GitHub 未确认正式发布。' }
    }
    Write-Host "GitHub 正式版发布成功：https://github.com/$Repository/releases/tag/$Tag"
}
catch {
    # 不回显异常正文：服务端响应或请求可能包含令牌。
    $status = if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) { [int]$_.Exception.Response.StatusCode } else { $null }
    Write-GitHubDiagnostic 'publication-failed' @{ stage = $stage; fileName = $activeName; scriptLine = $_.InvocationInfo.ScriptLineNumber; exceptionType = $_.Exception.GetType().FullName; httpStatus = $status; releaseId = $releaseId }
    Write-Warning "GitHub 镜像发布失败（类型=$($_.Exception.GetType().FullName)，HTTP=$status，草稿ID=$releaseId）。Gitee 可能已成功，不要重跑整条发布来覆盖。"
    throw 'GitHub 发布未完成；保留已有草稿和附件供人工核对。请参阅 eng/GITHUB_ACTIONS.md。'
}
