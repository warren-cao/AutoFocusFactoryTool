using System.IO.Compression;
using System.Security.Cryptography;

namespace FocusIR.Workbench.Updater;

internal sealed record PackageInstallResult(SemanticVersion Version, string VersionDirectory, string? LauncherPayloadDirectory);

internal sealed class PackageInstaller
{
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumEntryCount = 100_000;
    private const long DiskSpaceMarginBytes = 64L * 1024 * 1024;

    private readonly InstallationLayout layout;
    private readonly InstallStateStore stateStore;
    private readonly IUpdaterLog log;

    public PackageInstaller(InstallationLayout layout, InstallStateStore stateStore, IUpdaterLog? log = null)
    {
        this.layout = layout;
        this.stateStore = stateStore;
        this.log = log ?? NullUpdaterLog.Instance;
    }

    public async Task<PackageInstallResult> InstallAsync(
        string packagePath,
        SemanticVersion version,
        string? expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        string fullPackagePath = Path.GetFullPath(packagePath);
        var packageFile = new FileInfo(fullPackagePath);
        if (!packageFile.Exists)
        {
            throw new FileNotFoundException("升级包不存在。", fullPackagePath);
        }

        await using var packageStream = new FileStream(
            fullPackagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous);
        if (packageStream.Length <= 0 || packageStream.Length > MaximumPackageBytes)
        {
            throw new InvalidDataException("升级包大小无效或超过 2 GiB 限制。");
        }

        if (expectedSha256 is not null)
        {
            ValidateSha256(expectedSha256);
            await VerifyPackageHashAsync(packageStream, expectedSha256, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log.Write("本次安装未提供 SHA-256；仅适用于人工基线安装。");
        }

        layout.EnsureDirectories();
        InstalledVersionState? current = stateStore.ReadCurrent();
        if (current is not null && version.CompareTo(current.ParsedVersion) <= 0)
        {
            throw new InvalidOperationException(
                $"拒绝从 {current.Version} 安装到非更高版本 {version}；降级只能通过回滚命令执行。");
        }

        string targetDirectory = layout.GetVersionDirectory(version);
        if (Directory.Exists(targetDirectory))
        {
            throw new IOException($"版本 {version} 已经安装。拒绝覆盖现有版本目录。");
        }

        string stagingDirectory = Path.Combine(layout.VersionsDirectory, $".staging-{version}-{Guid.NewGuid():N}");
        try
        {
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            IReadOnlyList<ValidatedArchiveEntry> entries = ValidateArchive(archive, stagingDirectory, out long expandedBytes);
            EnsureDiskSpace(expandedBytes);

            Directory.CreateDirectory(stagingDirectory);
            foreach (ValidatedArchiveEntry validatedEntry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (validatedEntry.IsDirectory)
                {
                    Directory.CreateDirectory(validatedEntry.DestinationPath);
                    continue;
                }

                string? parentDirectory = Path.GetDirectoryName(validatedEntry.DestinationPath);
                if (parentDirectory is not null)
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                await using Stream source = validatedEntry.Entry.Open();
                await using var destination = new FileStream(
                    validatedEntry.DestinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            string applicationPath = Path.Combine(stagingDirectory, UpdaterConstants.ApplicationExecutableName);
            if (!File.Exists(applicationPath))
            {
                throw new InvalidDataException($"升级包根目录缺少 {UpdaterConstants.ApplicationExecutableName}。");
            }

            string launcherPayloadDirectory = Path.Combine(stagingDirectory, "launcher");
            string? launcherPayload = null;
            if (Directory.Exists(launcherPayloadDirectory))
            {
                string updaterPath = Path.Combine(launcherPayloadDirectory, UpdaterConstants.UpdaterExecutableName);
                if (!File.Exists(updaterPath))
                {
                    throw new InvalidDataException($"launcher 目录缺少 {UpdaterConstants.UpdaterExecutableName}。");
                }

                launcherPayload = Path.Combine(targetDirectory, "launcher");
            }

            Directory.Move(stagingDirectory, targetDirectory);
            stateStore.Promote(version);
            stateStore.CleanupObsoleteVersions();
            log.Write($"升级包已安装：{version}");
            return new PackageInstallResult(version, targetDirectory, launcherPayload);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    private static void ValidateSha256(string expectedSha256)
    {
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 必须为 64 位十六进制字符串。", nameof(expectedSha256));
        }
    }

    private static async Task VerifyPackageHashAsync(
        Stream packageStream,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        packageStream.Position = 0;
        byte[] actualHash = await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false);
        byte[] expectedHash = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException("升级包 SHA-256 校验失败。");
        }

        packageStream.Position = 0;
    }

    private static IReadOnlyList<ValidatedArchiveEntry> ValidateArchive(
        ZipArchive archive,
        string stagingDirectory,
        out long expandedBytes)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("升级包文件数量无效或超过限制。");
        }

        string rootPath = Path.GetFullPath(stagingDirectory);
        string rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validatedEntries = new List<ValidatedArchiveEntry>(archive.Entries.Count);
        expandedBytes = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string entryName = entry.FullName.Replace('\\', '/');
            bool isDirectory = entryName.EndsWith("/", StringComparison.Ordinal);
            string trimmedName = isDirectory ? entryName.TrimEnd('/') : entryName;

            if (string.IsNullOrWhiteSpace(trimmedName) ||
                entryName.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(entryName) ||
                entryName.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"升级包包含非法路径：{entry.FullName}");
            }

            string[] segments = trimmedName.Split('/');
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            {
                throw new InvalidDataException($"升级包包含路径穿越或非法路径段：{entry.FullName}");
            }

            int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
            {
                throw new InvalidDataException($"升级包不得包含符号链接：{entry.FullName}");
            }

            string destinationPath = Path.GetFullPath(Path.Combine(
                rootPath,
                trimmedName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !destinations.Add(destinationPath))
            {
                throw new InvalidDataException($"升级包包含越界或重复路径：{entry.FullName}");
            }

            if (!isDirectory)
            {
                try
                {
                    expandedBytes = checked(expandedBytes + entry.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("升级包解压后大小溢出。", exception);
                }

                if (expandedBytes > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("升级包解压后大小超过 4 GiB 限制。");
                }
            }

            validatedEntries.Add(new ValidatedArchiveEntry(entry, destinationPath, isDirectory));
        }

        return validatedEntries;
    }

    private void EnsureDiskSpace(long expandedBytes)
    {
        string? pathRoot = Path.GetPathRoot(layout.RootDirectory);
        if (pathRoot is null)
        {
            throw new IOException("无法确定安装盘符。");
        }

        var drive = new DriveInfo(pathRoot);
        if (drive.AvailableFreeSpace < expandedBytes + DiskSpaceMarginBytes)
        {
            throw new IOException("磁盘可用空间不足，无法安全解压升级包。");
        }
    }

    private sealed record ValidatedArchiveEntry(ZipArchiveEntry Entry, string DestinationPath, bool IsDirectory);
}
