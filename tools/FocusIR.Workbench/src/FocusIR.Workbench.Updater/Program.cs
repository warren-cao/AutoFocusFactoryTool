using System.Security.Cryptography;
using System.Text;

namespace FocusIR.Workbench.Updater;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        UpdaterCommand? command = null;
        IUpdaterLog? log = null;
        try
        {
            command = CommandLine.Parse(args);
            var layout = new InstallationLayout(command.RootDirectory);
            layout.EnsureDirectories();
            log = new FileUpdaterLog(layout);

            using Mutex mutex = CreateInstallationMutex(layout.RootDirectory);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromMinutes(3));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("另一个升级进程长时间未结束。");
            }

            try
            {
                return await ExecuteAsync(command, layout, log).ConfigureAwait(false);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            log?.Write("升级器执行失败", exception);
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(
        UpdaterCommand command,
        InstallationLayout layout,
        IUpdaterLog log)
    {
        var stateStore = new InstallStateStore(layout, log);
        var launcher = new ApplicationLauncher(stateStore, log);
        var selfUpdater = new LauncherSelfUpdater(layout, log);
        var shortcutManager = new ShortcutManager(layout, log);
        var downloadCleaner = new DownloadArtifactCleaner(layout, log);

        switch (command.Kind)
        {
            case UpdaterCommandKind.Launch:
                selfUpdater.CleanupTemporaryDirectories();
                if (shortcutManager.IsCurrentProcessManagedLauncher())
                {
                    _ = shortcutManager.CreateOrRepair();
                }

                ApplicationLaunchResult launchResult = await launcher
                    .LaunchCurrentAsync(rollbackOnFailure: true)
                    .ConfigureAwait(false);
                return launchResult is ApplicationLaunchResult.Healthy or ApplicationLaunchResult.RolledBack ? 0 : 2;

            case UpdaterCommandKind.Rollback:
                selfUpdater.CleanupTemporaryDirectories();
                _ = stateStore.Rollback();
                var rollbackResult = await launcher.LaunchCurrentAsync(rollbackOnFailure: false).ConfigureAwait(false);
                return rollbackResult == ApplicationLaunchResult.Healthy ? 0 : 2;

            case UpdaterCommandKind.InstallPackage:
                return await InstallAndLaunchAsync(
                        command,
                        layout,
                        stateStore,
                        launcher,
                        selfUpdater,
                        shortcutManager,
                        downloadCleaner,
                        log)
                    .ConfigureAwait(false);

            case UpdaterCommandKind.ApplyPackage:
                await LauncherSelfUpdater.WaitForParentExitAsync(command.ParentProcessId!.Value, CancellationToken.None)
                    .ConfigureAwait(false);
                return await InstallAndLaunchAsync(
                        command,
                        layout,
                        stateStore,
                        launcher,
                        selfUpdater,
                        shortcutManager,
                        downloadCleaner,
                        log)
                    .ConfigureAwait(false);

            case UpdaterCommandKind.ReplaceLauncher:
                await selfUpdater.ReplaceFromRunnerAsync(
                        command.SourceDirectory!,
                        command.TargetDirectory!,
                        command.RunnerDirectory!,
                        command.ParentProcessId!.Value)
                    .ConfigureAwait(false);
                return 0;

            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static async Task<int> InstallAndLaunchAsync(
        UpdaterCommand command,
        InstallationLayout layout,
        InstallStateStore stateStore,
        ApplicationLauncher launcher,
        LauncherSelfUpdater selfUpdater,
        ShortcutManager shortcutManager,
        DownloadArtifactCleaner downloadCleaner,
        IUpdaterLog log)
    {
        selfUpdater.CleanupTemporaryDirectories();
        var installer = new PackageInstaller(layout, stateStore, log);
        PackageInstallResult result = await installer.InstallAsync(
                command.PackagePath!,
                command.Version!.Value,
                command.Sha256)
            .ConfigureAwait(false);
        ApplicationLaunchResult launchResult = await launcher
            .LaunchCurrentAsync(rollbackOnFailure: true)
            .ConfigureAwait(false);
        if (launchResult != ApplicationLaunchResult.Healthy)
        {
            return 2;
        }

        // 只有新应用通过健康检查后才替换启动器，避免失败版本连同启动器一起生效。
        selfUpdater.PrepareUpdate(result.LauncherPayloadDirectory);
        _ = shortcutManager.CreateOrRepair();
        downloadCleaner.CleanupFiles();
        return 0;
    }

    private static Mutex CreateInstallationMutex(string rootDirectory)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(rootDirectory).ToUpperInvariant()));
        string suffix = Convert.ToHexString(hash.AsSpan(0, 12));
        return new Mutex(initiallyOwned: false, $"Local\\FocusIR.Workbench.Updater.{suffix}");
    }
}
