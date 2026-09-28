namespace ComTool.Knowledge.Tests;

public sealed class KnowledgePackReaderTests
{
    [Fact]
    public void EmbeddedHeaderPreservesAuthoritativeProvenanceWithoutBuildMachineGuessing()
    {
        using var reader = KnowledgePackReader.OpenEmbedded();

        Assert.Equal(KnowledgePackFormat.FormatId, reader.Header.Format);
        Assert.Equal(KnowledgePackFormat.Version, reader.Header.Version);
        Assert.Equal("sqlite-index", reader.Header.Source.Kind);
        Assert.Equal(
            "9c8ab3613afa727d0d728f583d6dbf31d8fb282db39ff0ada0ad3e81aeb3ec72",
            reader.Header.Source.DatabaseSha256);
        Assert.Equal(
            "fa8baefb8899eb60500789512c0a0eb07999e137326404f9399956c292dc6db7",
            reader.Header.Source.JsonSha256);
        Assert.True(reader.Header.Source.JsonOnDiskMatchesManifest);
        Assert.Equal("illustrator", reader.Header.Host.Family);
        Assert.Equal(
            KnowledgeHostProvenance.UnknownVersion,
            reader.Header.Host.Version);
        Assert.False(reader.Header.Host.VersionKnown);
        Assert.False(reader.Header.BuildEnvironment.Authoritative);
        Assert.Null(reader.Header.BuildEnvironment.IllustratorProduct);
        Assert.Null(reader.Header.BuildEnvironment.IllustratorProductVersion);
        Assert.Equal(224, reader.Header.Counts.Interfaces);
        Assert.Equal(670, reader.Header.Counts.Methods);
        Assert.Equal(2100, reader.Header.Counts.Properties);
        Assert.Equal(138, reader.Header.Counts.Enums);
        Assert.Equal(678, reader.Header.Counts.EnumValues);
    }

    [Fact]
    public void EmbeddedBodyMatchesDeclaredLengthAndSha()
    {
        using var reader = KnowledgePackReader.OpenEmbedded();

        reader.VerifyIntegrity();

        Assert.False(reader.ScanBudgetExhausted);
        Assert.Equal(566322, reader.Header.BodyBytes);
        Assert.Equal(
            "20913a8f2642b6b801ea2bc325a85388edf7c02b3ac7bc50c04694ba9f12d952",
            reader.Header.BodySha256);
    }

    [Fact]
    public void MultiSectionSelectionReturnsMethodsAndProperties()
    {
        using var reader = KnowledgePackReader.OpenEmbedded();

        var result = reader.Select(
            new KnowledgeSelection
            {
                InterfaceName = "_Application",
                IncludeMethods = true,
                IncludeProperties = true
            });

        Assert.Contains(
            result.Methods,
            method => method.Name == "DoScript" &&
                      method.ReturnType == "VT_VOID");
        Assert.Contains(
            result.Properties,
            property => property.Name == "UserInteractionLevel");
        Assert.False(result.RetentionCapReached);
        Assert.False(reader.ScanBudgetExhausted);
    }

    [Fact]
    public void MemberPassAttachesDoScriptParameterSignature()
    {
        using var reader = KnowledgePackReader.OpenEmbedded();

        var selected = reader.Select(
            new KnowledgeSelection
            {
                InterfaceName = "_Application",
                SymbolName = "DoScript",
                IncludeMethods = true
            });

        var method = Assert.Single(selected.Methods);
        Assert.Equal(629, method.Id);
        Assert.Equal(1, method.OptionalParameterCount);

        var members = reader.LoadMembers(
            new HashSet<int> { method.Id },
            new HashSet<int>(),
            includeAccessorParameters: false);

        var parameters = Assert.Single(members.MethodParameters).Value;
        Assert.Collection(
            parameters.OrderBy(parameter => parameter.Position),
            parameter =>
            {
                Assert.Equal("Action", parameter.Name);
                Assert.Equal("VT_BSTR", parameter.Type);
            },
            parameter =>
            {
                Assert.Equal("From", parameter.Name);
                Assert.Equal("VT_BSTR", parameter.Type);
            },
            parameter =>
            {
                Assert.Equal("Dialogs", parameter.Name);
                Assert.Equal("VT_VARIANT", parameter.Type);
                Assert.Contains(
                    "PARAMFLAG_FOPT",
                    parameter.Flags ?? string.Empty,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void EnumSelectionFindsSaveOptionsWithoutHostVersionGuessing()
    {
        using var reader = KnowledgePackReader.OpenEmbedded();

        var result = reader.Select(
            new KnowledgeSelection
            {
                SymbolName = "aiSaveChanges",
                IncludeEnumValues = true
            });

        var value = Assert.Single(result.EnumValues);
        Assert.Equal("AiSaveOptions", value.EnumName);
        Assert.Equal("aiSaveChanges", value.Name);
        Assert.Equal(1, value.Value);
        Assert.Equal(
            KnowledgeHostProvenance.UnknownVersion,
            reader.Header.Host.Version);
    }

    [Fact]
    public void RetentionCapActuallyBoundsReturnedRecords()
    {
        using var reader = KnowledgePackReader.OpenEmbedded(
            new KnowledgeScanOptions
            {
                MaxRetainedRecords = 1
            });

        var result = reader.Select(
            new KnowledgeSelection
            {
                InterfaceName = "_Application",
                IncludeMethods = true
            });

        Assert.Single(result.Methods);
        Assert.True(result.RetentionCapReached);
        Assert.True(reader.ScanBudgetExhausted);
    }
}
