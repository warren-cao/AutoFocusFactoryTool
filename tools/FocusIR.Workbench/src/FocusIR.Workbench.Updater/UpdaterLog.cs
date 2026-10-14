using System.Globalization;

namespace FocusIR.Workbench.Updater;

internal interface IUpdaterLog
{
    void Write(string message);

    void Write(string message, Exception exception);
}

internal sealed class FileUpdaterLog : IUpdaterLog
{
    private readonly object syncRoot = new();
    private readonly string logPath;

    public FileUpdaterLog(InstallationLayout layout)
    {
        Directory.CreateDirectory(layout.LogsDirectory);
        logPath = Path.Combine(layout.LogsDirectory, "updater.log");
    }

    public void Write(string message) => Append(message);

    public void Write(string message, Exception exception) => Append($"{message}：{exception.GetType().Name} - {exception.Message}");

    private void Append(string message)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");

        lock (syncRoot)
        {
            File.AppendAllText(logPath, line);
        }
    }
}

internal sealed class NullUpdaterLog : IUpdaterLog
{
    public static readonly NullUpdaterLog Instance = new();

    private NullUpdaterLog()
    {
    }

    public void Write(string message)
    {
    }

    public void Write(string message, Exception exception)
    {
    }
}
