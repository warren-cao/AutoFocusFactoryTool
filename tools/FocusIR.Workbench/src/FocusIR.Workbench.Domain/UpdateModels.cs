using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FocusIR.Workbench.Domain;

/// <summary>仅表示正式发布使用的三段式语义版本，不接受预发布或构建元数据。</summary>
public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    public SemanticVersion(int major, int minor, int patch)
    {
        if (major < 0)
            throw new ArgumentOutOfRangeException(nameof(major));
        if (minor < 0)
            throw new ArgumentOutOfRangeException(nameof(minor));
        if (patch < 0)
            throw new ArgumentOutOfRangeException(nameof(patch));
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' 不是有效的三段式稳定版本号。");

    public static bool TryParse(string? value, out SemanticVersion version) =>
        TryParseCore(value, requireTagPrefix: false, out version);

    public static bool TryParseTag(string? value, out SemanticVersion version) =>
        TryParseCore(value, requireTagPrefix: true, out version);

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static bool TryParseCore(string? value, bool requireTagPrefix, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;

        var text = value;
        if (requireTagPrefix)
        {
            if (!text.StartsWith('v'))
                return false;
            text = text[1..];
        }

        var parts = text.Split('.');
        if (parts.Length != 3
            || !TryParsePart(parts[0], out var major)
            || !TryParsePart(parts[1], out var minor)
            || !TryParsePart(parts[2], out var patch))
            return false;

        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    private static bool TryParsePart(string value, out int result)
    {
        result = 0;
        return value.Length > 0
            && (value.Length == 1 || value[0] != '0')
            && value.All(static character => character is >= '0' and <= '9')
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }
}

public sealed record UpdatePackageManifest(
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record UpdateManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("publishedAtUtc")] DateTimeOffset PublishedAtUtc,
    [property: JsonPropertyName("releaseNotes")] string ReleaseNotes,
    [property: JsonPropertyName("minimumUpdaterVersion")] string MinimumUpdaterVersion,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("package")] UpdatePackageManifest Package)
{
    private static readonly Regex KeyIdPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    public SemanticVersion Validate()
    {
        if (SchemaVersion != 1)
            throw new UpdateValidationException($"不支持升级清单 schemaVersion={SchemaVersion}。");
        if (!SemanticVersion.TryParse(Version, out var version))
            throw new UpdateValidationException("升级清单 version 必须是三段式稳定版本号。");
        if (!SemanticVersion.TryParseTag(Tag, out var tagVersion) || tagVersion != version)
            throw new UpdateValidationException("升级清单 tag 必须为与 version 一致的 vX.Y.Z。");
        if (PublishedAtUtc == default || PublishedAtUtc.Offset != TimeSpan.Zero)
            throw new UpdateValidationException("升级清单 publishedAtUtc 必须是有效的 UTC 时间。");
        if (ReleaseNotes is null)
            throw new UpdateValidationException("升级清单缺少 releaseNotes。");
        if (!SemanticVersion.TryParse(MinimumUpdaterVersion, out _))
            throw new UpdateValidationException("升级清单 minimumUpdaterVersion 无效。");
        if (string.IsNullOrWhiteSpace(KeyId) || !KeyIdPattern.IsMatch(KeyId))
            throw new UpdateValidationException("升级清单 keyId 无效。");
        if (Package is null)
            throw new UpdateValidationException("升级清单缺少 package。");
        if (!string.Equals(Package.Rid, "win-x64", StringComparison.Ordinal))
            throw new UpdateValidationException("升级包 rid 必须是 win-x64。");

        var expectedFileName = $"FocusIR.Workbench-v{version}-win-x64.zip";
        if (!string.Equals(Package.FileName, expectedFileName, StringComparison.Ordinal))
            throw new UpdateValidationException($"升级包文件名必须是 {expectedFileName}。");
        if (!Uri.TryCreate(Package.Url, UriKind.Absolute, out _))
            throw new UpdateValidationException("升级包 url 必须是绝对地址。");
        if (Package.Size <= 0)
            throw new UpdateValidationException("升级包 size 必须大于零。");
        if (Package.Sha256 is null
            || Package.Sha256.Length != 64
            || !Package.Sha256.All(static character => char.IsAsciiHexDigit(character)))
            throw new UpdateValidationException("升级包 sha256 必须是 64 位十六进制摘要。");

        return version;
    }
}

public sealed class UpdateRelease
{
    public UpdateRelease(
        SemanticVersion version,
        string tag,
        DateTimeOffset publishedAtUtc,
        string releaseNotes,
        UpdateManifest manifest,
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> manifestSignature)
    {
        Version = version;
        Tag = tag;
        PublishedAtUtc = publishedAtUtc;
        ReleaseNotes = releaseNotes;
        Manifest = manifest;
        ManifestBytes = manifestBytes.ToArray();
        ManifestSignature = manifestSignature.ToArray();
    }

    public SemanticVersion Version { get; }
    public string Tag { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public string ReleaseNotes { get; }
    public UpdateManifest Manifest { get; }
    public ReadOnlyMemory<byte> ManifestBytes { get; }
    public ReadOnlyMemory<byte> ManifestSignature { get; }
}

public enum UpdateCheckStatus
{
    UpToDate,
    NoStableRelease,
    UpdateAvailable
}

public sealed record UpdateCheckResult(
    SemanticVersion CurrentVersion,
    UpdateCheckStatus Status,
    UpdateRelease? Release)
{
    public bool IsUpdateAvailable => Status == UpdateCheckStatus.UpdateAvailable && Release is not null;
}

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double? Percentage => TotalBytes > 0 ? (double)BytesReceived / TotalBytes * 100d : null;
}

public sealed record UpdateDownloadResult(UpdateRelease Release, string PackagePath, long Size, string Sha256)
{
    public SemanticVersion Version => Release.Version;
}

public sealed class UpdateValidationException(string message, Exception? innerException = null)
    : IOException(message, innerException);
