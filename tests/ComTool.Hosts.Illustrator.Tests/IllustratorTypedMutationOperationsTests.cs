using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorTypedMutationOperationsTests
{
    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerException = unchecked((int)0x80010105);

    [Fact]
    public void LayerOpacityByUniqueNameMutatesOnlySelectedLayer()
    {
        var app = MutableApplication.Create();

        var result = ExecuteSta(
            app,
            IllustratorTypedMutationOperations.LayerSetOpacityOperation,
            """
            {
              "document":{"index":0},
              "layer":{"name":"Layer B"},
              "value":42.5
            }
            """);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(100d, app.Documents.Item(1).Layers.Item(1).Opacity);
        Assert.Equal(42.5d, app.Documents.Item(1).Layers.Item(2).Opacity);

        var payload = Value(result);
        Assert.Equal("Opacity", payload.GetProperty("property").GetString());
        Assert.Equal(
            "Layer B",
            payload.GetProperty("layer").GetProperty("name").GetString());
        Assert.Equal("idempotent_write", payload.GetProperty("mutationClass").GetString());
    }

    [Fact]
    public void LayerRenameByNameRefusesAmbiguousSelectorBeforeMutation()
    {
        var app = MutableApplication.Create();
        app.Documents.Item(1).Layers.Item(1).Name = "Duplicate";
        app.Documents.Item(1).Layers.Item(2).Name = "Duplicate";

        var result = Execute(
            app,
            IllustratorTypedMutationOperations.LayerSetNameOperation,
            """
            {
              "document":{"index":0},
              "layer":{"name":"Duplicate"},
              "value":"Renamed"
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal("layer_selector_ambiguous", result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Equal("Duplicate", app.Documents.Item(1).Layers.Item(1).Name);
        Assert.Equal("Duplicate", app.Documents.Item(1).Layers.Item(2).Name);
    }

    [Fact]
    public void LayerRenameSuccessReportsPostMutationName()
    {
        var app = MutableApplication.Create();

        var result = ExecuteSta(
            app,
            IllustratorTypedMutationOperations.LayerSetNameOperation,
            """
            {
              "document":{"index":0},
              "layer":{"index":0},
              "value":"Renamed"
            }
            """);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(
            "Renamed",
            app.Documents.Item(1).Layers.Item(1).Name);
        var payload = Value(result);
        Assert.Equal(
            "Renamed",
            payload.GetProperty("layer").GetProperty("name").GetString());
        Assert.Equal(
            "Renamed",
            payload.GetProperty("value").GetString());
    }

    [Fact]
    public void LayerSelectorDriftIsRefusedBeforePropertyPut()
    {
        var app = MutableApplication.Create();
        var layers = app.Documents.Item(1).Layers;
        layers.BeforeItemReturn = (read, layer) =>
        {
            if (read == 3)
                layer.Name = "Externally Renamed";
        };

        var result = ExecuteSta(
            app,
            IllustratorTypedMutationOperations.LayerSetVisibleOperation,
            """
            {
              "document":{"index":0},
              "layer":{"name":"Layer B"},
              "value":false
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(
            "typed_mutation_selector_changed",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.True(app.Documents.Item(1).Layers.Item(2).Visible);
    }

    [Fact]
    public void DocumentSelectorDriftIsRefusedBeforePropertyPut()
    {
        var app = MutableApplication.Create();
        app.Documents.BeforeItemReturn = (read, document) =>
        {
            if (read == 5)
                document.Name = "Externally Retargeted.ai";
        };

        var result = ExecuteSta(
            app,
            IllustratorTypedMutationOperations.LayerSetLockedOperation,
            """
            {
              "document":{"index":0},
              "layer":{"index":0},
              "value":true
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(
            "typed_mutation_selector_changed",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.False(app.Documents.Item(1).Layers.Item(1).Locked);
    }

    [Fact]
    public void LayerVisibilityRejectsImplicitTargeting()
    {
        var app = MutableApplication.Create();

        var result = Execute(
            app,
            IllustratorTypedMutationOperations.LayerSetVisibleOperation,
            """
            {
              "document":{"index":0},
              "layer":{},
              "value":false
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_layer_selector", result.Error?.Kind);
        Assert.True(app.Documents.Item(1).Layers.Item(1).Visible);
    }

    [Fact]
    public void TypedMutationRejectsActiveDocumentSelector()
    {
        var app = MutableApplication.Create();

        var result = Execute(
            app,
            IllustratorTypedMutationOperations.LayerSetVisibleOperation,
            """
            {
              "document":{"active":true},
              "layer":{"index":0},
              "value":false
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_document_selector", result.Error?.Kind);
        Assert.True(app.Documents.Item(1).Layers.Item(1).Visible);
    }

    [Fact]
    public void ArtboardRectByNameMutatesOnlySelectedArtboard()
    {
        var app = MutableApplication.Create();

        var result = ExecuteSta(
            app,
            IllustratorTypedMutationOperations.ArtboardSetRectOperation,
            """
            {
              "document":{"name":"First.ai"},
              "artboard":{"name":"Board B"},
              "value":[10,200,310,-20]
            }
            """);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(
            new double[] { 0, 100, 100, 0 },
            app.Documents.Item(1).Artboards.Item(1).ArtboardRect);
        Assert.Equal(
            new double[] { 10, 200, 310, -20 },
            app.Documents.Item(1).Artboards.Item(2).ArtboardRect);
    }

    [Theory]
    [InlineData("""[0,0,100,100]""")]
    [InlineData("""[100,100,0,0]""")]
    [InlineData("""[0,100,0,0]""")]
    public void ArtboardRectRejectsInvalidGeometryBeforeMutation(string valueJson)
    {
        var app = MutableApplication.Create();
        var before = app.Documents.Item(1).Artboards.Item(1).ArtboardRect.ToArray();

        var result = Execute(
            app,
            IllustratorTypedMutationOperations.ArtboardSetRectOperation,
            $$"""
            {
              "document":{"index":0},
              "artboard":{"index":0},
              "value":{{valueJson}}
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_artboard_rect", result.Error?.Kind);
        Assert.Equal(
            before,
            app.Documents.Item(1).Artboards.Item(1).ArtboardRect);
    }

    [Fact]
    public void DuplicateDocumentNamesAreNeverSilentlyRetargeted()
    {
        var app = MutableApplication.Create();
        app.Documents.Item(2).Name = "First.ai";

        var result = Execute(
            app,
            IllustratorTypedMutationOperations.LayerSetLockedOperation,
            """
            {
              "document":{"name":"First.ai"},
              "layer":{"index":0},
              "value":true
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal("document_selector_ambiguous", result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.False(app.Documents.Item(1).Layers.Item(1).Locked);
        Assert.False(app.Documents.Item(2).Layers.Item(1).Locked);
    }

    [Fact]
    public void DuplicateArtboardNamesAreRejectedBeforeMutation()
    {
        var app = MutableApplication.Create();
        app.Documents.Item(1).Artboards.Item(1).Name = "Duplicate";
        app.Documents.Item(1).Artboards.Item(2).Name = "Duplicate";
        var firstBefore =
            app.Documents.Item(1).Artboards.Item(1).ArtboardRect.ToArray();
        var secondBefore =
            app.Documents.Item(1).Artboards.Item(2).ArtboardRect.ToArray();

        var result = Execute(
            app,
            IllustratorTypedMutationOperations.ArtboardSetRectOperation,
            """
            {
              "document":{"index":0},
              "artboard":{"name":"Duplicate"},
              "value":[10,200,310,-20]
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(
            "artboard_selector_ambiguous",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Equal(
            firstBefore,
            app.Documents.Item(1).Artboards.Item(1).ArtboardRect);
        Assert.Equal(
            secondBefore,
            app.Documents.Item(1).Artboards.Item(2).ArtboardRect);
    }

    [Fact]
    public void ArtboardSelectorDriftIsRefusedBeforePropertyPut()
    {
        var app = MutableApplication.Create();
        var artboards = app.Documents.Item(1).Artboards;
        var before = artboards.Item(2).ArtboardRect.ToArray();
        artboards.BeforeItemReturn = (read, artboard) =>
        {
            if (read == 4)
                artboard.Name = "Externally Renamed";
        };

        var result = ExecuteSta(
            app,
            IllustratorTypedMutationOperations.ArtboardSetRectOperation,
            """
            {
              "document":{"index":0},
              "artboard":{"name":"Board B"},
              "value":[10,200,310,-20]
            }
            """);

        Assert.False(result.Ok);
        Assert.Equal(
            "typed_mutation_selector_changed",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Equal(before, artboards.Item(2).ArtboardRect);
    }

    [Fact]
    public void MissingNamedSelectorsRemainDistinctFromAmbiguity()
    {
        var app = MutableApplication.Create();

        var document = Execute(
            app,
            IllustratorTypedMutationOperations.LayerSetVisibleOperation,
            """
            {
              "document":{"name":"Missing.ai"},
              "layer":{"index":0},
              "value":false
            }
            """);
        var layer = Execute(
            app,
            IllustratorTypedMutationOperations.LayerSetVisibleOperation,
            """
            {
              "document":{"index":0},
              "layer":{"name":"Missing"},
              "value":false
            }
            """);
        var artboard = Execute(
            app,
            IllustratorTypedMutationOperations.ArtboardSetRectOperation,
            """
            {
              "document":{"index":0},
              "artboard":{"name":"Missing"},
              "value":[10,200,310,-20]
            }
            """);

        Assert.Equal("document_not_found", document.Error?.Kind);
        Assert.Equal("layer_not_found", layer.Error?.Kind);
        Assert.Equal("artboard_not_found", artboard.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            document.Error?.Execution);
        Assert.Equal(
            ExecutionState.NotStarted,
            layer.Error?.Execution);
        Assert.Equal(
            ExecutionState.NotStarted,
            artboard.Error?.Execution);
        Assert.True(app.Documents.Item(1).Layers.Item(1).Visible);
    }

    [Fact]
    public void TypedLayerMutationTreatsDefinitelyRejectedPutAsNotStarted()
    {
        var app = MutableApplication.Create();
        var layer = app.Documents.Item(1).Layers.Item(1);
        layer.VisibleSetException = new COMException(
            "Illustrator rejected the property put before execution.",
            RpcECallRejected);

        var error = Assert.Throws<ComTool.Hosts.Abstractions.HostAdapterException>(
            () => ExecuteSta(
                app,
                IllustratorTypedMutationOperations.LayerSetVisibleOperation,
                """
                {
                  "document":{"index":0},
                  "layer":{"index":0},
                  "value":false
                }
                """));

        Assert.Equal("host_busy", error.Kind);
        Assert.True(error.Retryable);
        Assert.Equal(ExecutionState.NotStarted, error.Execution);
        Assert.True(layer.Visible);
    }

    [Fact]
    public void TypedLayerMutationTreatsPossiblyAcceptedPutFailureAsAmbiguous()
    {
        var app = MutableApplication.Create();
        var layer = app.Documents.Item(1).Layers.Item(1);
        layer.VisibleSetException = new COMException(
            "Illustrator failed after the property put may have been accepted.",
            RpcEServerException);

        var error = Assert.Throws<ComTool.Hosts.Abstractions.HostAdapterException>(
            () => ExecuteSta(
                app,
                IllustratorTypedMutationOperations.LayerSetVisibleOperation,
                """
                {
                  "document":{"index":0},
                  "layer":{"index":0},
                  "value":false
                }
                """));

        Assert.Equal("host_server_fault", error.Kind);
        Assert.False(error.Retryable);
        Assert.Equal(ExecutionState.Ambiguous, error.Execution);
        Assert.True(layer.Visible);
    }

    private static OperationResult Execute(
        object app,
        string operation,
        string json)
    {
        using var input = JsonDocument.Parse(json);
        return IllustratorTypedMutationOperations.Execute(
            app,
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "typed-mutation-test",
                Operation = operation,
                Input = input.RootElement.Clone()
            });
    }

    private static OperationResult ExecuteSta(
        object app,
        string operation,
        string json)
    {
        OperationResult? result = null;
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                result = Execute(app, operation, json);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            done.Wait(TimeSpan.FromSeconds(10)),
            "STA mutation test timed out.");
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }

        return Assert.IsType<OperationResult>(result);
    }

    private static JsonElement Value(OperationResult result)
    {
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.Value.HasValue);
        return result.Result.Value.Value;
    }

    public sealed class MutableApplication
    {
        private MutableApplication(MutableDocuments documents)
        {
            Documents = documents;
            ActiveDocument = documents.Item(1);
        }

        public MutableDocuments Documents { get; }
        public MutableDocument ActiveDocument { get; }

        public static MutableApplication Create() =>
            new(
                new MutableDocuments(
                [
                    new MutableDocument("First.ai"),
                    new MutableDocument("Second.ai")
                ]));
    }

    public sealed class MutableDocuments
    {
        private readonly MutableDocument[] _items;
        private int _itemReads;

        public MutableDocuments(MutableDocument[] items) => _items = items;

        public int Count => _items.Length;
        public Action<int, MutableDocument>? BeforeItemReturn { get; set; }
        public MutableDocument Item(int oneBasedIndex)
        {
            var item = _items[oneBasedIndex - 1];
            var read = Interlocked.Increment(ref _itemReads);
            BeforeItemReturn?.Invoke(read, item);
            return item;
        }
    }

    public sealed class MutableDocument
    {
        public MutableDocument(string name)
        {
            Name = name;
            Layers = new MutableLayers(
            [
                new MutableLayer("Layer A"),
                new MutableLayer("Layer B")
            ]);
            Artboards = new MutableArtboards(
            [
                new MutableArtboard("Board A", [0, 100, 100, 0]),
                new MutableArtboard("Board B", [200, 100, 300, 0])
            ]);
        }

        public string Name { get; set; }
        public string Path { get; set; } = string.Empty;
        public MutableLayers Layers { get; }
        public MutableArtboards Artboards { get; }
    }

    public sealed class MutableLayers
    {
        private readonly MutableLayer[] _items;
        private int _itemReads;

        public MutableLayers(MutableLayer[] items) => _items = items;

        public int Count => _items.Length;
        public Action<int, MutableLayer>? BeforeItemReturn { get; set; }
        public MutableLayer Item(int oneBasedIndex)
        {
            var item = _items[oneBasedIndex - 1];
            var read = Interlocked.Increment(ref _itemReads);
            BeforeItemReturn?.Invoke(read, item);
            return item;
        }
    }

    public sealed class MutableLayer
    {
        public MutableLayer(string name) => Name = name;

        private bool _visible = true;

        public string Name { get; set; }
        public COMException? VisibleSetException { get; set; }
        public bool Visible
        {
            get => _visible;
            set
            {
                if (VisibleSetException is not null)
                    throw VisibleSetException;
                _visible = value;
            }
        }
        public bool Locked { get; set; }
        public double Opacity { get; set; } = 100d;
    }

    public sealed class MutableArtboards
    {
        private readonly MutableArtboard[] _items;
        private int _itemReads;

        public MutableArtboards(MutableArtboard[] items) => _items = items;

        public int Count => _items.Length;
        public Action<int, MutableArtboard>? BeforeItemReturn { get; set; }
        public MutableArtboard Item(int oneBasedIndex)
        {
            var item = _items[oneBasedIndex - 1];
            var read = Interlocked.Increment(ref _itemReads);
            BeforeItemReturn?.Invoke(read, item);
            return item;
        }
    }

    public sealed class MutableArtboard
    {
        public MutableArtboard(string name, double[] rect)
        {
            Name = name;
            ArtboardRect = rect;
        }

        public string Name { get; set; }
        public double[] ArtboardRect { get; set; }
    }
}
