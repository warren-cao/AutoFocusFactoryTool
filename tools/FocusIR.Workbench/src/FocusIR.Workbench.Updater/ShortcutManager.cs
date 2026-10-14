using System.Runtime.InteropServices;

namespace FocusIR.Workbench.Updater;

internal sealed record ShortcutDefinition(
    string ShortcutPath,
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string Description,
    string IconLocation);

internal sealed class ShortcutManager
{
    private const string ShortcutFileName = "FocusIR Workbench.lnk";
    private readonly InstallationLayout layout;
    private readonly IUpdaterLog log;
    private readonly string programsDirectory;

    public ShortcutManager(
        InstallationLayout layout,
        IUpdaterLog? log = null,
        string? programsDirectory = null)
    {
        this.layout = layout;
        this.log = log ?? NullUpdaterLog.Instance;
        this.programsDirectory = programsDirectory ?? ResolveProgramsDirectory();
    }

    public ShortcutDefinition BuildDefinition()
    {
        string targetPath = Path.Combine(layout.LauncherDirectory, UpdaterConstants.UpdaterExecutableName);
        return new ShortcutDefinition(
            Path.Combine(programsDirectory, "ThermEye", ShortcutFileName),
            targetPath,
            "--launch",
            layout.LauncherDirectory,
            "启动 FocusIR Workbench",
            $"{targetPath},0");
    }

    public bool CreateOrRepair()
    {
        ShortcutDefinition definition = BuildDefinition();
        if (!File.Exists(definition.TargetPath))
        {
            log.Write("未创建开始菜单快捷方式：固定 launcher 目录中没有启动器。");
            return false;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(definition.ShortcutPath)
                ?? throw new InvalidOperationException("快捷方式没有父目录。"));

            Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
                ?? throw new InvalidOperationException("系统不支持 WScript.Shell。");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法创建 WScript.Shell 实例。");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(definition.ShortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = definition.TargetPath;
            dynamicShortcut.Arguments = definition.Arguments;
            dynamicShortcut.WorkingDirectory = definition.WorkingDirectory;
            dynamicShortcut.Description = definition.Description;
            dynamicShortcut.IconLocation = definition.IconLocation;
            dynamicShortcut.Save();
            log.Write("开始菜单快捷方式已创建或修复。");
            return true;
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            log.Write("创建或修复开始菜单快捷方式失败；已安装版本不受影响", exception);
            return false;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    public bool IsCurrentProcessManagedLauncher()
    {
        string? processPath = Environment.ProcessPath;
        return processPath is not null && IsPathInside(processPath, layout.LauncherDirectory);
    }

    private static string ResolveProgramsDirectory()
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(programs))
        {
            return programs;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs");
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

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}
