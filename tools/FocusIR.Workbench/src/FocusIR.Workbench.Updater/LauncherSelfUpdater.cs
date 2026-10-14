using System.Diagnostics;

namespace FocusIR.Workbench.Updater;

internal sealed class LauncherSelfUpdater
{
    private readonly InstallationLayout layout;
    private readonly IUpdaterLog log;

    public LauncherSelfUpdater(InstallationLayout layout, IUpdaterLog? log = null)
    {
        this.layout = layout;
        this.log = log ?? NullUpdaterLog.Instance;
    }

    public void PrepareUpdate(string? launcherPayloadDirectory)
    {
        if (launcherPayloadDirectory is null)
        {
            return;
        }

        string payloadExecutable = Path.Combine(launcherPayloadDirectory, UpdaterConstants.UpdaterExecutableName);
        if (!File.Exists(payloadExecutable))
        {
            throw new InvalidDataException("启动器升级负载不完整。");
        }

        string pendingDirectory = Path.Combine(layout.RootDirectory, $".launcher-pending-{Guid.NewGuid():N}");
        CopyDirectory(launcherPayloadDirectory, pendingDirectory);

        string? currentExecutable = Environment.ProcessPath;
        bool runningFromManagedLauncher = currentExecutable is not null &&
            IsPathInside(currentExecutable, layout.LauncherDirectory);

        if (!runningFromManagedLauncher)
        {
            ReplaceDirectory(pendingDirectory, layout.LauncherDirectory);
            log.Write("启动器已安装到固定 launcher 目录。");
            return;
        }

        string runnerDirectory = Path.Combine(layout.RootDirectory, $".launcher-runner-{Guid.NewGuid():N}");
        CopyDirectory(pendingDirectory, runnerDirectory);
        string runnerExecutable = Path.Combine(runnerDirectory, UpdaterConstants.UpdaterExecutableName);
        var startInfo = new ProcessStartInfo(runnerExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = runnerDirectory,
        };
        startInfo.ArgumentList.Add("--replace-launcher");
        startInfo.ArgumentList.Add("--source-dir");
        startInfo.ArgumentList.Add(pendingDirectory);
        startInfo.ArgumentList.Add("--target-dir");
        startInfo.ArgumentList.Add(layout.LauncherDirectory);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--runner-dir");
        startInfo.ArgumentList.Add(runnerDirectory);
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(layout.RootDirectory);

        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动启动器自更新进程。");
        log.Write("已安排启动器在当前进程退出后进行自更新。");
    }

    public async Task ReplaceFromRunnerAsync(
        string sourceDirectory,
        string targetDirectory,
        string runnerDirectory,
        int parentProcessId,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateManagedTemporaryDirectory(sourceDirectory, ".launcher-pending-");
        string runner = ValidateManagedTemporaryDirectory(runnerDirectory, ".launcher-runner-");
        string target = Path.GetFullPath(targetDirectory);
        if (!string.Equals(target, Path.GetFullPath(layout.LauncherDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("启动器目标目录不在受管安装根目录中。");
        }

        string? processPath = Environment.ProcessPath;
        if (processPath is null || !IsPathInside(processPath, runner))
        {
            throw new InvalidOperationException("启动器替换命令只能从受管临时副本运行。");
        }

        await WaitForParentExitAsync(parentProcessId, cancellationToken).ConfigureAwait(false);
        ReplaceDirectory(source, target);
        log.Write("启动器自更新完成。临时运行目录将在下次启动时清理。");
    }

    public void CleanupTemporaryDirectories()
    {
        foreach (string directory in Directory.EnumerateDirectories(layout.RootDirectory, ".launcher-*"))
        {
            string? processPath = Environment.ProcessPath;
            if (processPath is not null && IsPathInside(processPath, directory))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException exception)
            {
                log.Write($"暂时无法清理启动器临时目录 {Path.GetFileName(directory)}", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                log.Write($"无权清理启动器临时目录 {Path.GetFileName(directory)}", exception);
            }
        }
    }

    internal static async Task WaitForParentExitAsync(int parentProcessId, CancellationToken cancellationToken)
    {
        if (parentProcessId <= 0 || parentProcessId == Environment.ProcessId)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        }

        Process? parent;
        try
        {
            parent = Process.GetProcessById(parentProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (parent)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(UpdaterConstants.ParentExitTimeout);
            await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    private string ValidateManagedTemporaryDirectory(string directory, string requiredPrefix)
    {
        string fullPath = Path.GetFullPath(directory);
        string parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("临时目录没有父目录。");
        string name = Path.GetFileName(fullPath);
        if (!string.Equals(parent, layout.RootDirectory, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("启动器临时目录不在受管安装根目录中。");
        }

        return fullPath;
    }

    private static void ReplaceDirectory(string sourceDirectory, string targetDirectory)
    {
        string? parent = Path.GetDirectoryName(targetDirectory);
        if (parent is null)
        {
            throw new InvalidOperationException("启动器目录没有父目录。");
        }

        Directory.CreateDirectory(parent);
        string backupDirectory = Path.Combine(parent, $".launcher-backup-{Guid.NewGuid():N}");
        bool targetMoved = false;
        try
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backupDirectory);
                targetMoved = true;
            }

            Directory.Move(sourceDirectory, targetDirectory);
            if (targetMoved)
            {
                try
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // 新启动器已原子切换成功；备份目录由下次托管启动清理。
                }
                catch (UnauthorizedAccessException)
                {
                    // 新启动器已原子切换成功；备份目录由下次托管启动清理。
                }
            }
        }
        catch
        {
            if (!Directory.Exists(targetDirectory) && targetMoved && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, targetDirectory);
            }

            throw;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        var source = new DirectoryInfo(sourceDirectory);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("启动器负载目录不得为重解析点。");
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (FileInfo file in source.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("启动器负载不得包含重解析点文件。");
            }

            file.CopyTo(Path.Combine(destinationDirectory, file.Name), overwrite: false);
        }

        foreach (DirectoryInfo directory in source.EnumerateDirectories())
        {
            CopyDirectory(directory.FullName, Path.Combine(destinationDirectory, directory.Name));
        }
    }

    private static bool IsPathInside(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(directory);
        string prefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
