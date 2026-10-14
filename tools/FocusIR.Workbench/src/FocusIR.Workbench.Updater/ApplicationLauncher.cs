using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace FocusIR.Workbench.Updater;

internal enum ApplicationLaunchResult
{
    Healthy,
    RolledBack,
    Failed,
}

internal sealed class ApplicationLauncher
{
    private readonly InstallStateStore stateStore;
    private readonly IUpdaterLog log;
    private readonly TimeSpan healthTimeout;

    public ApplicationLauncher(
        InstallStateStore stateStore,
        IUpdaterLog? log = null,
        TimeSpan? healthTimeout = null)
    {
        this.stateStore = stateStore;
        this.log = log ?? NullUpdaterLog.Instance;
        this.healthTimeout = healthTimeout ?? UpdaterConstants.HealthTimeout;
    }

    public async Task<ApplicationLaunchResult> LaunchCurrentAsync(
        bool rollbackOnFailure,
        CancellationToken cancellationToken = default)
    {
        InstalledVersionState current = stateStore.ReadCurrent()
            ?? throw new InvalidOperationException("尚未安装可启动的版本。");

        bool healthy = await LaunchAndWaitForHealthAsync(current, cancellationToken).ConfigureAwait(false);
        if (healthy)
        {
            stateStore.CleanupObsoleteVersions();
            return ApplicationLaunchResult.Healthy;
        }

        if (!rollbackOnFailure)
        {
            return ApplicationLaunchResult.Failed;
        }

        log.Write($"版本 {current.Version} 未通过启动健康检查，开始自动回滚。");
        InstalledVersionState rolledBack = stateStore.Rollback();
        bool rollbackHealthy = await LaunchAndWaitForHealthAsync(rolledBack, cancellationToken).ConfigureAwait(false);
        return rollbackHealthy ? ApplicationLaunchResult.RolledBack : ApplicationLaunchResult.Failed;
    }

    private async Task<bool> LaunchAndWaitForHealthAsync(
        InstalledVersionState state,
        CancellationToken cancellationToken)
    {
        string applicationPath = stateStore.ResolveApplicationPath(state);
        string pipeName = $"FocusIR.Workbench.Health.{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var startInfo = new ProcessStartInfo(applicationPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(applicationPath)
                ?? throw new InvalidOperationException("应用入口没有父目录。"),
        };
        startInfo.Environment[UpdaterConstants.HealthPipeEnvironmentVariable] = pipeName;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 FocusIR Workbench。");
        log.Write($"已启动版本 {state.Version}，等待健康信号。");

        bool healthy = await WaitForHealthSignalAsync(pipe, process, cancellationToken).ConfigureAwait(false);
        if (healthy)
        {
            log.Write($"版本 {state.Version} 已报告健康状态。");
            return true;
        }

        log.Write($"版本 {state.Version} 未在限定时间内报告健康状态。");
        TryTerminate(process);
        return false;
    }

    private async Task<bool> WaitForHealthSignalAsync(
        NamedPipeServerStream pipe,
        Process process,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(healthTimeout);

        try
        {
            Task connectionTask = pipe.WaitForConnectionAsync(timeout.Token);
            Task processExitTask = process.WaitForExitAsync(CancellationToken.None);
            Task completed = await Task.WhenAny(connectionTask, processExitTask).ConfigureAwait(false);
            if (completed != connectionTask)
            {
                return false;
            }

            await connectionTask.ConfigureAwait(false);
            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 256,
                leaveOpen: true);
            Task<string?> readTask = reader.ReadLineAsync(timeout.Token).AsTask();
            completed = await Task.WhenAny(readTask, processExitTask).ConfigureAwait(false);
            if (completed != readTask)
            {
                return false;
            }

            string? message = await readTask.ConfigureAwait(false);
            return string.Equals(message?.Trim(), UpdaterConstants.HealthyMessage, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
