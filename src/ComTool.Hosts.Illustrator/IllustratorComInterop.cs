using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

internal static class IllustratorComInterop
{
    public const string ProgId = "Illustrator.Application";

    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);
    private const int RpcSCallFailed = unchecked((int)0x800706BE);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    public static object AttachActive()
    {
        EnsureSta();

        var clsidHr = CLSIDFromProgID(ProgId, out var clsid);
        Marshal.ThrowExceptionForHR(clsidHr);

        var activeHr = GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
        Marshal.ThrowExceptionForHR(activeHr);
        return instance;
    }

    public static void EnsureSta()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Adobe COM automation is Windows-only.");

        var apartment = Thread.CurrentThread.GetApartmentState();
        if (apartment != ApartmentState.STA)
        {
            throw new HostAdapterException(
                "invalid_com_apartment",
                $"Illustrator COM worker requires STA; current apartment is {apartment}.",
                retryable: false,
                ExecutionState.NotStarted);
        }
    }

    public static string ExecuteJavaScript(
        object application,
        string source,
        int executionMode,
        TimeSpan? rejectionRetryBudget = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(source);
        EnsureSta();

        var budget =
            rejectionRetryBudget ?? TimeSpan.FromSeconds(2);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(25);

        while (true)
        {
            try
            {
                var value = InvokeDoJavaScriptOnce(
                    application,
                    source,
                    executionMode);

                return value as string
                    ?? Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture)
                    ?? string.Empty;
            }
            catch (COMException ex)
                when (IsDefinitelyRejectedBeforeExecution(ex.HResult))
            {
                var remaining = budget - started.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new HostAdapterException(
                        "host_busy",
                        ex.Message,
                        retryable: true,
                        ExecutionState.NotStarted,
                        ex.HResult,
                        ex);
                }

                var sleep =
                    delay <= remaining ? delay : remaining;
                Thread.Sleep(sleep);
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, 250));
            }
            catch (COMException ex)
            {
                // Once a script dispatch is accepted, transport/server
                // failure cannot prove whether user code ran. Never blindly
                // replay arbitrary ExtendScript after this point.
                throw new HostAdapterException(
                    Classify(ex.HResult),
                    ex.Message,
                    retryable: false,
                    ExecutionState.Ambiguous,
                    ex.HResult,
                    ex);
            }
            catch (Exception ex)
                when (ex is MissingMethodException or
                    TargetParameterCountException or
                    ArgumentException)
            {
                throw new HostAdapterException(
                    "com_signature_mismatch",
                    ex.Message,
                    retryable: false,
                    ExecutionState.NotStarted,
                    ex.HResult,
                    ex);
            }
        }
    }

    public static T RetryRead<T>(
        Func<T> operation,
        TimeSpan? retryBudget = null)
    {
        var budget = retryBudget ?? TimeSpan.FromSeconds(2);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(25);

        while (true)
        {
            try
            {
                return operation();
            }
            catch (COMException ex) when (IsRetryableBusy(ex.HResult))
            {
                var remaining = budget - started.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new HostAdapterException(
                        "host_busy",
                        ex.Message,
                        retryable: true,
                        ExecutionState.NotStarted,
                        ex.HResult,
                        ex);
                }

                var sleep = delay <= remaining ? delay : remaining;
                Thread.Sleep(sleep);
                var nextMs = Math.Min(delay.TotalMilliseconds * 2, 250);
                delay = TimeSpan.FromMilliseconds(nextMs);
            }
            catch (COMException ex)
            {
                throw new HostAdapterException(
                    Classify(ex.HResult),
                    ex.Message,
                    retryable: false,
                    ExecutionState.Started,
                    ex.HResult,
                    ex);
            }
        }
    }

    public static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Worker teardown owns final recovery. Release failure is not proof
            // that the host target itself is inconsistent.
        }
    }

    private static object? InvokeDoJavaScriptOnce(
        object application,
        string source,
        int executionMode)
    {
        try
        {
            return application
                .GetType()
                .InvokeMember(
                    "DoJavaScript",
                    BindingFlags.InvokeMethod |
                    BindingFlags.OptionalParamBinding,
                    binder: null,
                    target: application,
                    args:
                    [
                        source,
                        Type.Missing,
                        executionMode
                    ],
                    modifiers: null,
                    culture: CultureInfo.InvariantCulture,
                    namedParameters: null);
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is COMException comException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(comException)
                .Throw();

            throw;
        }
    }

    private static bool IsDefinitelyRejectedBeforeExecution(int hresult) =>
        hresult is RpcECallRejected or RpcEServerCallRetryLater;

    private static bool IsRetryableBusy(int hresult) =>
        hresult is RpcECallRejected or RpcEServerCallRetryLater or RpcSCallFailed;

    private static string Classify(int hresult) =>
        hresult switch
        {
            unchecked((int)0x80010105) => "host_server_fault",
            _ => "com_failure"
        };
}
