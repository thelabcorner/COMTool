using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private const string ProgId = "Photoshop.Application";

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [STAThread]
    private static int Main()
    {
        var sw = Stopwatch.StartNew();
        object? appObject = null;
        bool launchedBySpike = false;
        var checks = new List<Check>();

        try
        {
            var apartment = Thread.CurrentThread.GetApartmentState().ToString();
            checks.Add(new("sta", apartment == "STA", new { apartment }));

            var clsidHr = CLSIDFromProgID(ProgId, out var clsid);
            Marshal.ThrowExceptionForHR(clsidHr);
            checks.Add(new("resolve-progid", true, new { progId = ProgId, clsid }));

            var activeHr = GetActiveObject(ref clsid, IntPtr.Zero, out appObject);
            if (activeHr < 0)
            {
                // MK_E_UNAVAILABLE means no active instance is registered. Other
                // activation errors remain real failures.
                const int MkEUnavailable = unchecked((int)0x800401E3);
                if (activeHr != MkEUnavailable)
                    Marshal.ThrowExceptionForHR(activeHr);

                var comType = Type.GetTypeFromCLSID(clsid, throwOnError: true)
                    ?? throw new InvalidOperationException("Could not resolve Photoshop COM type");
                appObject = Activator.CreateInstance(comType)
                    ?? throw new InvalidOperationException("Photoshop COM activation returned null");
                launchedBySpike = true;
            }

            checks.Add(new("instance-ownership", appObject is not null,
                new { launchedBySpike, ownership = launchedBySpike ? "owned" : "preexisting" }));

            dynamic app = appObject!;
            var name = SafeString(() => app.Name);
            var version = SafeString(() => app.Version);
            var documentCount = SafeInt(() => app.Documents.Count);

            checks.Add(new("generic-host-state",
                !string.IsNullOrWhiteSpace(version) && documentCount >= 0,
                new { host = "photoshop", name, version, documentCount }));

            // Script execution is a separately probed facet. V2 does not assume
            // every Adobe host exposes Illustrator's exact scripting surface.
            bool scriptSupported;
            string? scriptResult = null;
            string? scriptError = null;
            try
            {
                scriptResult = Convert.ToString(app.DoJavaScript("app.version"));
                scriptSupported = true;
            }
            catch (Exception ex)
            {
                scriptSupported = false;
                scriptError = $"{ex.GetType().Name}: {ex.Message}";
            }

            checks.Add(new("script-capability-probed", true,
                new
                {
                    supported = scriptSupported,
                    route = scriptSupported ? "com.DoJavaScript" : null,
                    result = scriptResult,
                    error = scriptError
                }));

            bool cleanupSafe = true;
            bool quitIssued = false;
            if (launchedBySpike)
            {
                var docsBeforeQuit = SafeInt(() => app.Documents.Count);
                if (docsBeforeQuit == 0)
                {
                    app.Quit();
                    quitIssued = true;
                }
                else
                {
                    // Never close documents merely to satisfy a test gate.
                    cleanupSafe = false;
                }
            }

            checks.Add(new("ownership-safe-cleanup",
                !launchedBySpike || (cleanupSafe && quitIssued),
                new { launchedBySpike, cleanupSafe, quitIssued }));

            ReleaseCom(appObject);
            appObject = null;

            bool activeAfterOwnedQuit = false;
            if (launchedBySpike && quitIssued)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(15))
                {
                    Thread.Sleep(250);
                    object? probe = null;
                    var hr = GetActiveObject(ref clsid, IntPtr.Zero, out probe);
                    if (hr < 0)
                    {
                        activeAfterOwnedQuit = false;
                        ReleaseCom(probe);
                        break;
                    }

                    activeAfterOwnedQuit = true;
                    ReleaseCom(probe);
                }

                checks.Add(new("owned-instance-detached",
                    !activeAfterOwnedQuit,
                    new { activeAfterOwnedQuit }));
            }

            sw.Stop();
            var ok = checks.All(static c => c.Ok);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                gate = "0F",
                ok,
                referenceHost = "photoshop",
                progId = ProgId,
                launchedBySpike,
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                architecturalEvidence = new
                {
                    genericComFacet = true,
                    hostSpecificDom = true,
                    scriptingIsOptionalFacet = true,
                    ownershipAwareLifecycle = true
                },
                checks
            }, JsonOptions));

            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                gate = "0F",
                ok = false,
                referenceHost = "photoshop",
                launchedBySpike,
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                error = new
                {
                    kind = ex.GetType().Name,
                    message = ex.Message,
                    hresult = ex.HResult,
                    hresultHex = $"0x{unchecked((uint)ex.HResult):X8}"
                },
                checks
            }, JsonOptions));
            return 1;
        }
        finally
        {
            ReleaseCom(appObject);
        }
    }

    private static string? SafeString(Func<object?> getter)
    {
        try { return Convert.ToString(getter()); }
        catch { return null; }
    }

    private static int SafeInt(Func<object?> getter)
    {
        try { return Convert.ToInt32(getter()); }
        catch { return -1; }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;

        try { Marshal.FinalReleaseComObject(value); }
        catch { }
    }

    private sealed record Check(string Name, bool Ok, object Evidence);
}
