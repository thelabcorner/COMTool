using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private const string IllustratorProgId = "Illustrator.Application";
    private const int AiDoNotSaveChanges = 2;
    private const int JsxNeverShowDebugger = 1;

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [STAThread]
    private static int Main(string[] args)
    {
        var mutationProbe = args.Contains("--mutation-probe", StringComparer.OrdinalIgnoreCase);
        var report = new GateReport
        {
            Gate = "0A",
            ProgId = IllustratorProgId,
            MutationProbeRequested = mutationProbe,
            Apartment = Thread.CurrentThread.GetApartmentState().ToString(),
            ProcessId = Environment.ProcessId
        };

        var clock = Stopwatch.StartNew();
        object? appObject = null;

        try
        {
            report.Steps.Add(Step("apartment", report.Apartment == "STA",
                new { apartment = report.Apartment }));

            var clsidHr = CLSIDFromProgID(IllustratorProgId, out var clsid);
            Marshal.ThrowExceptionForHR(clsidHr);
            report.Steps.Add(Step("resolve-progid", true,
                new { progId = IllustratorProgId, clsid }));

            var activeHr = GetActiveObject(ref clsid, IntPtr.Zero, out appObject);
            Marshal.ThrowExceptionForHR(activeHr);
            report.Steps.Add(Step("attach-active-object", true,
                new { isComObject = Marshal.IsComObject(appObject) }));

            dynamic app = appObject;

            string version = Convert.ToString(app.Version) ?? "";
            int documentCount = Convert.ToInt32(app.Documents.Count);
            report.IllustratorVersion = version;
            report.DocumentsBefore = documentCount;
            report.Steps.Add(Step("read-host-state", true,
                new { version, documentCount }));

            // Match the legacy tool's explicit three-argument execution path.
            object jsxRaw = app.DoJavaScript("1+1", Type.Missing, JsxNeverShowDebugger);
            string jsxResult = Convert.ToString(jsxRaw) ?? "";
            report.Steps.Add(Step("do-javascript", jsxResult.Trim() == "2",
                new { result = jsxResult }));

            if (mutationProbe)
            {
                RunDisposableMutationProbe(app, report);
            }

            report.Ok = report.Steps.All(static step => step.Ok);
            report.ElapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3);
            Write(report);
            return report.Ok ? 0 : 1;
        }
        catch (COMException ex)
        {
            report.Ok = false;
            report.ElapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3);
            report.Error = new ErrorInfo(
                "com_exception",
                ex.Message,
                ex.HResult,
                $"0x{unchecked((uint)ex.HResult):X8}");
            Write(report);
            return 1;
        }
        catch (Exception ex)
        {
            report.Ok = false;
            report.ElapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3);
            report.Error = new ErrorInfo(
                ex.GetType().Name,
                ex.Message,
                ex.HResult,
                $"0x{unchecked((uint)ex.HResult):X8}");
            Write(report);
            return 1;
        }
        finally
        {
            ReleaseCom(appObject);
        }
    }

    private static void RunDisposableMutationProbe(dynamic app, GateReport report)
    {
        object? documentsObject = null;
        object? documentObject = null;
        object? pathItemsObject = null;
        object? itemObject = null;

        try
        {
            documentsObject = app.Documents;
            dynamic documents = documentsObject;

            documentObject = documents.Add(
                Type.Missing,
                Type.Missing,
                Type.Missing,
                Type.Missing,
                Type.Missing,
                Type.Missing,
                Type.Missing);
            dynamic document = documentObject;

            pathItemsObject = document.PathItems;
            dynamic pathItems = pathItemsObject;

            int before = Convert.ToInt32(pathItems.Count);
            itemObject = pathItems.Rectangle(100.0, 100.0, 20.0, 20.0, Type.Missing);
            dynamic item = itemObject;
            int afterAdd = Convert.ToInt32(pathItems.Count);

            item.Delete();
            ReleaseCom(itemObject);
            itemObject = null;

            int afterRemove = Convert.ToInt32(pathItems.Count);

            report.Steps.Add(Step(
                "disposable-direct-dom-mutation",
                afterAdd == before + 1 && afterRemove == before,
                new { before, afterAdd, afterRemove }));

            document.Close(AiDoNotSaveChanges);
            ReleaseCom(documentObject);
            documentObject = null;

            int afterClose = Convert.ToInt32(documents.Count);
            report.DocumentsAfter = afterClose;
            report.Steps.Add(Step(
                "close-disposable-document",
                afterClose == report.DocumentsBefore,
                new
                {
                    savePolicy = "aiDoNotSaveChanges",
                    savePolicyValue = AiDoNotSaveChanges,
                    documentsBefore = report.DocumentsBefore,
                    documentsAfter = afterClose
                }));
        }
        finally
        {
            if (documentObject is not null)
            {
                try
                {
                    dynamic document = documentObject;
                    document.Close(AiDoNotSaveChanges);
                }
                catch
                {
                    // Spike cleanup only. The primary exception/result is preserved.
                }
            }

            ReleaseCom(itemObject);
            ReleaseCom(pathItemsObject);
            ReleaseCom(documentObject);
            ReleaseCom(documentsObject);
        }
    }

    private static StepResult Step(string name, bool ok, object? evidence) =>
        new(name, ok, evidence);

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Cleanup failures must not hide primary gate evidence.
        }
    }

    private static void Write(GateReport report)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        Console.Out.WriteLine(JsonSerializer.Serialize(report, options));
    }

    private sealed class GateReport
    {
        public string Gate { get; init; } = "";
        public bool Ok { get; set; }
        public string ProgId { get; init; } = "";
        public bool MutationProbeRequested { get; init; }
        public string Apartment { get; init; } = "";
        public int ProcessId { get; init; }
        public string? IllustratorVersion { get; set; }
        public int? DocumentsBefore { get; set; }
        public int? DocumentsAfter { get; set; }
        public double ElapsedMs { get; set; }
        public List<StepResult> Steps { get; } = [];
        public ErrorInfo? Error { get; set; }
    }

    private sealed record StepResult(string Name, bool Ok, object? Evidence);

    private sealed record ErrorInfo(
        string Kind,
        string Message,
        int HResult,
        string HResultHex);
}
