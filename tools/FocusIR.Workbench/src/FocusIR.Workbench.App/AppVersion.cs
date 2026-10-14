using System.Reflection;

namespace FocusIR.Workbench.App;

internal static class AppVersion
{
    public static string Current { get; } = ResolveCurrentVersion();

    private static string ResolveCurrentVersion()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
