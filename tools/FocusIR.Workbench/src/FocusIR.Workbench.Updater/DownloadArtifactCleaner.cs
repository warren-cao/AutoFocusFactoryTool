namespace FocusIR.Workbench.Updater;

internal sealed class DownloadArtifactCleaner
{
    private readonly InstallationLayout layout;
    private readonly IUpdaterLog log;

    public DownloadArtifactCleaner(InstallationLayout layout, IUpdaterLog? log = null)
    {
        this.layout = layout;
        this.log = log ?? NullUpdaterLog.Instance;
    }

    public void CleanupFiles()
    {
        if (!Directory.Exists(layout.DownloadsDirectory))
            return;

        foreach (var filePath in Directory.EnumerateFiles(layout.DownloadsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var attributes = File.GetAttributes(filePath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    log.Write($"跳过下载目录中的重解析点：{Path.GetFileName(filePath)}");
                    continue;
                }

                File.Delete(filePath);
                log.Write($"已清理下载文件：{Path.GetFileName(filePath)}");
            }
            catch (IOException exception)
            {
                log.Write($"暂时无法清理下载文件 {Path.GetFileName(filePath)}", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                log.Write($"无权清理下载文件 {Path.GetFileName(filePath)}", exception);
            }
        }
    }
}
