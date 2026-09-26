using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime.Ipc;

internal static class Program
{
    private const string IllustratorProgId = "Illustrator.Application";
    private const int AiDoNotSaveChanges = 2;

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(
        string lpszProgID,
        out Guid lpclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [STAThread]
    private static int Main()
    {
        object? appObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        object? pathItemsObject = null;
        object? itemObject = null;

        var report = new SmokeReport();

        try
        {
            var clsidHr = CLSIDFromProgID(IllustratorProgId, out var clsid);
            Marshal.ThrowExceptionForHR(clsidHr);

            var activeHr = GetActiveObject(
                ref clsid,
                IntPtr.Zero,
                out appObject);
            Marshal.ThrowExceptionForHR(activeHr);

            dynamic app = appObject;
            documentsObject = app.Documents;
            dynamic documents = documentsObject;

            var documentsBefore = Convert.ToInt32(documents.Count);
            report.DocumentsBefore = documentsBefore;

            if (documentsBefore != 0)
            {
                report.Skipped = true;
                report.Reason =
                    "Refusing disposable mutation because Illustrator already has open documents.";
                return 2;
            }

            var target = ResolveSingleRuntimeTarget();

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

            itemObject = pathItems.Rectangle(
                100.0,
                100.0,
                20.0,
                30.0,
                Type.Missing);
            dynamic item = itemObject;

            item.Name = "comtool-v2-snapshot-smoke";
            item.Selected = true;

            var expectedUuid = Convert.ToString(item.Uuid);
            var expectedName = Convert.ToString(item.Name);
            var expectedPageItemType = Convert.ToInt32(item.PageItemType);

            var snapshot = ExecuteSnapshot(target);
            report.Snapshot = snapshot;
            var artboards = ExecuteStructureRead(
                target,
                "illustrator.artboard.read");
            var layers = ExecuteStructureRead(
                target,
                "illustrator.layer.read");
            var root = RequirePayloadObject(snapshot);
            var activeDocument = root.GetProperty("activeDocument");
            var selection = root.GetProperty("selection");
            var selected = selection[0];

            report.Checks.Add(
                new Check(
                    "document-count",
                    root.GetProperty("documentsCount").GetInt32() == 1));

            report.Checks.Add(
                new Check(
                    "active-document",
                    activeDocument.ValueKind == JsonValueKind.Object));

            report.Checks.Add(
                new Check(
                    "page-item-count",
                    activeDocument.GetProperty("pageItemsCount").GetInt32() >= 1));

            report.Checks.Add(
                new Check(
                    "path-item-count",
                    activeDocument.GetProperty("pathItemsCount").GetInt32() >= 1));

            report.Checks.Add(
                new Check(
                    "selection-count",
                    activeDocument.GetProperty("selectionCount").GetInt32() == 1 &&
                    selection.GetArrayLength() == 1));

            report.Checks.Add(
                new Check(
                    "selection-type",
                    selected.GetProperty("pageItemType").GetInt32() ==
                        expectedPageItemType &&
                    expectedPageItemType == 5 &&
                    selected.GetProperty("pageItemTypeName").GetString() ==
                        "path"));

            report.Checks.Add(
                new Check(
                    "selection-identity",
                    selected.GetProperty("uuid").GetString() == expectedUuid &&
                    selected.GetProperty("name").GetString() == expectedName));

            report.Checks.Add(
                new Check(
                    "selection-bounds",
                    IsFourNumberArray(
                        selected.GetProperty("geometricBounds")) &&
                    IsFourNumberArray(
                        selected.GetProperty("visibleBounds"))));

            report.Checks.Add(
                new Check(
                    "active-artboard",
                    activeDocument
                        .GetProperty("activeArtboardIndex")
                        .GetInt32() == 0));
            var artboardPayload = RequirePayloadObject(artboards);
            report.Checks.Add(
                new Check(
                    "wave4-artboard-read",
                    artboards.Ok &&
                    artboardPayload.GetProperty("exists").GetBoolean() &&
                    artboardPayload.GetProperty("artboardCount").GetInt32() == 1 &&
                    artboardPayload.GetProperty("artboards").GetArrayLength() == 1));
            var originalArtboardName =
                artboardPayload.GetProperty("artboards")[0]
                    .GetProperty("name")
                    .GetString()
                ?? throw new InvalidOperationException(
                    "Active artboard name was null.");
            var leaseId = AcquireLease(target);
            try
            {
                var smokeArtboardName =
                    $"comtool-v2-live-{Guid.NewGuid():N}";
                var setResult = SetActiveArtboardName(
                    target,
                    leaseId,
                    smokeArtboardName);
                var renamed = ExecuteStructureRead(
                    target,
                    "illustrator.artboard.read",
                    leaseId);
                var renamedPayload = RequirePayloadObject(renamed);
                var renamedName =
                    renamedPayload.GetProperty("artboards")[0]
                        .GetProperty("name")
                        .GetString();
                report.Checks.Add(
                    new Check(
                        "controlled-artboard-mutation",
                        setResult.Ok &&
                        renamedName == smokeArtboardName));

                var restoreResult = SetActiveArtboardName(
                    target,
                    leaseId,
                    originalArtboardName);
                var restored = ExecuteStructureRead(
                    target,
                    "illustrator.artboard.read",
                    leaseId);
                var restoredPayload = RequirePayloadObject(restored);
                report.Checks.Add(
                    new Check(
                        "controlled-artboard-restore",
                        restoreResult.Ok &&
                        restoredPayload.GetProperty("artboards")[0]
                            .GetProperty("name")
                            .GetString() == originalArtboardName));
            }
            finally
            {
                ReleaseLease(target, leaseId);
            }
            var layerPayload = RequirePayloadObject(layers);
            report.Checks.Add(
                new Check(
                    "wave4-layer-read",
                    layers.Ok &&
                    layerPayload.GetProperty("exists").GetBoolean() &&
                    layerPayload.GetProperty("layerCount").GetInt32() >= 1 &&
                    layerPayload.GetProperty("layers").GetArrayLength() >= 1));

            report.Ok = snapshot.Ok &&
                        report.Checks.All(static check => check.Ok);

            return report.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.Ok = false;
            report.Error = new ErrorInfo(
                ex.GetType().Name,
                ex.Message,
                ex.HResult,
                $"0x{unchecked((uint)ex.HResult):X8}");
            return 1;
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
                    // Cleanup must not replace the primary failure.
                }
            }

            if (documentsObject is not null)
            {
                try
                {
                    dynamic documents = documentsObject;
                    report.DocumentsAfter =
                        Convert.ToInt32(documents.Count);
                }
                catch
                {
                }
            }

            ReleaseCom(itemObject);
            ReleaseCom(pathItemsObject);
            ReleaseCom(documentObject);
            ReleaseCom(documentsObject);
            ReleaseCom(appObject);

            if (!report.Skipped)
            {
                report.Checks.Add(
                    new Check(
                        "cleanup-restored-document-count",
                        report.DocumentsAfter == report.DocumentsBefore));
                report.Ok &= report.DocumentsAfter == report.DocumentsBefore;
            }

            Write(report);
        }
    }

    private static TargetRef ResolveSingleRuntimeTarget()
    {
        using var nullDocument = JsonDocument.Parse("null");

        var client = RuntimePipeClient
            .ConnectAsync(timeout: TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();

        try
        {
            var result = client
                .ExecuteAsync(new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = $"snapshot-smoke-targets-{Guid.NewGuid():N}",
                    Operation = "core.targets.list",
                    Input = nullDocument.RootElement.Clone()
                })
                .GetAwaiter()
                .GetResult();

            var payload = RequirePayloadObjectOrArray(result);
            if (payload.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(
                    "Runtime target list payload was not an array.");

            var running = payload
                .EnumerateArray()
                .Where(static entry =>
                    entry.GetProperty("running").GetBoolean())
                .Select(static entry => entry.GetProperty("target"))
                .ToArray();

            if (running.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one running target, found {running.Length}.");

            var target = running[0];

            return new TargetRef(
                target.GetProperty("host").GetString()
                    ?? throw new InvalidOperationException(
                        "Target host missing."),
                target.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException(
                        "Target id missing."),
                target.TryGetProperty("generation", out var generation) &&
                generation.ValueKind == JsonValueKind.Number
                    ? generation.GetInt64()
                    : null);
        }
        finally
        {
            client
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static OperationResult ExecuteSnapshot(TargetRef target)
    {
        using var nullDocument = JsonDocument.Parse("null");

        var client = RuntimePipeClient
            .ConnectAsync(timeout: TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();

        try
        {
            return client
                .ExecuteAsync(
                    new OperationRequest
                    {
                        ProtocolVersion = ProtocolVersion.Current,
                        Id = $"snapshot-smoke-{Guid.NewGuid():N}",
                        Target = target,
                        Operation = "core.target.snapshot",
                        Input = nullDocument.RootElement.Clone()
                    },
                    timeout: TimeSpan.FromSeconds(30))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            client
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static OperationResult ExecuteStructureRead(
        TargetRef target,
        string operation,
        string? leaseId = null)
    {
        using var inputDocument = JsonDocument.Parse(
            """{"active":true}""");

        var client = RuntimePipeClient
            .ConnectAsync(timeout: TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();

        try
        {
            return client
                .ExecuteAsync(
                    new OperationRequest
                    {
                        ProtocolVersion = ProtocolVersion.Current,
                        Id = $"structure-smoke-{Guid.NewGuid():N}",
                        Target = target,
                        Operation = operation,
                        Input = inputDocument.RootElement.Clone(),
                        Policy = leaseId is null
                            ? null
                            : new OperationPolicy(LeaseId: leaseId)
                    },
                    timeout: TimeSpan.FromSeconds(30))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            client
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static string AcquireLease(TargetRef target)
    {
        using var input = JsonDocument.Parse("""{"ttlMs":30000}""");
        var result = ExecuteRuntimeTargetOperation(
            target,
            "core.target.lease.acquire",
            input.RootElement.Clone(),
            leaseId: null);
        return RequirePayloadObject(result)
            .GetProperty("leaseId")
            .GetString()
            ?? throw new InvalidOperationException(
                "Lease acquisition returned no lease id.");
    }

    private static void ReleaseLease(
        TargetRef target,
        string leaseId)
    {
        using var input = JsonDocument.Parse("null");
        var result = ExecuteRuntimeTargetOperation(
            target,
            "core.target.lease.release",
            input.RootElement.Clone(),
            leaseId);
        if (!result.Ok)
            throw new InvalidOperationException(
                result.Error?.Message ?? "Lease release failed.");
    }

    private static OperationResult SetActiveArtboardName(
        TargetRef target,
        string leaseId,
        string value)
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            property = "document.artboard.active.name",
            value
        });
        return ExecuteRuntimeTargetOperation(
            target,
            "illustrator.artboard.setName",
            input,
            leaseId);
    }

    private static OperationResult ExecuteRuntimeTargetOperation(
        TargetRef target,
        string operation,
        JsonElement input,
        string? leaseId)
    {
        var client = RuntimePipeClient
            .ConnectAsync(timeout: TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        try
        {
            return client.ExecuteAsync(
                    new OperationRequest
                    {
                        ProtocolVersion = ProtocolVersion.Current,
                        Id = $"live-smoke-{Guid.NewGuid():N}",
                        Target = target,
                        Operation = operation,
                        Input = input,
                        Policy = leaseId is null
                            ? null
                            : new OperationPolicy(LeaseId: leaseId)
                    },
                    timeout: TimeSpan.FromSeconds(30))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            client.DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static JsonElement RequirePayloadObject(
        OperationResult result)
    {
        var value = RequirePayloadObjectOrArray(result);
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "Snapshot payload was not an object.");

        return value;
    }

    private static JsonElement RequirePayloadObjectOrArray(
        OperationResult result)
    {
        if (!result.Ok)
            throw new InvalidOperationException(
                result.Error?.Message ??
                $"Operation '{result.Operation}' failed.");

        if (result.Result?.Value is not JsonElement value)
            throw new InvalidOperationException(
                $"Operation '{result.Operation}' returned no payload.");

        return value;
    }

    private static bool IsFourNumberArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() != 4)
            return false;

        return value
            .EnumerateArray()
            .All(static element =>
                element.ValueKind == JsonValueKind.Number);
    }

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
        }
    }

    private static void Write(SmokeReport report) =>
        Console.Out.WriteLine(
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy =
                        JsonNamingPolicy.CamelCase
                }));

    private sealed class SmokeReport
    {
        public bool Ok { get; set; }
        public bool Skipped { get; set; }
        public string? Reason { get; set; }
        public int? DocumentsBefore { get; set; }
        public int? DocumentsAfter { get; set; }
        public OperationResult? Snapshot { get; set; }
        public List<Check> Checks { get; } = [];
        public ErrorInfo? Error { get; set; }
    }

    private sealed record Check(string Name, bool Ok);

    private sealed record ErrorInfo(
        string Kind,
        string Message,
        int HResult,
        string HResultHex);
}
