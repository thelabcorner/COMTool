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
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Retryable = retryable;
        Execution = execution;
        HResultCode = hresult;
    }

    public string Kind { get; }
    public bool Retryable { get; }
    public ExecutionState Execution { get; }
    public int? HResultCode { get; }

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
