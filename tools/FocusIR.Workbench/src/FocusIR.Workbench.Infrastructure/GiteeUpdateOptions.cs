using System.Text.RegularExpressions;

namespace FocusIR.Workbench.Infrastructure;

public sealed class GiteeUpdateOptions
{
    private static readonly Regex RepositoryPartPattern = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    public string Owner { get; init; } = "warren-cao";
    public string Repository { get; init; } = "focusir-workbench-releases";
    public long MaxPackageBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxManifestBytes { get; init; } = 1024 * 1024;
    public int MaxSignatureBytes { get; init; } = 4096;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Owner) || !RepositoryPartPattern.IsMatch(Owner))
            throw new ArgumentException("Gitee 仓库所有者名称无效。", nameof(Owner));
        if (string.IsNullOrWhiteSpace(Repository) || !RepositoryPartPattern.IsMatch(Repository))
            throw new ArgumentException("Gitee 仓库名称无效。", nameof(Repository));
        if (MaxPackageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPackageBytes));
        if (MaxManifestBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxManifestBytes));
        if (MaxSignatureBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxSignatureBytes));
    }
}
