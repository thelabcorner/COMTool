using ComTool.Protocol;

namespace ComTool.Hosts.Abstractions;

/// <summary>
/// Truthful host-launch failure. <see cref="Execution"/> records whether class
/// activation may already have taken effect, so callers never infer retry
/// safety from an exception type or message.
/// </summary>
public sealed class HostLaunchException(
    string kind,
    string message,
    bool retryable,
    ExecutionState execution,
    int? hresult = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
    public bool Retryable { get; } = retryable;
    public ExecutionState Execution { get; } = execution;
    public int? HResultCode { get; } = hresult;
}
