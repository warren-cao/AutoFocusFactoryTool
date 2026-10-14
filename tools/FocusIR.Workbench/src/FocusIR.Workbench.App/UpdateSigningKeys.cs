using System.IO;
using System.Reflection;

namespace FocusIR.Workbench.App;

internal static class UpdateSigningKeys
{
    private const string ResourcePrefix = "FocusIR.UpdateKeys.";

    // eng/keys 下只保存可公开的验签公钥；私钥只保存到 Gitee 加密凭证。
    public static IReadOnlyDictionary<string, string> TrustedPublicKeys { get; } = LoadKeys();

    private static IReadOnlyDictionary<string, string> LoadKeys()
    {
        var assembly = typeof(UpdateSigningKeys).Assembly;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                    && name.EndsWith(".pem", StringComparison.Ordinal)))
        {
            var keyId = resourceName[ResourcePrefix.Length..^".pem".Length];
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"无法读取升级公钥资源 {resourceName}。");
            using var reader = new StreamReader(stream);
            keys.Add(keyId, reader.ReadToEnd());
        }

        if (keys.Count == 0)
            throw new InvalidOperationException("没有嵌入任何升级验签公钥。");
        return keys;
    }
}
