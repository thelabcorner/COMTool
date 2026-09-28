using ComTool.Protocol;

namespace ComTool.Hosts.Abstractions;

public sealed class HostAdapterException : Exception
{
    public HostAdapterException(
        string kind,
        string message,
        bool retryable,
        ExecutionState execution,
        int? hresult = null,
        Exception? innerException = null,
        IReadOnlyList<EvidenceItem>? evidence = null)
        : base(message, innerException)
    {
        Kind = kind;
        Retryable = retryable;
        Execution = execution;
        HResultCode = hresult;
        Evidence = evidence;
    }

    public string Kind { get; }
    public bool Retryable { get; }
    public ExecutionState Execution { get; }
    public int? HResultCode { get; }

    /// <summary>
    /// Optional bounded diagnostics for this failure.
    /// <para>
    /// Purely additive observability. It is never an input to
    /// <see cref="ToProtocolError"/>, to <see cref="Kind"/>,
    /// <see cref="Retryable"/>, or to <see cref="Execution"/>, and it
    /// defaults to <c>null</c> so every pre-existing throw site keeps its
    /// exact reported failure semantics.
    /// </para>
    /// </summary>
    public IReadOnlyList<EvidenceItem>? Evidence { get; }

    public ProtocolError ToProtocolError(params string[] suggestedActions) =>
        new()
        {
            Kind = Kind,
            Message = Message,
            Retryable = Retryable,
            Execution = Execution,
            HResult = HResultCode,
            HResultHex = HResultCode is null
                ? null
                : $"0x{unchecked((uint)HResultCode.Value):X8}",
            SuggestedActions = suggestedActions.Length == 0 ? null : suggestedActions
        };
}
