using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ComTool.Runtime.Ipc;

public static class RuntimeEndpoint
{
    public const string PipeEnvironmentVariable =
        "COMTOOL_V2_RUNTIME_PIPE";

    public static string DefaultPipeName =>
        $"comtool-v2-runtime-{Process.GetCurrentProcess().SessionId}";

    public static string DefaultMutexName =>
        $"Local\\ComToolV2Runtime_{Process.GetCurrentProcess().SessionId}";

    public static string ResolvePipeName(string? explicitPipeName = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPipeName))
            return explicitPipeName;

        var environmentPipe =
            Environment.GetEnvironmentVariable(
                PipeEnvironmentVariable);

        return string.IsNullOrWhiteSpace(environmentPipe)
            ? DefaultPipeName
            : environmentPipe;
    }

    public static string MutexNameForPipe(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        if (string.Equals(
                pipeName,
                DefaultPipeName,
                StringComparison.Ordinal))
            return DefaultMutexName;

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(pipeName));

        return
            $"Local\\ComToolV2Runtime_{Process.GetCurrentProcess().SessionId}_{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }
}
