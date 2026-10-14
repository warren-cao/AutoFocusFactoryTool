using System.Globalization;

namespace FocusIR.Workbench.Updater;

internal enum UpdaterCommandKind
{
    Launch,
    InstallPackage,
    ApplyPackage,
    Rollback,
    ReplaceLauncher,
}

internal sealed record UpdaterCommand(
    UpdaterCommandKind Kind,
    string RootDirectory,
    string? PackagePath = null,
    SemanticVersion? Version = null,
    string? Sha256 = null,
    int? ParentProcessId = null,
    string? SourceDirectory = null,
    string? TargetDirectory = null,
    string? RunnerDirectory = null);

internal static class CommandLine
{
    public static UpdaterCommand Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            throw new ArgumentException("必须指定 --launch、--install-package、--apply-package 或 --rollback。");
        }

        UpdaterCommandKind kind = ParseCommand(arguments[0]);
        string? positionalPackagePath = null;
        int optionStartIndex = 1;
        if ((kind is UpdaterCommandKind.InstallPackage or UpdaterCommandKind.ApplyPackage) &&
            arguments.Count > 1 &&
            !arguments[1].StartsWith("--", StringComparison.Ordinal))
        {
            positionalPackagePath = arguments[1];
            optionStartIndex = 2;
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = optionStartIndex; index < arguments.Count; index += 2)
        {
            string name = arguments[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Count)
            {
                throw new ArgumentException($"命令行参数格式无效：{name}");
            }

            if (!options.TryAdd(name, arguments[index + 1]))
            {
                throw new ArgumentException($"命令行参数重复：{name}");
            }
        }

        string root = TakeOptional(options, "--root") ?? InstallationLayout.DefaultRootDirectory;
        UpdaterCommand command = kind switch
        {
            UpdaterCommandKind.Launch => new UpdaterCommand(kind, root),
            UpdaterCommandKind.Rollback => new UpdaterCommand(kind, root),
            UpdaterCommandKind.InstallPackage => ParsePackageCommand(
                kind,
                root,
                options,
                positionalPackagePath,
                requireParent: false),
            UpdaterCommandKind.ApplyPackage => ParsePackageCommand(
                kind,
                root,
                options,
                positionalPackagePath,
                requireParent: true),
            UpdaterCommandKind.ReplaceLauncher => ParseReplaceLauncherCommand(root, options),
            _ => throw new ArgumentOutOfRangeException(nameof(arguments)),
        };

        if (options.Count != 0)
        {
            throw new ArgumentException($"存在无法识别的命令行参数：{string.Join(", ", options.Keys)}");
        }

        return command;
    }

    private static UpdaterCommandKind ParseCommand(string value) => value.ToLowerInvariant() switch
    {
        "--launch" => UpdaterCommandKind.Launch,
        "--install-package" => UpdaterCommandKind.InstallPackage,
        "--apply-package" => UpdaterCommandKind.ApplyPackage,
        "--rollback" => UpdaterCommandKind.Rollback,
        "--replace-launcher" => UpdaterCommandKind.ReplaceLauncher,
        _ => throw new ArgumentException($"无法识别的启动器命令：{value}"),
    };

    private static UpdaterCommand ParsePackageCommand(
        UpdaterCommandKind kind,
        string root,
        Dictionary<string, string> options,
        string? positionalPackagePath,
        bool requireParent)
    {
        string? namedPackagePath = TakeOptional(options, "--package");
        if (positionalPackagePath is not null && namedPackagePath is not null)
        {
            throw new ArgumentException("升级包路径不能同时使用位置参数和 --package 指定。");
        }

        string packagePath = positionalPackagePath ?? namedPackagePath
            ?? throw new ArgumentException("缺少升级包路径。");
        SemanticVersion version = SemanticVersion.Parse(TakeRequired(options, "--version"));
        string? sha256 = TakeOptional(options, "--sha256");
        if (requireParent && sha256 is null)
        {
            throw new ArgumentException("自动升级命令 --apply-package 必须提供 --sha256。");
        }

        int? parentProcessId = requireParent ? ParseProcessId(TakeRequired(options, "--parent-pid")) : null;
        return new UpdaterCommand(kind, root, packagePath, version, sha256, parentProcessId);
    }

    private static UpdaterCommand ParseReplaceLauncherCommand(
        string root,
        Dictionary<string, string> options)
    {
        string sourceDirectory = TakeRequired(options, "--source-dir");
        string targetDirectory = TakeRequired(options, "--target-dir");
        string runnerDirectory = TakeRequired(options, "--runner-dir");
        int parentProcessId = ParseProcessId(TakeRequired(options, "--parent-pid"));
        return new UpdaterCommand(
            UpdaterCommandKind.ReplaceLauncher,
            root,
            ParentProcessId: parentProcessId,
            SourceDirectory: sourceDirectory,
            TargetDirectory: targetDirectory,
            RunnerDirectory: runnerDirectory);
    }

    private static int ParseProcessId(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int processId) || processId <= 0)
        {
            throw new ArgumentException($"进程 ID 无效：{value}");
        }

        return processId;
    }

    private static string TakeRequired(Dictionary<string, string> options, string name) =>
        TakeOptional(options, name) ?? throw new ArgumentException($"缺少必需参数：{name}");

    private static string? TakeOptional(Dictionary<string, string> options, string name)
    {
        if (!options.Remove(name, out string? value))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"参数值不能为空：{name}");
        }

        return value;
    }
}
