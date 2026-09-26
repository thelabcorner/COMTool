using System.Security.Cryptography;
using System.Text;

namespace ComTool.Hosts.Abstractions;

public sealed record HostTargetIdentity
{
    public required string Host { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset ProcessStartedAt { get; init; }
    public required string ExecutablePath { get; init; }
    public required string HostVersion { get; init; }
    public required string AdapterVersion { get; init; }
    public string? EndpointIdentity { get; init; }

    public string TargetId => CreateTargetId(this);

    private static string CreateTargetId(HostTargetIdentity identity)
    {
        var canonical = string.Join(
            "\n",
            identity.Host.ToLowerInvariant(),
            identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity.ProcessStartedAt.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Path.GetFullPath(identity.ExecutablePath).ToUpperInvariant(),
            identity.EndpointIdentity ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{identity.Host.ToLowerInvariant()}:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
