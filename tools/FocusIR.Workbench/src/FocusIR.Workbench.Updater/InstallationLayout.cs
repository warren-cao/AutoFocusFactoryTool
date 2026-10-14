namespace FocusIR.Workbench.Updater;

internal sealed class InstallationLayout
{
    public InstallationLayout(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        VersionsDirectory = Path.Combine(RootDirectory, "versions");
        DownloadsDirectory = Path.Combine(RootDirectory, "downloads");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        LauncherDirectory = Path.Combine(RootDirectory, "launcher");
        CurrentStatePath = Path.Combine(RootDirectory, "current.json");
        PreviousStatePath = Path.Combine(RootDirectory, "previous.json");
    }

    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermEye",
        "FocusIRWorkbench");

    public string RootDirectory { get; }

    public string VersionsDirectory { get; }

    public string DownloadsDirectory { get; }

    public string LogsDirectory { get; }

    public string LauncherDirectory { get; }

    public string CurrentStatePath { get; }

    public string PreviousStatePath { get; }

    public string GetVersionDirectory(SemanticVersion version) => Path.Combine(VersionsDirectory, version.ToString());

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
