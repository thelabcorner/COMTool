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

    private static readonly AsyncLocal<TimeSpan?> RequestRetryBudget = new();

    /// <summary>
    /// Applies one operation's retry budget to every COM helper reached on the
    /// current async/thread flow. The worker command lane is serialized, and
    /// the previous scope is restored on dispose so no request can leak policy
    /// into the next one.
    /// </summary>
    internal static IDisposable PushRetryBudget(int? retryBudgetMs)
    {
        if (retryBudgetMs is { } value &&
            !OperationPolicy.IsValidRetryBudget(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryBudgetMs),
                value,
                $"Retry budget must be between " +
                $"{OperationPolicy.MinRetryBudgetMs} and " +
                $"{OperationPolicy.MaxRetryBudgetMs} ms.");
        }

        var previous = RequestRetryBudget.Value;
        RequestRetryBudget.Value = retryBudgetMs is { } milliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
        return new RetryBudgetScope(previous);
    }

    private static TimeSpan ResolveRetryBudget(TimeSpan? explicitBudget) =>
        explicitBudget ??
        RequestRetryBudget.Value ??
        TimeSpan.FromMilliseconds(OperationPolicy.DefaultRetryBudgetMs);

    private sealed class RetryBudgetScope(TimeSpan? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                RequestRetryBudget.Value = previous;
        }
    }

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

    public static object Activate(string progId)
    {
        EnsureSta();
        ArgumentException.ThrowIfNullOrWhiteSpace(progId);

        var type = Type.GetTypeFromProgID(
            progId,
            throwOnError: true)
            ?? throw new COMException(
                $"COM ProgID '{progId}' did not resolve to a registered class.");

        return Activator.CreateInstance(type)
            ?? throw new COMException(
                $"COM activation for '{progId}' returned null.");
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

        var budget = ResolveRetryBudget(rejectionRetryBudget);
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
        var budget = ResolveRetryBudget(retryBudget);
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

    public static object? InvokeMutationMethod(
        object target,
        string method,
        object?[] args,
        TimeSpan? rejectionRetryBudget = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(args);
        EnsureSta();

        var budget = ResolveRetryBudget(rejectionRetryBudget);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(25);

        while (true)
        {
            try
            {
                return InvokeMethodOnce(target, method, args);
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

    /// <summary>
    /// Property-put sibling of <see cref="InvokeMutationMethod"/>. The retry
    /// budget covers only HRESULTs rejected before the call can run; once the
    /// host may have accepted the put, failure is ambiguous and must never be
    /// replayed.
    /// </summary>
    public static void PutMutationProperty(
        object target,
        string property,
        object? value,
        TimeSpan? rejectionRetryBudget = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        EnsureSta();

        var budget = ResolveRetryBudget(rejectionRetryBudget);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(25);

        while (true)
        {
            try
            {
                InvokePropertyPutOnce(target, property, value);
                return;
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
                throw new HostAdapterException(
                    Classify(ex.HResult),
                    ex.Message,
                    retryable: false,
                    ExecutionState.Ambiguous,
                    ex.HResult,
                    ex);
            }
            catch (Exception ex)
                when (ex is MissingMemberException or
                    MissingMethodException or
                    TargetException or
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

    private static object? InvokeMethodOnce(
        object target,
        string method,
        object?[] args)
    {
        try
        {
            return target
                .GetType()
                .InvokeMember(
                    method,
                    BindingFlags.InvokeMethod |
                    BindingFlags.OptionalParamBinding,
                    binder: null,
                    target,
                    args,
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

    private static void InvokePropertyPutOnce(
        object target,
        string property,
        object? value)
    {
        try
        {
            target.GetType().InvokeMember(
                property,
                BindingFlags.SetProperty |
                BindingFlags.OptionalParamBinding,
                binder: null,
                target,
                [value],
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
