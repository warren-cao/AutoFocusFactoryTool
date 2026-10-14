using System.Text.Json;

namespace FocusIR.Workbench.Updater;

internal sealed record InstalledVersionState(string Version, DateTimeOffset InstalledAtUtc)
{
    public SemanticVersion ParsedVersion => SemanticVersion.Parse(Version);
}

internal sealed class InstallStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly InstallationLayout layout;
    private readonly IUpdaterLog log;

    public InstallStateStore(InstallationLayout layout, IUpdaterLog? log = null)
    {
        this.layout = layout;
        this.log = log ?? NullUpdaterLog.Instance;
    }

    public InstalledVersionState? ReadCurrent() => ReadState(layout.CurrentStatePath);

    public InstalledVersionState? ReadPrevious() => ReadState(layout.PreviousStatePath);

    public void Promote(SemanticVersion version)
    {
        layout.EnsureDirectories();
        InstalledVersionState? current = ReadCurrent();
        var next = new InstalledVersionState(version.ToString(), DateTimeOffset.UtcNow);

        if (current is not null && current.ParsedVersion != version)
        {
            WriteStateAtomically(layout.PreviousStatePath, current);
        }

        WriteStateAtomically(layout.CurrentStatePath, next);
        log.Write($"当前版本已切换为 {version}");
    }

    public InstalledVersionState Rollback()
    {
        InstalledVersionState current = ReadCurrent()
            ?? throw new InvalidOperationException("没有可回滚的当前版本。");
        InstalledVersionState previous = ReadPrevious()
            ?? throw new InvalidOperationException("没有可回滚的上一版本。");

        EnsureInstalled(previous);
        WriteStateAtomically(layout.CurrentStatePath, previous);
        WriteStateAtomically(layout.PreviousStatePath, current);
        log.Write($"已从 {current.Version} 回滚到 {previous.Version}");
        return previous;
    }

    public string ResolveApplicationPath(InstalledVersionState state)
    {
        string versionDirectory = layout.GetVersionDirectory(state.ParsedVersion);
        string applicationPath = Path.Combine(versionDirectory, UpdaterConstants.ApplicationExecutableName);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("已安装版本缺少应用入口。", applicationPath);
        }

        return applicationPath;
    }

    public void CleanupObsoleteVersions()
    {
        if (!Directory.Exists(layout.VersionsDirectory))
        {
            return;
        }

        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRetainedVersion(retained, ReadCurrent());
        AddRetainedVersion(retained, ReadPrevious());

        foreach (string directory in Directory.EnumerateDirectories(layout.VersionsDirectory))
        {
            string name = Path.GetFileName(directory);
            if (!SemanticVersion.TryParse(name, out _) || retained.Contains(name))
            {
                continue;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    log.Write($"跳过重解析点版本目录：{name}");
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                log.Write($"已清理旧版本：{name}");
            }
            catch (IOException exception)
            {
                log.Write($"旧版本 {name} 暂时无法清理，将在后续启动时重试", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                log.Write($"旧版本 {name} 无法清理，将在后续启动时重试", exception);
            }
        }
    }

    private static void AddRetainedVersion(HashSet<string> retained, InstalledVersionState? state)
    {
        if (state is not null && SemanticVersion.TryParse(state.Version, out SemanticVersion version))
        {
            retained.Add(version.ToString());
        }
    }

    private void EnsureInstalled(InstalledVersionState state)
    {
        _ = ResolveApplicationPath(state);
    }

    private InstalledVersionState? ReadState(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string json = File.ReadAllText(path);
        InstalledVersionState? state = JsonSerializer.Deserialize<InstalledVersionState>(json, JsonOptions);
        if (state is null || !SemanticVersion.TryParse(state.Version, out _))
        {
            throw new InvalidDataException($"安装状态文件无效：{Path.GetFileName(path)}");
        }

        return state;
    }

    private static void WriteStateAtomically(string path, InstalledVersionState state)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("状态文件没有父目录。");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
