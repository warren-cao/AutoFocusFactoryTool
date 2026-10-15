using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusIR.Workbench.Application;
using FocusIR.Workbench.Domain;

namespace FocusIR.Workbench.Infrastructure;

public sealed class GiteeUpdateService : IUpdateService
{
    private const string ManifestFileName = "update-manifest.json";
    private const string SignatureFileName = "update-manifest.sig";
    private const string GiteeHost = "gitee.com";
    private const string GiteeDownloadHost = "foruda.gitee.com";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, string> _trustedPublicKeys;
    private readonly GiteeUpdateOptions _options;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public GiteeUpdateService(
        HttpClient httpClient,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        GiteeUpdateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);

        _httpClient = httpClient;
        _trustedPublicKeys = new Dictionary<string, string>(trustedPublicKeys, StringComparer.Ordinal);
        _options = options ?? new GiteeUpdateOptions();
        _options.Validate();
        if (_trustedPublicKeys.Count == 0)
            throw new ArgumentException("至少需要配置一个受信任的升级签名公钥。", nameof(trustedPublicKeys));
        if (_trustedPublicKeys.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new ArgumentException("受信任公钥的 keyId 和 PEM 内容均不能为空。", nameof(trustedPublicKeys));
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        SemanticVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        // 两个固定文件可能在连续请求间更新；只重读一次，仍必须通过验签。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var manifestResponse = await SendGetAsync(BuildFeedUri(ManifestFileName), cancellationToken)
                .ConfigureAwait(false);
            if (manifestResponse.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(currentVersion, UpdateCheckStatus.NoStableRelease, null);
            EnsureSuccessfulResponse(manifestResponse);
            var manifestBytes = await ReadBoundedResponseAsync(manifestResponse, _options.MaxManifestBytes, cancellationToken)
                .ConfigureAwait(false);
            var signatureFileBytes = await GetBoundedBytesAsync(BuildFeedUri(SignatureFileName), _options.MaxSignatureBytes, cancellationToken)
                .ConfigureAwait(false);
            var manifest = DeserializeManifest(manifestBytes);
            var manifestVersion = manifest.Validate();
            var signature = DecodeSignature(signatureFileBytes);
            try
            {
                VerifyManifestSignature(manifest, manifestBytes, signature);
            }
            catch (UpdateValidationException) when (attempt == 0)
            {
                continue;
            }

            EnsurePackageUri(manifest);
            if (manifest.Package.Size > _options.MaxPackageBytes)
                throw new UpdateValidationException($"升级包超过允许的最大大小 {_options.MaxPackageBytes} 字节。");
            if (manifestVersion <= currentVersion)
                return new UpdateCheckResult(currentVersion, UpdateCheckStatus.UpToDate, null);

            var release = new UpdateRelease(
                manifestVersion,
                manifest.Tag,
                manifest.PublishedAtUtc,
                manifest.ReleaseNotes,
                manifest,
                manifestBytes,
                signature);
            return new UpdateCheckResult(currentVersion, UpdateCheckStatus.UpdateAvailable, release);
        }
        throw new UpdateValidationException("升级入口的清单与签名不一致，请稍后重新检查。");
    }

    public async Task<UpdateDownloadResult> DownloadUpdateAsync(
        UpdateRelease release,
        string destinationDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifestVersion = release.Manifest.Validate();
            if (manifestVersion != release.Version
                || !string.Equals(release.Manifest.Tag, release.Tag, StringComparison.Ordinal))
                throw new UpdateValidationException("待下载 Release 与升级清单版本不一致。");
            VerifyManifestSignature(release.Manifest, release.ManifestBytes.Span, release.ManifestSignature.Span);

            var package = release.Manifest.Package;
            var packageUri = new Uri(package.Url, UriKind.Absolute);
            EnsurePackageUri(release.Manifest);
            if (package.Size > _options.MaxPackageBytes)
                throw new UpdateValidationException($"升级包超过允许的最大大小 {_options.MaxPackageBytes} 字节。");
            EnsureSafeFileName(package.FileName);

            var destinationRoot = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destinationRoot);
            var packagePath = Path.Combine(destinationRoot, package.FileName);
            var partialPath = packagePath + ".partial";

            if (File.Exists(packagePath))
            {
                var existingResult = await TryValidateExistingPackageAsync(packagePath, release, cancellationToken)
                    .ConfigureAwait(false);
                if (existingResult is not null)
                    return existingResult;
                File.Delete(packagePath);
            }
            if (File.Exists(partialPath))
                File.Delete(partialPath);

            try
            {
                using var response = await SendGetAsync(packageUri, cancellationToken).ConfigureAwait(false);
                EnsureSuccessfulResponse(response);
                if (response.Content.Headers.ContentLength is { } contentLength
                    && contentLength != package.Size)
                    throw new UpdateValidationException("服务器返回的升级包长度与清单不一致。");

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                long total = 0;
                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                            break;
                        total += read;
                        if (total > package.Size || total > _options.MaxPackageBytes)
                            throw new UpdateValidationException("实际下载的升级包大于清单声明值或安全上限。");

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        progress?.Report(new UpdateDownloadProgress(total, package.Size));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (total != package.Size)
                    throw new UpdateValidationException("实际下载的升级包长度与清单不一致。");

                var actualHash = hash.GetHashAndReset();
                EnsureHashMatches(actualHash, package.Sha256);
                destination.Close();
                File.Move(partialPath, packagePath, overwrite: false);
                return new UpdateDownloadResult(release, packagePath, total, Convert.ToHexString(actualHash).ToLowerInvariant());
            }
            catch
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
                throw;
            }
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task<UpdateDownloadResult?> TryValidateExistingPackageAsync(
        string packagePath,
        UpdateRelease release,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(packagePath);
        if (file.Length != release.Manifest.Package.Size)
            return null;

        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!HashMatches(hash, release.Manifest.Package.Sha256))
            return null;

        return new UpdateDownloadResult(
            release,
            packagePath,
            file.Length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private async Task<byte[]> GetBoundedBytesAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendGetAsync(uri, cancellationToken).ConfigureAwait(false);
        EnsureSuccessfulResponse(response);
        return await ReadBoundedResponseAsync(response, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGetAsync(Uri uri, CancellationToken cancellationToken)
    {
        EnsureGiteeEntryUri(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FocusIR.Workbench", "1.0"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureTrustedResponseUri(response.RequestMessage?.RequestUri ?? uri);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new UpdateValidationException($"服务器响应超过允许的最大大小 {maximumBytes} 字节。");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
                if (total > maximumBytes)
                    throw new UpdateValidationException($"服务器响应超过允许的最大大小 {maximumBytes} 字节。");
                destination.Write(buffer, 0, read);
            }
            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void VerifyManifestSignature(
        UpdateManifest manifest,
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signature)
    {
        if (!_trustedPublicKeys.TryGetValue(manifest.KeyId, out var publicKeyPem))
            throw new UpdateValidationException($"升级清单使用了不受信任的签名密钥 {manifest.KeyId}。");

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportFromPem(publicKeyPem);
            if (verifier.KeySize != 256)
                throw new UpdateValidationException("升级签名公钥必须使用 ECDSA P-256。");
            if (!verifier.VerifyData(
                    manifestBytes,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new UpdateValidationException("升级清单签名校验失败。");
        }
        catch (UpdateValidationException)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw new UpdateValidationException("升级签名或受信任公钥格式无效。", exception);
        }
    }

    private static UpdateManifest DeserializeManifest(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(bytes, ManifestJsonOptions)
                ?? throw new UpdateValidationException("升级清单内容为空。");
        }
        catch (JsonException exception)
        {
            throw new UpdateValidationException("升级清单不是有效的 JSON。", exception);
        }
    }

    private static byte[] DecodeSignature(ReadOnlySpan<byte> signatureFileBytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(signatureFileBytes).Trim();
            var signature = Convert.FromBase64String(text);
            if (signature.Length != 64)
                throw new UpdateValidationException("ECDSA P-256 签名必须是 64 字节 IEEE P1363 数据。");
            return signature;
        }
        catch (FormatException exception)
        {
            throw new UpdateValidationException("update-manifest.sig 不是有效的 Base64 文本。", exception);
        }
    }

    private Uri BuildFeedUri(string fileName) =>
        new($"https://gitee.com/{_options.Owner}/{_options.Repository}/raw/master/latest/{fileName}", UriKind.Absolute);

    private void EnsurePackageUri(UpdateManifest manifest)
    {
        var uri = new Uri(manifest.Package.Url, UriKind.Absolute);
        EnsureGiteeEntryUri(uri);
        var expected = $"https://gitee.com/{_options.Owner}/{_options.Repository}/releases/download/{manifest.Tag}/{manifest.Package.FileName}";
        if (!string.Equals(uri.AbsoluteUri, expected, StringComparison.Ordinal))
            throw new UpdateValidationException("升级包地址必须指向当前 Gitee 发布仓的对应版本附件。");
    }

    private static void EnsureSuccessfulResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("Gitee 拒绝访问公开升级文件（HTTP 403），请稍后重试或检查当前网络。", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    private static void EnsureGiteeEntryUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.IdnHost, GiteeHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new UpdateValidationException("升级资源入口只允许使用 gitee.com 的标准 HTTPS 地址。");
    }

    private static void EnsureTrustedResponseUri(Uri uri)
    {
        var trustedHost = string.Equals(uri.IdnHost, GiteeHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.IdnHost, GiteeDownloadHost, StringComparison.OrdinalIgnoreCase);
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !trustedHost
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new UpdateValidationException("Gitee 将升级资源重定向到了不受信任的地址。");
    }

    private static void EnsureSafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UpdateValidationException("升级包文件名不安全。");
    }

    private static void EnsureHashMatches(ReadOnlySpan<byte> actualHash, string expectedHex)
    {
        if (!HashMatches(actualHash, expectedHex))
            throw new UpdateValidationException("升级包 SHA-256 校验失败。");
    }

    private static bool HashMatches(ReadOnlySpan<byte> actualHash, string expectedHex)
    {
        try
        {
            var expectedHash = Convert.FromHexString(expectedHex);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

}
