using System.Security.Cryptography;
using System.Text;

namespace ComTool.Hosts.Illustrator;

internal static class IllustratorEsonRuntime
{
    internal const string ResourceName =
        "ComTool.Hosts.Illustrator.Assets.eson-runtime.js";

    internal const string ExpectedSha256 =
        "31ee6046390589fabcc8a574c0b1fad50684912ae1d04682f026f95a9e7750fc";

    private static readonly Lazy<string> SourceLazy =
        new(LoadAndVerify, isThreadSafe: true);

    public static string Source => SourceLazy.Value;

    private static string LoadAndVerify()
    {
        var assembly = typeof(IllustratorEsonRuntime).Assembly;

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded ESON runtime resource '{ResourceName}' was not found.");

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        var source = reader.ReadToEnd();
        var hash = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();

        if (!string.Equals(
                hash,
                ExpectedSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Embedded ESON runtime provenance mismatch. Expected {ExpectedSha256}, got {hash}.");
        }

        return source;
    }
}
