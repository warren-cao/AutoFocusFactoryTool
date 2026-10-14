using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusIR.Workbench.Domain;
using FocusIR.Workbench.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FocusIR.Workbench.Update.Tests;

[TestClass]
public sealed class GiteeUpdateServiceTests
{
    [TestMethod]
    public async Task CheckForUpdateAsync_ReturnsVerifiedStableRelease()
    {
        using var fixture = new UpdateFixture();

        var result = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0));

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.IsNotNull(result.Release);
        Assert.AreEqual(new SemanticVersion(0, 2, 0), result.Release.Version);
        Assert.AreEqual("release-one", result.Release.ReleaseNotes);
        CollectionAssert.DoesNotContain(fixture.Handler.RequestedUris, fixture.PackageUri);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_DoesNotOfferSameOrOlderVersion()
    {
        using var fixture = new UpdateFixture();

        var result = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 2, 0));

        Assert.AreEqual(UpdateCheckStatus.UpToDate, result.Status);
        Assert.IsNull(result.Release);
        Assert.HasCount(1, fixture.Handler.RequestedUris);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_RejectsPrerelease()
    {
        using var fixture = new UpdateFixture(prerelease: true);

        var result = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0));

        Assert.AreEqual(UpdateCheckStatus.NoStableRelease, result.Status);
        Assert.IsNull(result.Release);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_AcceptsGiteeStringFalsePrerelease()
    {
        using var fixture = new UpdateFixture(prerelease: "false");

        var result = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0));

        Assert.IsTrue(result.IsUpdateAvailable);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_RejectsTamperedManifest()
    {
        using var fixture = new UpdateFixture(tamperManifestAfterSigning: true);

        await Assert.ThrowsExactlyAsync<UpdateValidationException>(
            () => fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0)));
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_RejectsUnknownKeyId()
    {
        using var fixture = new UpdateFixture(manifestTransform: manifest => manifest with { KeyId = "unknown-key" });

        await Assert.ThrowsExactlyAsync<UpdateValidationException>(
            () => fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0)));
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_RejectsWrongPublicKey()
    {
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fixture = new UpdateFixture(trustedPublicKeyOverride: wrongKey.ExportSubjectPublicKeyInfoPem());

        await Assert.ThrowsExactlyAsync<UpdateValidationException>(
            () => fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0)));
    }

    [TestMethod]
    [DataRow("http://gitee.com/warren-cao/package.zip")]
    [DataRow("https://download.gitee.com/warren-cao/package.zip")]
    [DataRow("https://gitee.com.evil.example/warren-cao/package.zip")]
    public async Task CheckForUpdateAsync_RejectsUntrustedPackageAddress(string packageUrl)
    {
        using var fixture = new UpdateFixture(
            manifestTransform: manifest => manifest with
            {
                Package = manifest.Package with { Url = packageUrl }
            });

        await Assert.ThrowsExactlyAsync<UpdateValidationException>(
            () => fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0)));
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_WritesVerifiedPackageAndReportsResult()
    {
        using var fixture = new UpdateFixture();
        var check = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0));
        var directory = CreateTemporaryDirectory();
        try
        {
            var result = await fixture.Service.DownloadUpdateAsync(check.Release!, directory);

            CollectionAssert.AreEqual(fixture.PackageBytes, await File.ReadAllBytesAsync(result.PackagePath));
            Assert.AreEqual(new SemanticVersion(0, 2, 0), result.Version);
            Assert.AreEqual(fixture.ExpectedSha256, result.Sha256);
            Assert.IsFalse(File.Exists(result.PackagePath + ".partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_AcceptsOfficialGiteeDownloadRedirect()
    {
        using var fixture = new UpdateFixture(
            packageResponseUri: "https://foruda.gitee.com/attach_file/123/package.zip?token=signed");
        var check = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0));
        var directory = CreateTemporaryDirectory();
        try
        {
            var result = await fixture.Service.DownloadUpdateAsync(check.Release!, directory);

            Assert.IsTrue(File.Exists(result.PackagePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_RejectsRedirectToUntrustedHost()
    {
        using var fixture = new UpdateFixture(packageResponseUri: "https://evil.example/package.zip");
        var check = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0));
        var directory = CreateTemporaryDirectory();
        try
        {
            await Assert.ThrowsExactlyAsync<UpdateValidationException>(
                () => fixture.Service.DownloadUpdateAsync(check.Release!, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_DeletesPartialFileWhenHashIsWrong()
    {
        using var fixture = new UpdateFixture(
            manifestTransform: manifest => manifest with
            {
                Package = manifest.Package with { Sha256 = new string('0', 64) }
            });
        var check = await fixture.Service.CheckForUpdateAsync(new SemanticVersion(0, 1, 0));
        var directory = CreateTemporaryDirectory();
        var expectedPath = Path.Combine(directory, check.Release!.Manifest.Package.FileName);
        try
        {
            await Assert.ThrowsExactlyAsync<UpdateValidationException>(
                () => fixture.Service.DownloadUpdateAsync(check.Release, directory));

            Assert.IsFalse(File.Exists(expectedPath));
            Assert.IsFalse(File.Exists(expectedPath + ".partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FocusIR.Update.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class UpdateFixture : IDisposable
    {
        private const string LatestUri =
            "https://gitee.com/api/v5/repos/warren-cao/auto-focus-factory-tool/releases/latest";
        private const string AssetsUri =
            "https://gitee.com/api/v5/repos/warren-cao/auto-focus-factory-tool/releases/42/attach_files?page=1&per_page=100&direction=asc";
        private const string ManifestUri =
            "https://gitee.com/warren-cao/auto-focus-factory-tool/releases/download/v0.2.0/update-manifest.json";
        private const string SignatureUri =
            "https://gitee.com/warren-cao/auto-focus-factory-tool/releases/download/v0.2.0/update-manifest.sig";
        private const string ChecksumsUri =
            "https://gitee.com/warren-cao/auto-focus-factory-tool/releases/download/v0.2.0/SHA256SUMS.txt";
        private const string DefaultPackageUri =
            "https://gitee.com/warren-cao/auto-focus-factory-tool/releases/download/v0.2.0/FocusIR.Workbench-v0.2.0-win-x64.zip";

        private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly HttpClient _httpClient;

        public UpdateFixture(
            Func<UpdateManifest, UpdateManifest>? manifestTransform = null,
            bool tamperManifestAfterSigning = false,
            string? trustedPublicKeyOverride = null,
            object? prerelease = null,
            string? packageResponseUri = null)
        {
            PackageBytes = Encoding.UTF8.GetBytes("package-data");
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(PackageBytes)).ToLowerInvariant();
            var manifest = UpdateManifestTests.CreateManifest() with
            {
                Package = UpdateManifestTests.CreateManifest().Package with
                {
                    Size = PackageBytes.Length,
                    Sha256 = ExpectedSha256
                }
            };
            if (manifestTransform is not null)
                manifest = manifestTransform(manifest);

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            var signature = _signingKey.SignData(
                manifestBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (tamperManifestAfterSigning)
            {
                var text = Encoding.UTF8.GetString(manifestBytes).Replace("release-one", "release-two", StringComparison.Ordinal);
                manifestBytes = Encoding.UTF8.GetBytes(text);
            }

            PackageUri = new Uri(manifest.Package.Url, UriKind.Absolute);
            var latestBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["id"] = 42,
                ["tag_name"] = "v0.2.0",
                ["prerelease"] = prerelease ?? false
            });
            var assetsBytes = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                Asset("update-manifest.json", ManifestUri, manifestBytes.Length),
                Asset("update-manifest.sig", SignatureUri, Convert.ToBase64String(signature).Length),
                Asset("SHA256SUMS.txt", ChecksumsUri, 128),
                Asset(manifest.Package.FileName, manifest.Package.Url, manifest.Package.Size)
            });

            Handler = new FakeHttpMessageHandler(new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.Ordinal)
            {
                [LatestUri] = () => Response(latestBytes, "application/json"),
                [AssetsUri] = () => Response(assetsBytes, "application/json"),
                [ManifestUri] = () => Response(manifestBytes, "application/json"),
                [SignatureUri] = () => Response(Encoding.UTF8.GetBytes(Convert.ToBase64String(signature)), "text/plain"),
                [manifest.Package.Url] = () => Response(PackageBytes, "application/zip", packageResponseUri)
            });
            _httpClient = new HttpClient(Handler);
            var trustedKey = trustedPublicKeyOverride ?? _signingKey.ExportSubjectPublicKeyInfoPem();
            Service = new GiteeUpdateService(
                _httpClient,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["focusir-release-2026"] = trustedKey
                });
        }

        public FakeHttpMessageHandler Handler { get; }
        public GiteeUpdateService Service { get; }
        public byte[] PackageBytes { get; }
        public string ExpectedSha256 { get; }
        public Uri PackageUri { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            _signingKey.Dispose();
        }

        private static Dictionary<string, object?> Asset(string name, string url, long size) => new()
        {
            ["name"] = name,
            ["browser_download_url"] = url,
            ["size"] = size
        };

        private static HttpResponseMessage Response(byte[] content, string mediaType, string? responseUri = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            if (responseUri is not null)
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, responseUri);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return response;
        }
    }

    internal sealed class FakeHttpMessageHandler(
        IReadOnlyDictionary<string, Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException("请求缺少 URI。");
            RequestedUris.Add(uri);
            if (!responses.TryGetValue(uri.AbsoluteUri, out var responseFactory))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            var response = responseFactory();
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
