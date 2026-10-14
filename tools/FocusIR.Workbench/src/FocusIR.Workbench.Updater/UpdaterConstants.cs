namespace FocusIR.Workbench.Updater;

internal static class UpdaterConstants
{
    public const string ApplicationExecutableName = "FocusIR.Workbench.exe";
    public const string UpdaterExecutableName = "FocusIR.Workbench.Updater.exe";
    public const string HealthPipeEnvironmentVariable = "FOCUSIR_UPDATE_HEALTH_PIPE";
    public const string HealthyMessage = "healthy";

    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(2);
}
