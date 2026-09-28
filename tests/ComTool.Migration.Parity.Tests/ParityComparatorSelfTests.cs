using System.Text.Json;

namespace ComTool.Migration.Parity.Tests;

/// <summary>
/// Negative controls. A parity harness that cannot fail is worthless, so
/// these tests mutate a known-good comparison in one specific way at a time
/// and prove the comparator rejects it.
/// </summary>
public sealed class ParityComparatorSelfTests
{
    [Fact]
    public void TheCanonicalProjectionHasNineDistinctFields()
    {
        Assert.Equal(9, SemanticProjection.Fields.Length);
        Assert.Equal(
            SemanticProjection.Fields.Length,
            SemanticProjection.Fields.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MatchingProjectionIsAccepted()
    {
        var left = Projection(("outcome", "success"), ("payloadKind", "scalar"));
        var right = Projection(("outcome", "success"), ("payloadKind", "scalar"));

        var comparison = SemanticProjection.Compare(
            left,
            right,
            SemanticProjection.Fields,
            []);

        Assert.Empty(comparison.UnaccountedFields);
        Assert.Empty(comparison.EquivalenceViolations);
        Assert.Empty(comparison.DivergenceViolations);
    }

    [Fact]
    public void MismatchedDeclaredEquivalenceIsRejected()
    {
        var left = Projection(("outcome", "success"));
        var right = Projection(("outcome", "failure"));

        var comparison = SemanticProjection.Compare(
            left,
            right,
            [SemanticProjection.Outcome],
            []);

        Assert.False(comparison.Passed);
        Assert.NotEmpty(comparison.EquivalenceViolations);
    }

    [Fact]
    public void UnaccountedFieldIsRejected()
    {
        var left = Projection(("outcome", "success"));
        var right = Projection(("outcome", "success"));

        var comparison = SemanticProjection.Compare(left, right, [], []);

        Assert.False(comparison.Passed);
        Assert.NotEmpty(comparison.UnaccountedFields);
    }

    [Fact]
    public void StaleDivergenceDeclarationIsRejected()
    {
        var left = Projection(("outcome", "failure"));
        var right = Projection(("outcome", "success"));

        var comparison = SemanticProjection.Compare(
            left,
            right,
            [],
            [new DeclaredDivergence(
                SemanticProjection.Outcome,
                "success",
                "failure",
                "declared backwards on purpose")]);

        Assert.False(comparison.Passed);
        Assert.NotEmpty(comparison.DivergenceViolations);
    }

    [Fact]
    public void RedundantDivergenceIsRejected()
    {
        var left = Projection(("outcome", "success"));
        var right = Projection(("outcome", "success"));

        var comparison = SemanticProjection.Compare(
            left,
            right,
            [],
            [new DeclaredDivergence(
                SemanticProjection.Outcome,
                "success",
                "success",
                "declared divergent but the sides agree")]);

        Assert.False(comparison.Passed);
        Assert.NotEmpty(comparison.DivergenceViolations);
    }

    [Fact]
    public void FieldDeclaredBothEquivalentAndDivergentIsRejected()
    {
        var left = Projection(("outcome", "success"));
        var right = Projection(("outcome", "success"));

        var comparison = SemanticProjection.Compare(
            left,
            right,
            [SemanticProjection.Outcome],
            [new DeclaredDivergence(
                SemanticProjection.Outcome,
                "success",
                "success",
                "double declared on purpose")]);

        Assert.False(comparison.Passed);
        Assert.NotEmpty(comparison.Conflicts);
    }

    [Fact]
    public void UnknownProjectionFieldNameIsRejected()
    {
        var left = Projection(("outcome", "success"));
        var right = Projection(("outcome", "success"));

        var comparison = SemanticProjection.Compare(
            left,
            right,
            ["notAField"],
            []);

        Assert.False(comparison.Passed);
        Assert.NotEmpty(comparison.EquivalenceViolations);
    }

    [Fact]
    public void LegacyEnvelopeWithoutResultProjectsPayloadNone()
    {
        using var document = JsonDocument.Parse(
            """{"ok":false,"op":"status","error":"nope","hresult":-1}""");

        var projection = SemanticProjection.FromLegacy(document.RootElement);

        Assert.Equal("failure", projection[SemanticProjection.Outcome]);
        Assert.Equal("none", projection[SemanticProjection.PayloadKind]);
        Assert.Equal("none", projection[SemanticProjection.ValueType]);
        Assert.Equal("true", projection[SemanticProjection.HostFailureIdentified]);
        Assert.Equal(
            SemanticProjection.Absent,
            projection[SemanticProjection.Retryable]);
        Assert.Equal(
            SemanticProjection.Absent,
            projection[SemanticProjection.Execution]);
        Assert.Equal(
            SemanticProjection.Absent,
            projection[SemanticProjection.TargetIdentity]);
    }

    [Fact]
    public void V2ResultWithStrongTargetProjectsStrongIdentity()
    {
        using var request = JsonDocument.Parse(
            """
            {"protocolVersion":1,"id":"x","target":{"host":"illustrator","id":"i:1","generation":7},"operation":"core.target.status","input":{}}
            """);
        using var result = JsonDocument.Parse(
            """
            {"protocolVersion":1,"id":"x","operation":"core.target.status","ok":true,"status":"completed","targetState":"known","result":{"kind":"object","value":{}}}
            """);

        var projection = SemanticProjection.FromV2(
            request.RootElement,
            result.RootElement);

        Assert.Equal("success", projection[SemanticProjection.Outcome]);
        Assert.Equal("structure", projection[SemanticProjection.PayloadKind]);
        Assert.Equal("object", projection[SemanticProjection.ValueType]);
        Assert.Equal("strong", projection[SemanticProjection.TargetIdentity]);
        Assert.Equal("known", projection[SemanticProjection.TargetState]);
    }

    [Fact]
    public void LegacyTempFileOffloadProjectsArtifactReference()
    {
        using var document = JsonDocument.Parse(
            """
            {"ok":true,"op":"get","result":{"_note":"result too large","path":"C:/tmp/x.json","bytes":40000}}
            """);

        Assert.Equal(
            "artifact_reference",
            SemanticProjection.FromLegacy(document.RootElement)[
                SemanticProjection.PayloadKind]);
    }

    [Fact]
    public void LegacyNestedErrorProjectsHostFailure()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "ok":true,
              "op":"status",
              "result":{
                "Name":"Adobe Illustrator",
                "ActiveDocument":{
                  "Name":{"_error":"RPC_E_CALL_REJECTED","hresult":-2147418111}
                }
              }
            }
            """);

        var projection = SemanticProjection.FromLegacy(document.RootElement);

        Assert.Equal("success", projection[SemanticProjection.Outcome]);
        Assert.Equal(
            "true",
            projection[SemanticProjection.HostFailureIdentified]);
    }

    private static IReadOnlyDictionary<string, string> Projection(
        params (string Field, string Value)[] assigned)
    {
        var projection = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in SemanticProjection.Fields)
            projection[field] = SemanticProjection.Absent;

        foreach (var (field, value) in assigned)
            projection[field] = value;

        return projection;
    }
}
