using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Runtime.Tests;

/// <summary>
/// Focused tests for the advisory ES3 pre-flight analyzer behind
/// <c>script.validate</c>.
///
/// The legacy <c>comtool/es3check.py</c> checker is the behavioral oracle for
/// which constructs are reported and with which line; the V2-specific tests
/// below pin the semantics that make this an advisory analyzer rather than
/// engine proof: findings never fail the operation, findings never carry
/// source text, ordering is canonical, and no auto-return routing exists.
/// </summary>
public sealed class ScriptEs3PreflightTests
{
    private static ScriptValidateAnalysis Analyze(
        string source,
        string kind = "code") =>
        ScriptEs3Preflight.Analyze(new ScriptValidateRequest(kind, source));

    private static IReadOnlyList<ScriptValidateFinding> Rules(
        string source,
        string kind = "code") =>
        Analyze(source, kind).Findings;

    private static ScriptValidateFinding Single(
        string source,
        string kind = "code") =>
        Assert.Single(Rules(source, kind));

    private static ScriptValidateFinding Grammar(
        string source,
        string kind = "code") =>
        Assert.Single(
            Rules(source, kind),
            static finding =>
                finding.Category ==
                ScriptEs3Preflight.CategoryEs3Grammar);

    // ------------------------------------------------------------------
    // Oracle parity: clean ES3 source produces no findings.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("var x = 1;\nreturn x + 1;")]
    [InlineData("function f(a) { return a * 2; }\nreturn f(21);")]
    [InlineData("return app.documents.length;")]
    [InlineData("for (var i = 0; i < 10; i++) { app.activeDocument.artboards[i].name; }")]
    [InlineData("try { app.documents[0].save(); } catch (e) { }")]
    [InlineData("var o = {a: 1, 'b': 2, \"c\": 3}; return o.a;")]
    public void CleanEs3SourceIsClean(string source)
    {
        var analysis = Analyze(source);

        Assert.True(analysis.Clean);
        Assert.Empty(analysis.Findings);
        Assert.False(analysis.FindingsTruncated);
    }

    [Fact]
    public void EmptyAndBlankSourceIsClean()
    {
        Assert.True(Analyze("").Clean);
        Assert.True(Analyze("   \n  ").Clean);
    }

    // ------------------------------------------------------------------
    // Oracle parity: ES3 grammar violations.
    // ------------------------------------------------------------------

    [Fact]
    public void LetAndConstAreReportedOnTheOffendingLine()
    {
        var finding = Single("var a = 1;\nlet b = 2;\nreturn b;");

        Assert.Equal(ScriptEs3Preflight.RuleBlockScopedDeclaration, finding.Rule);
        Assert.Equal(2, finding.Line);
        Assert.Equal(1, finding.Column);
        Assert.Equal(3, finding.EndColumn);
        Assert.Contains("let", finding.Message, StringComparison.Ordinal);
        Assert.Equal("use `var`", finding.Fix);
    }

    [Fact]
    public void ConstIsReportedDistinctlyFromLet()
    {
        var finding = Single("const b = 2;\nreturn b;");

        Assert.Equal(ScriptEs3Preflight.RuleBlockScopedDeclaration, finding.Rule);
        Assert.Contains("const", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return (x) => x + 1;", "arrow")]
    [InlineData("return `hi ${name}`;", "template")]
    [InlineData("class Foo {}\nreturn 1;", "class")]
    [InlineData("return [...args];", "spread")]
    public void GrammarLabelsMatchTheLegacyOracle(string source, string label)
    {
        var finding = Grammar(source);

        Assert.Contains(label, finding.Message, StringComparison.Ordinal);
        Assert.Equal("error", finding.Severity);
        Assert.Equal(ScriptEs3Preflight.CategoryEs3Grammar, finding.Category);
    }

    [Fact]
    public void ForOfIsReportedOnTheHeaderLine()
    {
        var finding = Grammar("for (var i of arr) {}\nreturn 1;");

        Assert.Equal(ScriptEs3Preflight.RuleForOf, finding.Rule);
        Assert.Equal(1, finding.Line);
        Assert.Equal(12, finding.Column);
        Assert.Contains("for...of", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return a?.b;")]
    [InlineData("return a ?? b;")]
    public void OptionalChainingAndNullishAreReported(string source)
    {
        var finding = Grammar(source);

        Assert.Contains(
            finding.Rule,
            new[]
            {
                ScriptEs3Preflight.RuleOptionalChaining,
                ScriptEs3Preflight.RuleNullishCoalescing
            });
    }

    [Fact]
    public void TernaryWithBareFloatIsNotOptionalChaining()
    {
        Assert.True(Analyze("return x ? .5 : 0;").Clean);
    }

    [Theory]
    [InlineData("async function f() {}\nreturn 1;")]
    [InlineData("return await x;")]
    public void AsyncAndAwaitAreReported(string source)
    {
        Assert.Contains(
            Rules(source),
            static finding =>
                finding.Category ==
                ScriptEs3Preflight.CategoryEs3Grammar);
    }

    [Fact]
    public void YieldIsReported()
    {
        Assert.Equal(
            ScriptEs3Preflight.RuleYield,
            Grammar("function* g() { yield 1; }\nreturn 1;").Rule);
    }

    // ------------------------------------------------------------------
    // Oracle parity: strings and comments never produce false positives.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("return \"let me know\";")]
    [InlineData("return 'const x = 1;';")]
    [InlineData("// const x = 1;\nreturn 1;")]
    [InlineData("/* let x = 1; */\nreturn 1;")]
    [InlineData("return \"a?.b\";")]
    [InlineData("return '=>';")]
    [InlineData("return \"`\";")]
    [InlineData("var s = \"line1\nline2\";\nreturn s.length;")]
    [InlineData("/* multi\nline let x = 1; */\nreturn 1;")]
    public void QuotedAndCommentedTextIsNotScanned(string source)
    {
        var analysis = Analyze(source);

        Assert.True(
            analysis.Clean,
            string.Join(
                "; ",
                analysis.Findings.Select(static f => f.Message)));
    }

    // ------------------------------------------------------------------
    // Oracle parity: directive, unterminated, and balance findings.
    // ------------------------------------------------------------------

    [Fact]
    public void PreprocessorDirectiveInSnippetIsReportedOnceOnItsLine()
    {
        var finding = Single("#target illustrator\nreturn 1;");

        Assert.Equal(
            ScriptEs3Preflight.RulePreprocessorDirective,
            finding.Rule);
        Assert.Equal(1, finding.Line);
        Assert.Contains("directive", finding.Message, StringComparison.Ordinal);
        Assert.Equal(
            ScriptEs3Preflight.CategoryPreprocessorPlacement,
            finding.Category);
        Assert.Equal("warning", finding.Severity);
    }

    [Theory]
    [InlineData("return (1 + 2;")]
    [InlineData("return [1, 2;")]
    [InlineData("return {a: 1;")]
    public void UnbalancedCloserIsReported(string source)
    {
        var finding = Assert.Single(
            Rules(source),
            static candidate =>
                candidate.Rule ==
                ScriptEs3Preflight.RuleDelimiterBalance);

        Assert.Contains("unbalanced", finding.Message, StringComparison.Ordinal);
        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void NeverClosedOpenerIsReportedAtItsOpeningPosition()
    {
        var finding = Single("function f() {\n  return 1;");

        Assert.Equal(
            ScriptEs3Preflight.RuleDelimiterBalance, finding.Rule);
        Assert.Equal(1, finding.Line);
        Assert.Equal(14, finding.Column);
        Assert.Contains("never closed", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return \"truncated;")]
    [InlineData("return 'truncated;")]
    [InlineData("return 1; /* never closed")]
    [InlineData("return `truncated;")]
    public void UnterminatedConstructIsReported(string source)
    {
        var finding = Assert.Single(
            Rules(source),
            static candidate =>
                candidate.Rule ==
                ScriptEs3Preflight.RuleUnterminated);

        Assert.Contains("unterminated", finding.Message, StringComparison.Ordinal);
        Assert.Equal(
            ScriptEs3Preflight.CategoryUnterminated,
            finding.Category);
    }

    [Fact]
    public void EscapeSequencesDoNotConfuseTheScanner()
    {
        Assert.True(Analyze("return \"a\\\"; let x = 1;\";").Clean);
        Assert.True(Analyze("return 'it\\'s const x = 1;';").Clean);
    }

    // ------------------------------------------------------------------
    // Deliberate divergences from the legacy per-line regex scanner.
    // ------------------------------------------------------------------

    [Fact]
    public void DeclarationKeywordSplitAcrossLinesIsStillReported()
    {
        // The legacy scanner matched per line, so `let` followed by a newline
        // was missed. The whole-text scan reports it at the keyword.
        var finding = Grammar("let\n  b = 2;\nreturn b;");

        Assert.Equal(ScriptEs3Preflight.RuleBlockScopedDeclaration, finding.Rule);
        Assert.Equal(1, finding.Line);
    }

    [Fact]
    public void MemberAccessIsNotTreatedAsADeclaration()
    {
        Assert.True(Analyze("obj.let;\nreturn 1;").Clean);
        Assert.True(Analyze("return obj.await;").Clean);
        Assert.DoesNotContain(
            Rules("return obj?.async;"),
            static finding =>
                finding.Rule == ScriptEs3Preflight.RuleAsyncFunction);
    }

    [Fact]
    public void ForHeaderOfIsOnlyReportedInTheInitializer()
    {
        Assert.NotEmpty(Rules("for (var i of f(x)) {}\nreturn 1;"));
        Assert.True(
            Analyze("for (var i = 0; i < arr.of; i++) {}\nreturn 1;").Clean);
        Assert.True(
            Analyze("for (var k in obj) {}\nreturn 1;").Clean);
    }

    [Fact]
    public void BackslashLineContinuationKeepsTheStringOpen()
    {
        var analysis = Analyze("return \"a\\\nb\";");

        Assert.DoesNotContain(
            analysis.Findings,
            static finding =>
                finding.Rule == ScriptEs3Preflight.RuleUnterminated);
    }

    // ------------------------------------------------------------------
    // Kind-aware shape advisories. No auto-return routing exists.
    // ------------------------------------------------------------------

    [Fact]
    public void ExpressionKindAcceptsASingleExpression()
    {
        Assert.True(Analyze("app.documents.length", "expression").Clean);
        Assert.True(Analyze("  app.documents\n  .length  ", "expression").Clean);
        Assert.True(Analyze("x = 5", "expression").Clean);
        Assert.True(Analyze("x ? y : z", "expression").Clean);
    }

    [Theory]
    [InlineData("var x = 1;", "statement keyword 'var'")]
    [InlineData("a; b", "statement separator ';'")]
    [InlineData("if (x) y", "statement keyword 'if'")]
    [InlineData("f();", "statement separator ';'")]
    public void ExpressionKindReportsShapeDisagreement(
        string source,
        string expectedReason)
    {
        var finding = Single(source, "expression");

        Assert.Equal(
            ScriptEs3Preflight.RuleExpressionKindNotSingleExpression,
            finding.Rule);
        Assert.Equal(
            ScriptEs3Preflight.CategorySnippetShape,
            finding.Category);
        Assert.Contains(expectedReason, finding.Message, StringComparison.Ordinal);
        Assert.Equal("use kind 'code' for a statement body", finding.Fix);
    }

    [Fact]
    public void CodeKindWithoutReturnIsInformationalOnly()
    {
        var finding = Single("app.documents.length;", "code");

        Assert.Equal(ScriptEs3Preflight.RuleCodeBodyWithoutReturn, finding.Rule);
        Assert.Equal("info", finding.Severity);
        Assert.Equal(1, finding.Line);
    }

    [Fact]
    public void CodeKindWithReturnHasNoShapeAdvisory()
    {
        Assert.True(Analyze("return app.documents.length;", "code").Clean);
    }

    [Fact]
    public void ShapeAdvisoryDoesNotSuppressGrammarFindings()
    {
        var findings = Rules("var x = 1;\nlet y = 2;", "expression");

        Assert.Contains(
            findings,
            static finding =>
                finding.Rule ==
                ScriptEs3Preflight.RuleExpressionKindNotSingleExpression);
        Assert.Contains(
            findings,
            static finding =>
                finding.Rule ==
                ScriptEs3Preflight.RuleBlockScopedDeclaration);
    }

    // ------------------------------------------------------------------
    // Advisory semantics: never engine proof, never a failure.
    // ------------------------------------------------------------------

    [Fact]
    public void FindingsAreASuccessfulOperationWithCleanFalse()
    {
        var analysis = Analyze("var x = 1;\nlet y = 2;\nreturn y;");
        var result = ScriptEs3Preflight.BuildResult(
            Request(),
            analysis,
            totalMs: 0.25);

        Assert.True(result.Ok);
        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(TargetState.Known, result.TargetState);
        Assert.Null(result.Error);

        var payload = Payload(result);
        Assert.False(payload.GetProperty("clean").GetBoolean());
        Assert.True(payload.GetProperty("advisory").GetBoolean());
        Assert.False(payload.GetProperty("authoritative").GetBoolean());
        Assert.False(payload.GetProperty("engineProof").GetBoolean());
        Assert.Equal(
            ScriptEs3Preflight.Ruleset,
            payload.GetProperty("ruleset").GetString());
        Assert.Equal(
            ScriptEs3Preflight.Analyzer,
            payload.GetProperty("analyzer").GetString());
        Assert.Contains(
            "Advisory static analysis only",
            payload.GetProperty("disclaimer").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(0.25, result.Timing?.TotalMs);
    }

    [Fact]
    public void CleanSourceIsASuccessfulOperationWithCleanTrue()
    {
        var result = ScriptEs3Preflight.BuildResult(
            Request(),
            Analyze("return app.documents.length;"),
            totalMs: 0.1);

        Assert.True(result.Ok);
        Assert.Equal(OperationStatus.Completed, result.Status);

        var payload = Payload(result);
        Assert.True(payload.GetProperty("clean").GetBoolean());
        Assert.Equal(0, payload.GetProperty("findingCount").GetInt32());
        Assert.Equal(
            0,
            payload.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void PayloadNeverCarriesSourceContentOrAExcerpt()
    {
        const string marker = "SECRET_CALLER_TOKEN_4711";
        var result = ScriptEs3Preflight.BuildResult(
            Request(),
            Analyze($"let {marker} = 1;\nreturn {marker};"),
            totalMs: 0.1);

        var serialized = JsonSerializer.Serialize(Payload(result));

        Assert.DoesNotContain(marker, serialized, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "excerpt",
                     "snippet",
                     "sourceText",
                     "lineText",
                     "rewritten",
                     "wrapped",
                     "autoReturn"
                 })
        {
            Assert.DoesNotContain(
                forbidden,
                serialized,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PayloadFindingShapeIsLineColumnSpanAndRuleMetadata()
    {
        var result = ScriptEs3Preflight.BuildResult(
            Request(),
            Analyze("var a = 1;\nlet b = 2;\nreturn b;"),
            totalMs: 0.1);

        var finding = Payload(result)
            .GetProperty("findings")[0];

        Assert.Equal(
            ScriptEs3Preflight.RuleBlockScopedDeclaration,
            finding.GetProperty("rule").GetString());
        Assert.Equal(
            ScriptEs3Preflight.CategoryEs3Grammar,
            finding.GetProperty("category").GetString());
        Assert.Equal("error", finding.GetProperty("severity").GetString());
        Assert.Equal(2, finding.GetProperty("line").GetInt32());
        Assert.Equal(1, finding.GetProperty("column").GetInt32());
        Assert.Equal(2, finding.GetProperty("endLine").GetInt32());
        Assert.Equal(3, finding.GetProperty("endColumn").GetInt32());
        Assert.False(
            string.IsNullOrWhiteSpace(
                finding.GetProperty("message").GetString()));
        Assert.Equal("use `var`", finding.GetProperty("fix").GetString());
    }

    // ------------------------------------------------------------------
    // Determinism, bounds, and cancellation.
    // ------------------------------------------------------------------

    [Fact]
    public void AnalysisIsDeterministicForTheSameBytes()
    {
        const string source = "#target illustrator\nvar a = 1;\nlet b = 2;\nreturn a?.b ?? b;";

        var first = Analyze(source, "code");
        var second = Analyze(source, "code");

        Assert.Equal(
            first.Findings
                .Select(static f => (f.Rule, f.Line, f.Column, f.Message))
                .ToArray(),
            second.Findings
                .Select(static f => (f.Rule, f.Line, f.Column, f.Message))
                .ToArray());
    }

    [Fact]
    public void FindingsAreCanonicallyOrderedByPositionThenRule()
    {
        var findings = Rules(
            "let a = 1;\nreturn a?.b;\nvar c = `x`;",
            "code");

        var positions = findings
            .Select(static f => (f.Line, f.Column))
            .ToArray();

        Assert.Equal(
            positions.OrderBy(static p => p.Line).ThenBy(static p => p.Column),
            positions);
    }

    [Fact]
    public void FindingCountIsBoundedAndTruncationIsTruthful()
    {
        var source = string.Concat(
            Enumerable.Repeat("...", 400));

        var analysis = Analyze(source);

        Assert.Equal(
            ScriptEs3Preflight.MaxFindings,
            analysis.Findings.Count);
        Assert.True(analysis.FindingsTruncated);
        Assert.False(analysis.Clean);
    }

    [Fact]
    public void AnalysisIsSinglePassAndLinearOnLargeInput()
    {
        var source =
            string.Concat(Enumerable.Repeat("var value = 1;\n", 20_000)) +
            "return 1;";

        var analysis = Analyze(source);

        Assert.True(analysis.Clean);
        Assert.Equal(20_001, analysis.SourceLineCount);
        Assert.Equal(source.Length, analysis.SourceLength);
    }

    [Fact]
    public void CancellationIsObservedDuringAnalysis()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ScriptEs3Preflight.Analyze(
                new ScriptValidateRequest("code", "return 1;"),
                cancellation.Token));
    }

    // ------------------------------------------------------------------
    // Request contract.
    // ------------------------------------------------------------------

    [Fact]
    public void ParseAcceptsExplicitKindAndReadOnlyEffects()
    {
        using var document = JsonDocument.Parse(
            """
            {"kind":"expression","source":"app.documents.length","effects":"read_only"}
            """);

        var request = ScriptEs3Preflight.ParseRequest(document.RootElement);

        Assert.Equal("expression", request.Kind);
        Assert.Equal("app.documents.length", request.Source);
    }

    [Theory]
    [InlineData("""{"source":"return 1;"}""")]
    [InlineData("""{"kind":"snippet","source":"return 1;"}""")]
    [InlineData("""{"kind":7,"source":"return 1;"}""")]
    [InlineData("""{"kind":"code"}""")]
    [InlineData("""{"kind":"code","source":7}""")]
    [InlineData("""{"kind":"code","source":"   "}""")]
    [InlineData("""{"kind":"code","source":"return 1;","mystery":true}""")]
    [InlineData("""{"kind":"code","source":"return 1;","effects":"idempotent_write"}""")]
    [InlineData("""{"kind":"code","source":"return 1;","effects":"unknown"}""")]
    [InlineData("[]")]
    public void ParseRejectsAmbiguousOrUnsupportedInput(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Throws<ArgumentException>(
            () => ScriptEs3Preflight.ParseRequest(document.RootElement));
    }

    [Fact]
    public void ParseEnforcesTheSameSourceCeilingAsScriptEval()
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(
                new
                {
                    kind = "code",
                    source = new string(
                        'a',
                        ScriptEs3Preflight.MaxSourceChars + 1)
                }));

        var error = Assert.Throws<ArgumentException>(
            () => ScriptEs3Preflight.ParseRequest(document.RootElement));

        Assert.Contains("character limit", error.Message, StringComparison.Ordinal);
    }

    private static OperationRequest Request()
    {
        using var document = JsonDocument.Parse(
            """{"kind":"code","source":"return 1;"}""");

        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "validate-test",
            Operation = ScriptEs3Preflight.Operation,
            Input = document.RootElement.Clone()
        };
    }

    private static JsonElement Payload(OperationResult result)
    {
        var value = result.Result?.Value;
        Assert.NotNull(value);
        return value!.Value;
    }
}
