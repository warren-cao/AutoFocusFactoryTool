[CmdletBinding()]
param(
    [string]$Tag,

    [string]$Repository = $(
        if ([string]::IsNullOrWhiteSpace($env:GITEE_REPOSITORY)) {
            'warren-cao/focusir-workbench-releases'
        }
        else {
            $env:GITEE_REPOSITORY
        }
    ),

    [string]$KeyId = $(
        if ([string]::IsNullOrWhiteSpace($env:UPDATE_SIGNING_KEY_ID)) {
            'focusir-release-2026'
        }
        else {
            $env:UPDATE_SIGNING_KEY_ID
        }
    ),

    [string]$MinimumUpdaterVersion = $(
        if ([string]::IsNullOrWhiteSpace($env:MINIMUM_UPDATER_VERSION)) {
            '0.2.0'
        }
        else {
            $env:MINIMUM_UPDATER_VERSION
        }
    ),

    [string]$OutputDirectory,

    [string]$BridgeRepository = $env:GITEE_BRIDGE_REPOSITORY,
    [string]$BridgeOutputDirectory,

    # 允许可信工作流中的发布工具构建另一标签的源码，默认仍为当前仓库。
    [string]$SourceDirectory,

    [switch]$SkipCleanCheck,

    [switch]$SkipRemoteReleaseCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    Split-Path -Parent $PSScriptRoot
}
else {
    [IO.Path]::GetFullPath($SourceDirectory)
}
$solutionPath = Join-Path $repositoryRoot 'FocusIR.Workbench.sln'
$appProject = Join-Path $repositoryRoot 'tools\FocusIR.Workbench\src\FocusIR.Workbench.App\FocusIR.Workbench.App.csproj'
$updaterProject = Join-Path $repositoryRoot 'tools\FocusIR.Workbench\src\FocusIR.Workbench.Updater\FocusIR.Workbench.Updater.csproj'
$changelogPath = Join-Path $repositoryRoot 'CHANGELOG.md'
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\release'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$trustedPublicKeyPath = Join-Path $repositoryRoot "eng\keys\$KeyId.pem"
if ($BridgeRepository -and -not $BridgeOutputDirectory) { $BridgeOutputDirectory = Join-Path $artifactsRoot 'bridge-release' }
if ($BridgeOutputDirectory) { $BridgeOutputDirectory = [IO.Path]::GetFullPath($BridgeOutputDirectory) }
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)

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

function Get-GitText {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = (& git @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "Git 命令执行失败：git $($Arguments -join ' ')"
    }
    return ($output -join "`n").Trim()
}

function Get-ReleaseNotes {
    param(
        [Parameter(Mandatory)][string]$Changelog,
        [Parameter(Mandatory)][string]$Version
    )

    $headerPattern = "(?m)^##\s+\[?$([regex]::Escape($Version))\]?(?:\s+-\s+\d{4}-\d{2}-\d{2})?\s*$"
    $header = [regex]::Match($Changelog, $headerPattern)
    if (-not $header.Success) {
        throw "CHANGELOG.md 中缺少版本 $Version 的二级标题。"
    }

    $contentStart = $header.Index + $header.Length
    $nextHeader = [regex]::Match($Changelog.Substring($contentStart), '(?m)^##\s+')
    $contentLength = if ($nextHeader.Success) { $nextHeader.Index } else { $Changelog.Length - $contentStart }
    $notes = $Changelog.Substring($contentStart, $contentLength).Trim()
    if ([string]::IsNullOrWhiteSpace($notes)) {
        throw "CHANGELOG.md 中版本 $Version 的发布说明为空。"
    }
    return $notes
}

function ConvertTo-SemanticVersion {
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match($Value, '^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')
    if (-not $match.Success) {
        return $null
    }
    return [version]::new(
        [int]$match.Groups[1].Value,
        [int]$match.Groups[2].Value,
        [int]$match.Groups[3].Value
    )
}

function ConvertTo-GiteeBoolean {
    param([object]$Value)

    if ($null -eq $Value) {
        return $false
    }
    if ($Value -is [bool]) {
        return $Value
    }
    $parsed = $false
    if ($Value -is [string] -and [bool]::TryParse($Value, [ref]$parsed)) {
        return $parsed
    }
    throw "Gitee Release 布尔字段值无效：$Value"
}

function Get-GiteeStableReleases {
    param([Parameter(Mandatory)][string]$RepositoryPath)

    $headers = @{ Accept = 'application/json' }
    if (-not [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        $headers.Authorization = "token $($env:GITEE_TOKEN)"
    }

    $releases = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 10; $page++) {
        $uri = "https://gitee.com/api/v5/repos/$RepositoryPath/releases?page=$page&per_page=100"
        try {
            # Invoke-RestMethod 把 JSON 数组作为一个管道对象返回。
            # 在命令外套 @() 会得到嵌套数组，把 [] 错当成一条 Release。
            # 直接赋值后再 foreach，才能正确枚举零条、一条及多条记录。
            $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -TimeoutSec 30
        }
        catch {
            throw "无法读取 Gitee Release 列表，发布已终止：$($_.Exception.Message)"
        }

        if ($null -eq $response -or $response -isnot [Array]) {
            throw "Gitee 第 $page 页 Release 列表不是 JSON 数组，发布已终止。"
        }

        foreach ($release in $response) {
            if ($null -eq $release) {
                throw "Gitee 第 $page 页 Release 列表包含空记录，发布已终止。"
            }
            $tagProperty = $release.PSObject.Properties['tag_name']
            if ($null -eq $tagProperty -or $tagProperty.Value -isnot [string] -or
                [string]::IsNullOrWhiteSpace($tagProperty.Value)) {
                throw "Gitee 第 $page 页 Release 记录缺少有效的 tag_name，发布已终止。"
            }
            $prereleaseProperty = $release.PSObject.Properties['prerelease']
            $draftProperty = $release.PSObject.Properties['draft']
            $isPrerelease = $null -ne $prereleaseProperty -and (ConvertTo-GiteeBoolean $prereleaseProperty.Value)
            $isDraft = $null -ne $draftProperty -and (ConvertTo-GiteeBoolean $draftProperty.Value)
            if (-not $isPrerelease -and -not $isDraft) {
                $releases.Add($release)
            }
        }
        if ($response.Count -lt 100) {
            break
        }
    }
    return $releases
}

function Import-ReleasePrivateKey {
    param([Parameter(Mandatory)][string]$PrivateKeyText)

    $trimmed = $PrivateKeyText.Trim()
    if ($trimmed.StartsWith('-----BEGIN PRIVATE KEY-----', [StringComparison]::Ordinal)) {
        $match = [regex]::Match(
            $trimmed,
            '^-----BEGIN PRIVATE KEY-----\s*(?<key>[A-Za-z0-9+/=\s]+?)\s*-----END PRIVATE KEY-----$',
            [Text.RegularExpressions.RegexOptions]::Singleline
        )
        if (-not $match.Success) {
            throw 'UPDATE_SIGNING_KEY_PKCS8 的 PKCS#8 PEM 格式无效。'
        }
        $base64Key = $match.Groups['key'].Value -replace '\s', ''
    }
    else {
        $base64Key = $trimmed -replace '\s', ''
    }

    try {
        $raw = [Convert]::FromBase64String($base64Key)
    }
    catch {
        throw 'UPDATE_SIGNING_KEY_PKCS8 既不是 PKCS#8 PEM，也不是有效的 PKCS#8 Base64。'
    }

    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        try {
            $key.ImportPkcs8PrivateKey($raw, [ref]$bytesRead)
            if ($bytesRead -ne $raw.Length) {
                throw 'PKCS#8 私钥尾部包含未解析数据。'
            }
        }
        catch [Security.Cryptography.CryptographicException] {
            # 某些 Windows CNG 环境无法直接导入自己导出的 EC PKCS#8。
            # 仍严格解析 PKCS#8，再用其中的 RFC 5915 ECPrivateKey 构造 P-256 参数。
            $reader = [System.Formats.Asn1.AsnReader]::new(
                $raw,
                [System.Formats.Asn1.AsnEncodingRules]::DER
            )
            $privateKeyInfo = $reader.ReadSequence()
            if ($privateKeyInfo.ReadInteger() -ne 0) {
                throw '只支持版本 0 的 PKCS#8 PrivateKeyInfo。'
            }

            $algorithm = $privateKeyInfo.ReadSequence()
            if ($algorithm.ReadObjectIdentifier() -ne '1.2.840.10045.2.1' -or
                $algorithm.ReadObjectIdentifier() -ne '1.2.840.10045.3.1.7') {
                throw '签名私钥必须是 id-ecPublicKey / prime256v1 (P-256)。'
            }
            $algorithm.ThrowIfNotEmpty()
            $ecPrivateKeyBytes = $privateKeyInfo.ReadOctetString()
            $privateKeyInfo.ThrowIfNotEmpty()
            $reader.ThrowIfNotEmpty()

            $ecReader = [System.Formats.Asn1.AsnReader]::new(
                $ecPrivateKeyBytes,
                [System.Formats.Asn1.AsnEncodingRules]::DER
            )
            $ecPrivateKey = $ecReader.ReadSequence()
            if ($ecPrivateKey.ReadInteger() -ne 1) {
                throw 'RFC 5915 ECPrivateKey 版本必须为 1。'
            }
            $privateScalar = $ecPrivateKey.ReadOctetString()
            if ($privateScalar.Length -ne 32) {
                throw 'P-256 私钥标量必须为 32 字节。'
            }

            $parameters = [Security.Cryptography.ECParameters]::new()
            $parameters.Curve = [Security.Cryptography.ECCurve+NamedCurves]::nistP256
            $parameters.D = $privateScalar

            while ($ecPrivateKey.HasData) {
                $tag = $ecPrivateKey.PeekTag()
                if ($tag.TagClass -eq [System.Formats.Asn1.TagClass]::ContextSpecific -and
                    $tag.TagValue -eq 0) {
                    $parameterTag = [System.Formats.Asn1.Asn1Tag]::new(
                        [System.Formats.Asn1.TagClass]::ContextSpecific,
                        0,
                        $true
                    )
                    $curveParameters = $ecPrivateKey.ReadSequence($parameterTag)
                    if ($curveParameters.ReadObjectIdentifier() -ne '1.2.840.10045.3.1.7') {
                        throw 'ECPrivateKey 内的曲线不是 prime256v1 (P-256)。'
                    }
                    $curveParameters.ThrowIfNotEmpty()
                }
                elseif ($tag.TagClass -eq [System.Formats.Asn1.TagClass]::ContextSpecific -and
                        $tag.TagValue -eq 1) {
                    $publicKeyTag = [System.Formats.Asn1.Asn1Tag]::new(
                        [System.Formats.Asn1.TagClass]::ContextSpecific,
                        1,
                        $true
                    )
                    $publicKeyContainer = $ecPrivateKey.ReadSequence($publicKeyTag)
                    $unusedBitCount = 0
                    $publicPoint = $publicKeyContainer.ReadBitString([ref]$unusedBitCount)
                    $publicKeyContainer.ThrowIfNotEmpty()
                    if ($unusedBitCount -ne 0 -or $publicPoint.Length -ne 65 -or $publicPoint[0] -ne 4) {
                        throw 'ECPrivateKey 公钥点不是 P-256 未压缩格式。'
                    }
                    $parameters.Q = [Security.Cryptography.ECPoint]::new()
                    $parameters.Q.X = [byte[]]$publicPoint[1..32]
                    $parameters.Q.Y = [byte[]]$publicPoint[33..64]
                }
                else {
                    throw "ECPrivateKey 包含不支持的 ASN.1 字段：$tag"
                }
            }
            $ecReader.ThrowIfNotEmpty()

            $key.Dispose()
            $key = [Security.Cryptography.ECDsa]::Create($parameters)
        }

        if ($key.KeySize -ne 256) {
            throw "签名私钥必须使用 P-256，当前密钥大小为 $($key.KeySize) 位。"
        }
        return $key
    }
    catch {
        $key.Dispose()
        throw
    }
}

function Assert-ManagedAssemblyVersion {
    param(
        [Parameter(Mandatory)][string]$AssemblyPath,
        [Parameter(Mandatory)][version]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
        throw "发布输出缺少托管程序集：$AssemblyPath"
    }
    $actual = [Reflection.AssemblyName]::GetAssemblyName($AssemblyPath).Version
    if ($actual -ne $ExpectedVersion) {
        throw "程序集版本不一致：$AssemblyPath 为 $actual，期望 $ExpectedVersion。"
    }
}

function Assert-ExecutableProductVersion {
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "发布输出缺少可执行文件：$ExecutablePath"
    }
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath).ProductVersion
    if ($actual -ne $ExpectedVersion) {
        throw "产品版本不一致：$ExecutablePath 为 $actual，期望 $ExpectedVersion。"
    }
}

Push-Location $repositoryRoot
$workDirectory = $null
try {
    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $appProject -PathType Leaf) -or
        -not (Test-Path -LiteralPath $updaterProject -PathType Leaf)) {
        throw '发布所需的解决方案、应用项目或更新器项目不存在。'
    }

    if ([string]::IsNullOrWhiteSpace($Tag)) {
        $candidateTags = @(
            git tag --points-at HEAD |
                Where-Object { $_ -match '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' }
        )
        if ($LASTEXITCODE -ne 0 -or $candidateTags.Count -ne 1) {
            throw '未传入 -Tag，且当前提交没有且仅有一个 vX.Y.Z 标签。'
        }
        $Tag = $candidateTags[0]
    }

    $versionValue = ConvertTo-SemanticVersion $Tag
    if ($null -eq $versionValue -or -not $Tag.StartsWith('v', [StringComparison]::Ordinal)) {
        throw "标签 '$Tag' 不符合 v主版本.次版本.修订版本 格式。"
    }
    $versionText = $Tag.Substring(1)

    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Gitee 仓库路径 '$Repository' 无效，应为 owner/repository。"
    }
    if ($KeyId -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') {
        throw "keyId '$KeyId' 无效。"
    }
    if ($null -eq (ConvertTo-SemanticVersion $MinimumUpdaterVersion) -or
        $MinimumUpdaterVersion.StartsWith('v', [StringComparison]::Ordinal)) {
        throw "minimumUpdaterVersion '$MinimumUpdaterVersion' 必须是无 v 前缀的 X.Y.Z。"
    }

    if (-not $OutputDirectory.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布输出目录必须位于 $artifactsRoot 内。"
    }
    if ((Test-Path -LiteralPath $OutputDirectory) -and
        @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
        throw "发布输出目录非空，拒绝覆盖：$OutputDirectory"
    }
    if ($BridgeRepository) {
        if ($BridgeRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $BridgeRepository -ieq $Repository) {
            throw '桥接仓地址必须是不同于主发布仓的 owner/repository。'
        }
        if (-not $BridgeOutputDirectory.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $BridgeOutputDirectory -ieq $OutputDirectory -or
            $BridgeOutputDirectory.StartsWith($OutputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $OutputDirectory.StartsWith($BridgeOutputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw '桥接输出目录必须是 artifacts 下独立且不互相嵌套的目录。'
        }
        if ((Test-Path -LiteralPath $BridgeOutputDirectory) -and @(Get-ChildItem -LiteralPath $BridgeOutputDirectory -Force).Count -gt 0) {
            throw '桥接输出目录非空，拒绝覆盖。'
        }
    }

    if (-not $SkipCleanCheck) {
        $changes = @(git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0) {
            throw '无法读取 Git 工作区状态。'
        }
        if ($changes.Count -gt 0) {
            throw "发布必须从干净工作区运行。检测到以下变更：`n$($changes -join "`n")"
        }
    }

    $headCommit = Get-GitText -Arguments @('rev-parse', 'HEAD')
    $tagCommit = Get-GitText -Arguments @('rev-parse', "refs/tags/$Tag^{}")
    if ($headCommit -ne $tagCommit) {
        throw "标签 $Tag 未指向当前提交。"
    }

    $masterRef = $null
    foreach ($candidate in @('refs/remotes/origin/master', 'refs/heads/master')) {
        & git show-ref --verify --quiet $candidate
        if ($LASTEXITCODE -eq 0) {
            $masterRef = $candidate
            break
        }
    }
    if ($null -eq $masterRef) {
        throw '本地不存在 origin/master 或 master，无法验证标签提交属于主分支。'
    }
    & git merge-base --is-ancestor $tagCommit $masterRef
    if ($LASTEXITCODE -ne 0) {
        throw "标签 $Tag 的提交不在 $masterRef 历史中。"
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
    $configuredVersion = [string]$props.Project.PropertyGroup.VersionPrefix
    if ($configuredVersion -ne $versionText) {
        throw "标签版本 $versionText 与 Directory.Build.props 的 VersionPrefix '$configuredVersion' 不一致。"
    }
    # 避免把仍查询旧仓的旧源码标签发布到新仓，形成迁移死循环。
    $optionsSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools/FocusIR.Workbench/src/FocusIR.Workbench.Infrastructure/GiteeUpdateOptions.cs') -Raw
    $ownerMatch = [regex]::Match($optionsSource, 'public string Owner \{ get; init; \} = "([A-Za-z0-9_.-]+)";')
    $repoMatch = [regex]::Match($optionsSource, 'public string Repository \{ get; init; \} = "([A-Za-z0-9_.-]+)";')
    if (-not $ownerMatch.Success -or -not $repoMatch.Success -or
        "$($ownerMatch.Groups[1].Value)/$($repoMatch.Groups[1].Value)" -cne $Repository) {
        throw '源码中的默认升级查询仓与主发布仓不一致；不要用旧标签重建新仓基线。'
    }

    $changelog = Get-Content -LiteralPath $changelogPath -Raw -Encoding UTF8
    $releaseNotes = Get-ReleaseNotes -Changelog $changelog -Version $versionText

    if (-not $SkipRemoteReleaseCheck) {
        $stableReleases = @(Get-GiteeStableReleases -RepositoryPath $Repository)
        foreach ($release in $stableReleases) {
            if ([string]$release.tag_name -eq $Tag) {
                throw "Gitee 上已存在标签 $Tag 的 Release；正式发布禁止覆盖。"
            }

            $existingVersion = ConvertTo-SemanticVersion ([string]$release.tag_name)
            if ($null -ne $existingVersion -and $versionValue -le $existingVersion) {
                throw "待发布版本 $versionText 不高于已有稳定版 $existingVersion。"
            }
        }
        if ($BridgeRepository) {
            foreach ($release in @(Get-GiteeStableReleases -RepositoryPath $BridgeRepository)) {
                $existingVersion = ConvertTo-SemanticVersion ([string]$release.tag_name)
                if ([string]$release.tag_name -eq $Tag -or ($null -ne $existingVersion -and $versionValue -le $existingVersion)) {
                    throw '桥接版本必须高于旧升级源的全部稳定版，且同名版本不能已发布。'
                }
            }
        }
    }

    & (Join-Path $repositoryRoot 'eng/ci.ps1') -SkipCleanCheck
    if (-not $?) {
        throw 'CI 脚本执行失败。'
    }

    $workDirectory = Join-Path $artifactsRoot ('.release-work-' + [guid]::NewGuid().ToString('N'))
    $appPublishDirectory = Join-Path $workDirectory 'publish\app'
    $updaterPublishDirectory = Join-Path $workDirectory 'publish\updater'
    $packageDirectory = Join-Path $workDirectory 'package'
    $launcherDirectory = Join-Path $packageDirectory 'launcher'
    New-Item -ItemType Directory -Path $appPublishDirectory, $updaterPublishDirectory, $packageDirectory, $launcherDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

    foreach ($project in @($appProject, $updaterProject)) {
        Invoke-CheckedCommand -FilePath dotnet -Arguments @(
            'restore',
            $project,
            '--locked-mode',
            '-r', 'win-x64'
        )
    }

    $commonPublishProperties = @(
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '-p:PublishTrimmed=false',
        '-p:ContinuousIntegrationBuild=true',
        "-p:Version=$versionText",
        "-p:VersionPrefix=$versionText",
        "-p:AssemblyVersion=$versionText.0",
        "-p:FileVersion=$versionText.0",
        "-p:InformationalVersion=$versionText"
    )
    $appPublishProperties = $commonPublishProperties + @(
        '-p:PublishSingleFile=false',
        '-p:SatelliteResourceLanguages=zh-Hans',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    $updaterPublishProperties = $commonPublishProperties + @(
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    Invoke-CheckedCommand -FilePath dotnet -Arguments (@('publish', $appProject) + $appPublishProperties + @('-o', $appPublishDirectory))
    Invoke-CheckedCommand -FilePath dotnet -Arguments (@('publish', $updaterProject) + $updaterPublishProperties + @('-o', $updaterPublishDirectory))

    Assert-ManagedAssemblyVersion -AssemblyPath (Join-Path $appPublishDirectory 'FocusIR.Workbench.dll') -ExpectedVersion ([version]"$versionText.0")
    Assert-ExecutableProductVersion -ExecutablePath (Join-Path $updaterPublishDirectory 'FocusIR.Workbench.Updater.exe') -ExpectedVersion $versionText

    Copy-Item -Path (Join-Path $appPublishDirectory '*') -Destination $packageDirectory -Recurse -Force
    Copy-Item -Path (Join-Path $updaterPublishDirectory '*') -Destination $launcherDirectory -Recurse -Force

    $appExecutable = Join-Path $packageDirectory 'FocusIR.Workbench.exe'
    $updaterExecutable = Join-Path $launcherDirectory 'FocusIR.Workbench.Updater.exe'
    if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf) -or
        -not (Test-Path -LiteralPath $updaterExecutable -PathType Leaf)) {
        throw '发布目录结构无效：缺少应用入口或 launcher/ 下的更新器入口。'
    }

    $packageFileName = "FocusIR.Workbench-$Tag-win-x64.zip"
    $packagePath = Join-Path $OutputDirectory $packageFileName
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal

    $packageInfo = Get-Item -LiteralPath $packagePath
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $packageUrl = "https://gitee.com/$Repository/releases/download/$Tag/$packageFileName"
    $manifestPath = Join-Path $OutputDirectory 'update-manifest.json'
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $versionText
        tag = $Tag
        publishedAtUtc = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
        releaseNotes = $releaseNotes
        minimumUpdaterVersion = $MinimumUpdaterVersion
        keyId = $KeyId
        package = [ordered]@{
            rid = 'win-x64'
            fileName = $packageFileName
            url = $packageUrl
            size = $packageInfo.Length
            sha256 = $packageHash
        }
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8WithoutBom)
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)

    if ([string]::IsNullOrWhiteSpace($env:UPDATE_SIGNING_KEY_PKCS8)) {
        throw '缺少加密变量 UPDATE_SIGNING_KEY_PKCS8，不能签署升级清单。'
    }
    if (-not (Test-Path -LiteralPath $trustedPublicKeyPath -PathType Leaf)) {
        throw "缺少 keyId '$KeyId' 对应的受信公钥：$trustedPublicKeyPath"
    }

    $privateKey = Import-ReleasePrivateKey -PrivateKeyText $env:UPDATE_SIGNING_KEY_PKCS8
    try {
        $signature = $privateKey.SignData(
            $manifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
        )
    }
    finally {
        $privateKey.Dispose()
    }
    if ($signature.Length -ne 64) {
        throw "P-256 IEEE P1363 签名长度异常：$($signature.Length) 字节。"
    }

    $signaturePath = Join-Path $OutputDirectory 'update-manifest.sig'
    [IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature), $utf8WithoutBom)

    $publicKey = [Security.Cryptography.ECDsa]::Create()
    try {
        $publicKey.ImportFromPem((Get-Content -LiteralPath $trustedPublicKeyPath -Raw -Encoding UTF8))
        $signatureValid = $publicKey.VerifyData(
            $manifestBytes,
            $signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
        )
    }
    finally {
        $publicKey.Dispose()
    }
    if (-not $signatureValid) {
        throw "签名私钥与 keyId '$KeyId' 的已提交公钥不匹配。"
    }

    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $signatureHash = (Get-FileHash -LiteralPath $signaturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumsPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
    $checksums = @(
        "$packageHash  $packageFileName",
        "$manifestHash  update-manifest.json",
        "$signatureHash  update-manifest.sig"
    ) -join "`n"
    [IO.File]::WriteAllText($checksumsPath, "$checksums`n", $utf8WithoutBom)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($entryName in $entryNames) {
            if ($entryName.StartsWith('/', [StringComparison]::Ordinal) -or
                $entryName -match '(^|/)\.\.(/|$)' -or
                [IO.Path]::IsPathRooted($entryName)) {
                throw "ZIP 中发现不安全路径：$entryName"
            }
        }
        if ($entryNames -notcontains 'FocusIR.Workbench.exe' -or
            $entryNames -notcontains 'launcher/FocusIR.Workbench.Updater.exe') {
            throw 'ZIP 静态检查失败：入口文件布局不符合约定。'
        }
    }
    finally {
        $archive.Dispose()
    }

    $parsedManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($parsedManifest.schemaVersion -ne 1 -or
        $parsedManifest.version -ne $versionText -or
        $parsedManifest.tag -ne $Tag -or
        $parsedManifest.minimumUpdaterVersion -ne $MinimumUpdaterVersion -or
        $parsedManifest.package.rid -ne 'win-x64' -or
        $parsedManifest.package.fileName -ne $packageFileName -or
        $parsedManifest.package.url -ne $packageUrl -or
        [long]$parsedManifest.package.size -ne $packageInfo.Length -or
        $parsedManifest.package.sha256 -ne $packageHash) {
        throw '生成后的升级清单静态复核失败。'
    }

    $releaseFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -File)
    $expectedNames = @($packageFileName, 'update-manifest.json', 'update-manifest.sig', 'SHA256SUMS.txt')
    if ($releaseFiles.Count -ne 4 -or @($releaseFiles.Name | Where-Object { $_ -notin $expectedNames }).Count -ne 0) {
        throw '发布目录必须且只能包含约定的四个 Release 附件。'
    }

    if ($BridgeRepository) {
        $null = [IO.Directory]::CreateDirectory($BridgeOutputDirectory)
        # 原样复制同一个 ZIP；只改外部清单地址并重新签名，不重编译/重新压缩。
        Copy-Item -LiteralPath $packagePath -Destination (Join-Path $BridgeOutputDirectory $packageFileName)
        $bridgeManifest = $manifestJson | ConvertFrom-Json
        $bridgeManifest.package.url = "https://gitee.com/$BridgeRepository/releases/download/$Tag/$packageFileName"
        $bridgeManifestPath = Join-Path $BridgeOutputDirectory 'update-manifest.json'
        [IO.File]::WriteAllText($bridgeManifestPath, ($bridgeManifest | ConvertTo-Json -Depth 5), $utf8WithoutBom)
        $bridgeBytes = [IO.File]::ReadAllBytes($bridgeManifestPath)
        $bridgeKey = Import-ReleasePrivateKey -PrivateKeyText $env:UPDATE_SIGNING_KEY_PKCS8
        try {
            $bridgeSignature = $bridgeKey.SignData($bridgeBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        }
        finally { $bridgeKey.Dispose() }
        $bridgeSignaturePath = Join-Path $BridgeOutputDirectory 'update-manifest.sig'
        [IO.File]::WriteAllText($bridgeSignaturePath, [Convert]::ToBase64String($bridgeSignature), $utf8WithoutBom)
        $bridgeManifestHash = (Get-FileHash -LiteralPath $bridgeManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $bridgeSignatureHash = (Get-FileHash -LiteralPath $bridgeSignaturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $bridgeChecksums = @("$packageHash  $packageFileName", "$bridgeManifestHash  update-manifest.json", "$bridgeSignatureHash  update-manifest.sig") -join "`n"
        [IO.File]::WriteAllText((Join-Path $BridgeOutputDirectory 'SHA256SUMS.txt'), "$bridgeChecksums`n", $utf8WithoutBom)
        & (Join-Path $PSScriptRoot 'Publish-GiteeRelease.ps1') -Tag $Tag -Repository $BridgeRepository -Mode Validate -SourceDirectory $repositoryRoot -ArtifactDirectory $BridgeOutputDirectory
        if (-not $?) { throw '桥接制品验签或哈希检查失败。' }
        Write-Host "桥接制品校验通过（ZIP 与主发布包相同）：$BridgeOutputDirectory"
    }

    Write-Host "发布包已生成并通过静态校验：$OutputDirectory"
    $releaseFiles | Sort-Object Name | ForEach-Object { Write-Host " - $($_.Name)" }
}
finally {
    if ($null -ne $workDirectory -and (Test-Path -LiteralPath $workDirectory)) {
        $resolvedWork = [IO.Path]::GetFullPath($workDirectory)
        if ($resolvedWork.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedWork).StartsWith('.release-work-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force
        }
    }
    Pop-Location
}
