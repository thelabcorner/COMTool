using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Hosts.Illustrator;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorComReadBridgeTests
{
    [Fact]
    public void GetReadsPrimitiveProperty()
    {
        var root = FakeApplication.Create();

        var result = IllustratorComReadBridge.Get(
            root,
            "Version");

        Assert.Equal(JsonValueKind.String, result.ValueKind);
        Assert.Equal("30.6-test", result.GetString());
    }

    [Fact]
    public void GetTranslatesZeroBasedIndexToOneBasedItem()
    {
        var root = FakeApplication.Create();

        var result = IllustratorComReadBridge.Get(
            root,
            "Documents[0].Name");

        Assert.Equal("First.ai", result.GetString());
        Assert.Equal(1, root.Documents.LastRequestedItemIndex);
    }

    [Fact]
    public void GetRejectsNegativeIndex()
    {
        var root = FakeApplication.Create();

        var error = Assert.Throws<ComAutomationPathException>(
            () => IllustratorComReadBridge.Get(
                root,
                "Documents[-1].Name"));

        Assert.Equal("invalid_com_path", error.Kind);
    }

    [Fact]
    public void GetRejectsPathBeyondSharedRuntimeBound()
    {
        var root = FakeApplication.Create();
        var path = string.Join(
            ".",
            Enumerable.Repeat(
                "Documents",
                ComAutomationPath.MaxPathSegments + 1));

        var error = Assert.Throws<ComAutomationPathException>(
            () => IllustratorComReadBridge.Get(root, path));

        Assert.Equal("com_path_too_deep", error.Kind);
    }

    [Fact]
    public void GetRejectsIndexThatCannotBeTranslatedToOneBasedComIndex()
    {
        var root = FakeApplication.Create();

        var error = Assert.Throws<ComAutomationPathException>(
            () => IllustratorComReadBridge.Get(
                root,
                $"Documents[{int.MaxValue}].Name"));

        Assert.Equal("invalid_com_path", error.Kind);
    }

    [Fact]
    public void GetReportsMissingMemberAsHostAdapterFailure()
    {
        var root = FakeApplication.Create();

        var error = Assert.Throws<HostAdapterException>(
            () => IllustratorComReadBridge.Get(
                root,
                "DefinitelyMissing"));

        Assert.Equal("com_member_not_found", error.Kind);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void CallReadInvokesAllowlistedQuery()
    {
        var root = FakeApplication.Create();

        using var args = JsonDocument.Parse("[]");
        var result = IllustratorComReadBridge.CallRead(
            root,
            "ActiveDocument.Artboards.GetActiveArtboardIndex",
            args.RootElement);

        Assert.Equal(JsonValueKind.Number, result.ValueKind);
        Assert.Equal(2, result.GetInt32());
    }

    [Fact]
    public void CallReadConvertsJsonArguments()
    {
        var root = FakeApplication.Create();

        using var args = JsonDocument.Parse("[\"Layer 7\"]");
        var result = IllustratorComReadBridge.CallRead(
            root,
            "ActiveDocument.Artboards.GetByName",
            args.RootElement);

        Assert.Equal(JsonValueKind.String, result.ValueKind);
        Assert.Equal("found:Layer 7", result.GetString());
    }

    [Fact]
    public void CallReadRejectsUnclassifiedMethod()
    {
        var root = FakeApplication.Create();

        using var args = JsonDocument.Parse("[]");

        var error = Assert.Throws<ArgumentException>(
            () => IllustratorComReadBridge.CallRead(
                root,
                "ActiveDocument.Save",
                args.RootElement));

        Assert.Contains("read-only allowlist", error.Message);
        Assert.Equal(0, root.ActiveDocument.SaveCallCount);
    }

    [Fact]
    public void CallReadRejectsObjectArguments()
    {
        var root = FakeApplication.Create();

        using var args = JsonDocument.Parse("[{\"x\":1}]");

        Assert.Throws<ArgumentException>(
            () => IllustratorComReadBridge.CallRead(
                root,
                "ActiveDocument.Artboards.GetByName",
                args.RootElement));
    }
}

public sealed class FakeApplication
{
    private FakeApplication(
        FakeDocuments documents,
        FakeDocument activeDocument)
    {
        Documents = documents;
        ActiveDocument = activeDocument;
    }

    public string Version { get; } = "30.6-test";

    public FakeDocuments Documents { get; }

    public FakeDocument ActiveDocument { get; }

    public static FakeApplication Create()
    {
        var first = new FakeDocument("First.ai");
        var second = new FakeDocument("Second.ai");
        var documents = new FakeDocuments([first, second]);

        return new FakeApplication(
            documents,
            new FakeDocument("Active.ai"));
    }
}

public sealed class FakeDocuments
{
    private readonly FakeDocument[] _items;

    public FakeDocuments(FakeDocument[] items)
    {
        _items = items;
    }

    public int Count => _items.Length;

    public int? LastRequestedItemIndex { get; private set; }

    public FakeDocument Item(int index)
    {
        LastRequestedItemIndex = index;
        return _items[index - 1];
    }
}

public sealed class FakeDocument
{
    public FakeDocument(string name)
    {
        Name = name;
        Artboards = new FakeArtboards();
        Layers = new FakeLayers();
    }

    public string Name { get; }

    public string Path { get; init; } = string.Empty;

    public FakeArtboards Artboards { get; }

    public FakeLayers Layers { get; }

    public int SaveCallCount { get; private set; }

    public void Save() => SaveCallCount++;
}

public sealed class FakeArtboards
{
    private readonly FakeArtboard[] _items =
    [
        new FakeArtboard("Artboard 1", 0d, 100d, 100d, 0d),
        new FakeArtboard("Artboard 2", 200d, 100d, 300d, 0d)
    ];

    public int Count => _items.Length;

    public FakeArtboard Item(int index) => _items[index - 1];

    public int GetActiveArtboardIndex() => 2;

    public string GetByName(string name) => $"found:{name}";
}

public sealed class FakeArtboard
{
    public FakeArtboard(
        string name,
        double left,
        double top,
        double right,
        double bottom)
    {
        Name = name;
        ArtboardRect = new[] { left, top, right, bottom };
    }

    public string Name { get; }

    public double[] ArtboardRect { get; }

    public double[] RulerOrigin { get; } = [0d, 0d];

    public int RulerPAR { get; } = 1;
}

public sealed class FakeLayers
{
    private readonly FakeLayer[] _items =
    [
        new FakeLayer("Layer 1"),
        new FakeLayer("Layer 2")
    ];

    public int Count => _items.Length;

    public FakeLayer Item(int index) => _items[index - 1];
}

public sealed class FakeLayer
{
    public FakeLayer(string name)
    {
        Name = name;
        PathItems = new FakePathItems();
    }

    public string Name { get; }

    public bool Visible { get; init; } = true;

    public bool Locked { get; init; }

    public bool IsTemplate { get; init; }

    public bool Preview { get; init; } = true;

    public double Opacity { get; init; } = 100d;

    public string Uuid { get; init; } = "00000000-0000-0000-0000-000000000000";

    public FakePathItems PathItems { get; }
}

public sealed class FakePathItems
{
    public int Count => 0;
}
