using System.IO;
using System.IO.Pipes;
using System.Text;

namespace FocusIR.Workbench.App;

internal static class UpdateHealthReporter
{
    private const string HealthPipeEnvironmentVariable = "FOCUSIR_UPDATE_HEALTH_PIPE";
    private static int _reported;

    public static async Task TryReportHealthyAsync()
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0)
            return;

        var pipeName = Environment.GetEnvironmentVariable(HealthPipeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pipeName)
            || pipeName.Length > 128
            || !pipeName.StartsWith("FocusIR.Workbench.Health.", StringComparison.Ordinal))
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync("healthy".AsMemory(), timeout.Token);
        }
        catch
        {
            // 健康上报只服务于升级回滚；失败不能阻止工作台正常启动。
        }
        finally
        {
            Environment.SetEnvironmentVariable(HealthPipeEnvironmentVariable, null);
        }
    }
}
