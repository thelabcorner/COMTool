using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Runtime;

/// <summary>
/// Advisory ES3 pre-flight static analysis for ExtendScript source text,
/// surfaced as the read-only runtime operation <c>script.validate</c>.
///
/// SEMANTICS (deliberate, and not negotiable by callers):
///
/// <para>
/// This is ADVISORY STATIC ANALYSIS, never authoritative engine proof. A
/// clean result means "this ruleset found nothing in the constructs it
/// models", not "the ExtendScript engine will accept this source". A finding
/// means "this construct is outside the ES3 grammar this ruleset models",
/// not "the engine will reject this source". Only a real dispatch through
/// <c>script.eval</c> / <c>script.runFile</c> produces engine truth.
/// </para>
///
/// <para>
/// Findings never make the operation fail. Findings are reported with
/// <c>ok = true</c> and <c>clean = false</c> so a caller can distinguish
/// "analysis ran" from "analysis ran and had something to say".
/// </para>
///
/// <para>
/// This type performs no COM, no filesystem access, no network access, and
/// no host dispatch. It is a single linear pass over the source with bounded
/// output, so it is deterministic (same bytes in, same findings out, in the
/// same order) and needs no target, no lease, and no mutation ledger.
/// </para>
///
/// <para>
/// It is NOT a parser and NOT a debugger. It does not model scopes, types,
/// host objects, or runtime values, and it never reports what the engine
/// would do beyond the stable rule categories below.
/// </para>
///
/// <para>
/// V1's <c>is_expression_candidate</c> auto-return heuristic is deliberately
/// NOT reproduced. V2 callers declare <c>kind</c> explicitly
/// (<c>expression</c> or <c>code</c>); the analyzer only reports that a
/// declared kind and the source shape disagree. It never rewrites, wraps, or
/// re-routes caller source.
/// </para>
///
/// <para>
/// Findings never contain source text, and the analyzer never returns a
/// rewritten source, so no caller content can leak through a diagnostic.
/// </para>
/// </summary>
public static class ScriptEs3Preflight
{
    public const string Operation = "script.validate";
    public const string Analyzer = "es3-preflight";
    public const int AnalyzerVersion = 1;
    public const string Ruleset = "es3-preflight/1";

    /// <summary>
    /// Same character ceiling as <c>script.eval</c> so a snippet that can be
    /// dispatched can also be pre-flighted.
    /// </summary>
    public const int MaxSourceChars = 1_000_000;

    /// <summary>
    /// Hard cap on reported findings. Analysis continues past the cap (with no
    /// further allocation) so the truncation flag is truthful about the whole
    /// source rather than about a prefix.
    /// </summary>
    public const int MaxFindings = 200;

    public const string CategoryEs3Grammar = "es3_grammar";
    public const string CategoryUnterminated = "unterminated_construct";
    public const string CategoryDelimiterBalance = "delimiter_balance";
    public const string CategorySnippetShape = "snippet_shape";
    public const string CategoryPreprocessorPlacement = "preprocessor_placement";

    public const string Disclaimer =
        "Advisory static analysis only. Findings describe source constructs "
        + "outside the ES3 grammar this ruleset models; severity is reader "
        + "triage, not an engine verdict. A clean result is not proof that the "
        + "ExtendScript engine will accept the source, and a finding is not "
        + "proof that it will reject it. Engine truth requires script.eval or "
        + "script.runFile.";

    public static ScriptValidateRequest ParseRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "script.validate input must be a JSON object.");
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "kind",
            "source",
            "effects"
        };

        foreach (var property in input.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ArgumentException(
                    $"Unknown script.validate input field '{property.Name}'.");
            }
        }

        if (!input.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                "'kind' must be 'expression' or 'code'.");
        }

        var kind = kindElement.GetString();
        if (kind is not ("expression" or "code"))
        {
            throw new ArgumentException(
                "'kind' must be 'expression' or 'code'.");
        }

        if (!input.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("'source' must be a string.");
        }

        var source = sourceElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("'source' must be non-empty.");

        if (source.Length > MaxSourceChars)
        {
            throw new ArgumentException(
                $"'source' exceeds the {MaxSourceChars} character limit.");
        }

        if (input.TryGetProperty("effects", out var effectsElement) &&
            effectsElement.ValueKind != JsonValueKind.Null)
        {
            if (effectsElement.ValueKind != JsonValueKind.String ||
                effectsElement.GetString() != "read_only")
            {
                throw new ArgumentException(
                    "script.validate is unconditionally read-only; "
                    + "'effects' may only be 'read_only'.");
            }
        }

        return new ScriptValidateRequest(kind, source);
    }

    public static ScriptValidateAnalysis Analyze(
        ScriptValidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = request.Source;
        var expressionKind = string.Equals(
            request.Kind,
            "expression",
            StringComparison.Ordinal);

        var findings = new List<ScriptValidateFinding>(8);
        var truncated = false;

        void Add(
            string rule,
            string category,
            string severity,
            int atLine,
            int atColumn,
            int endLine,
            int endColumn,
            string message,
            string? fix)
        {
            if (findings.Count >= MaxFindings)
            {
                truncated = true;
                return;
            }

            findings.Add(
                new ScriptValidateFinding(
                    rule,
                    category,
                    severity,
                    atLine,
                    atColumn,
                    endLine,
                    endColumn,
                    message,
                    fix));
        }

        var balance = new Stack<(char Open, int Line, int Column)>();
        var balanceEnabled = true;
        var state = ScanState.Normal;
        var openLine = 1;
        var openColumn = 1;
        var line = 1;
        var column = 1;
        var index = 0;
        var length = source.Length;
        var atLineStart = true;
        var suppressLine = false;
        var firstTokenPending = true;
        var sawReturn = false;
        var codeBodyLooksLikeBareExpression = false;
        var expressionShapeReported = false;
        var forHeaderActive = false;
        var forHeaderDepth = 0;
        var forHeaderInit = false;

        while (index < length)
        {
            if ((index & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var current = source[index];

            if (current == '\n')
            {
                // A raw newline always ends a line. It also ends a
                // single/double-quoted string: that matches the legacy
                // checker and the ES3 rule that quoted strings cannot span
                // lines. Block comments and template literals continue.
                if (state is ScanState.SingleQuote or
                    ScanState.DoubleQuote or
                    ScanState.LineComment)
                {
                    state = ScanState.Normal;
                }

                line++;
                column = 1;
                atLineStart = true;
                suppressLine = false;
                index++;
                continue;
            }

            switch (state)
            {
                case ScanState.LineComment:
                    index++;
                    column++;
                    continue;

                case ScanState.BlockComment:
                    if (current == '*' &&
                        index + 1 < length &&
                        source[index + 1] == '/')
                    {
                        state = ScanState.Normal;
                        index += 2;
                        column += 2;
                        continue;
                    }

                    index++;
                    column++;
                    continue;

                case ScanState.Template:
                    if (current == '`')
                    {
                        state = ScanState.Normal;
                        index++;
                        column++;
                        continue;
                    }

                    if (current == '\\' && index + 1 < length)
                    {
                        ConsumeEscape(
                            source,
                            ref index,
                            ref line,
                            ref column);
                        continue;
                    }

                    index++;
                    column++;
                    continue;

                case ScanState.SingleQuote:
                case ScanState.DoubleQuote:
                {
                    var quote = state == ScanState.SingleQuote ? '\'' : '"';
                    if (current == '\\' && index + 1 < length)
                    {
                        ConsumeEscape(
                            source,
                            ref index,
                            ref line,
                            ref column);
                        continue;
                    }

                    if (current == quote)
                        state = ScanState.Normal;

                    index++;
                    column++;
                    continue;
                }
            }

            // Normal state.
            if (IsSpace(current))
            {
                index++;
                column++;
                continue;
            }

            if (current == '#' && atLineStart)
            {
                var probe = index + 1;
                while (probe < length && IsSpace(source[probe]))
                    probe++;

                if (probe < length && IsAsciiLetter(source[probe]))
                {
                    if (!suppressLine)
                    {
                        Add(
                            RulePreprocessorDirective,
                            CategoryPreprocessorPlacement,
                            "warning",
                            line,
                            column,
                            line,
                            column,
                            "preprocessor directive in an inline snippet; "
                            + "directives belong in a .jsx file",
                            "move the directive into a .jsx file executed "
                            + "through script.runFile");
                    }

                    suppressLine = true;
                }

                atLineStart = false;
                firstTokenPending = false;
                index++;
                column++;
                continue;
            }

            atLineStart = false;

            switch (current)
            {
                case '/'
                    when index + 1 < length && source[index + 1] == '/':
                    state = ScanState.LineComment;
                    index += 2;
                    column += 2;
                    continue;

                case '/'
                    when index + 1 < length && source[index + 1] == '*':
                    openLine = line;
                    openColumn = column;
                    state = ScanState.BlockComment;
                    index += 2;
                    column += 2;
                    continue;

                case '\'':
                case '"':
                    openLine = line;
                    openColumn = column;
                    state = current == '\''
                        ? ScanState.SingleQuote
                        : ScanState.DoubleQuote;
                    firstTokenPending = false;
                    index++;
                    column++;
                    continue;

                case '`':
                    Add(
                        RuleTemplateLiteral,
                        CategoryEs3Grammar,
                        "error",
                        line,
                        column,
                        line,
                        column,
                        "ES3 violation: template literal ("
                        + "use string concatenation ('a' + b))",
                        "use string concatenation ('a' + b)");
                    openLine = line;
                    openColumn = column;
                    state = ScanState.Template;
                    firstTokenPending = false;
                    index++;
                    column++;
                    continue;

                case '='
                    when index + 1 < length && source[index + 1] == '>':
                    Add(
                        RuleArrowFunction,
                        CategoryEs3Grammar,
                        "error",
                        line,
                        column,
                        line,
                        column + 1,
                        "ES3 violation: arrow function (use `function`)",
                        "use a `function` expression or declaration");
                    firstTokenPending = false;
                    index += 2;
                    column += 2;
                    continue;

                case '.'
                    when index + 2 < length &&
                         source[index + 1] == '.' &&
                         source[index + 2] == '.':
                    Add(
                        RuleSpreadRest,
                        CategoryEs3Grammar,
                        "error",
                        line,
                        column,
                        line,
                        column + 2,
                        "ES3 violation: spread/rest ("
                        + "use explicit args or `arguments`)",
                        "pass explicit arguments or use `arguments`");
                    firstTokenPending = false;
                    index += 3;
                    column += 3;
                    continue;

                case '?':
                {
                    if (index + 1 < length && source[index + 1] == '?')
                    {
                        Add(
                            RuleNullishCoalescing,
                            CategoryEs3Grammar,
                            "error",
                            line,
                            column,
                            line,
                            column + 1,
                            "ES3 violation: nullish coalescing (??) "
                            + "(use `||` or explicit checks)",
                            "use `||` or an explicit check");
                        firstTokenPending = false;
                        index += 2;
                        column += 2;
                        continue;
                    }

                    if (index + 2 < length &&
                        source[index + 1] == '.' &&
                        (IsIdentifierStart(source[index + 2]) ||
                         source[index + 2] == '(' ||
                         source[index + 2] == '['))
                    {
                        Add(
                            RuleOptionalChaining,
                            CategoryEs3Grammar,
                            "error",
                            line,
                            column,
                            line,
                            column + 1,
                            "ES3 violation: optional chaining (?.) "
                            + "(guard with explicit checks)",
                            "guard the value with an explicit check");
                        firstTokenPending = false;
                        index += 2;
                        column += 2;
                        continue;
                    }

                    break;
                }
            }

            if (IsIdentifierStart(current))
            {
                var identifierEnd = index + 1;
                while (identifierEnd < length &&
                       IsIdentifierPart(source[identifierEnd]))
                {
                    identifierEnd++;
                }

                var tokenLength = identifierEnd - index;
                var memberAccess = index > 0 && source[index - 1] == '.';

                if (firstTokenPending)
                {
                    firstTokenPending = false;
                    var statementKeyword =
                        tokenLength <= 8 &&
                        IsStatementKeyword(source, index, tokenLength);

                    if (expressionKind &&
                        !suppressLine &&
                        statementKeyword)
                    {
                        Add(
                            RuleExpressionKindNotSingleExpression,
                            CategorySnippetShape,
                            "error",
                            line,
                            column,
                            line,
                            column + tokenLength - 1,
                            "kind 'expression' requires one expression; "
                            + "found the statement keyword '"
                            + source.Substring(index, tokenLength) + "'",
                            "use kind 'code' for a statement body");
                        expressionShapeReported = true;
                    }
                    else if (!expressionKind && !statementKeyword)
                    {
                        // The no-return advisory is intentionally narrow: it
                        // describes a bare expression statement that will not
                        // transport a value. Valid statement bodies (for/try/
                        // var/etc.) are not made "unclean" merely because they
                        // have no explicit return.
                        codeBodyLooksLikeBareExpression = true;
                    }
                }

                // Every keyword rule is at most six characters, so ordinary
                // tokens are compared in place and never allocate. Member
                // access (`obj.let`) is not a declaration and is skipped,
                // which removes a class of legacy false positives.
                if (!memberAccess && tokenLength <= 6)
                {
                    if (Matches(source, index, "let") ||
                        Matches(source, index, "const"))
                    {
                        if (PeekDeclarationTarget(source, identifierEnd))
                        {
                            Add(
                                RuleBlockScopedDeclaration,
                                CategoryEs3Grammar,
                                "error",
                                line,
                                column,
                                line,
                                column + tokenLength - 1,
                                Matches(source, index, "let")
                                    ? "ES3 violation: let (use `var`)"
                                    : "ES3 violation: const (use `var`)",
                                "use `var`");
                        }
                    }
                    else if (Matches(source, index, "class"))
                    {
                        if (PeekDeclarationTarget(source, identifierEnd))
                        {
                            Add(
                                RuleClassDeclaration,
                                CategoryEs3Grammar,
                                "error",
                                line,
                                column,
                                line,
                                column + tokenLength - 1,
                                "ES3 violation: class "
                                + "(use constructor functions)",
                                "use a constructor function");
                        }
                    }
                    else if (Matches(source, index, "async"))
                    {
                        if (PeekDeclarationTarget(source, identifierEnd))
                        {
                            Add(
                                RuleAsyncFunction,
                                CategoryEs3Grammar,
                                "error",
                                line,
                                column,
                                line,
                                column + tokenLength - 1,
                                "ES3 violation: async function "
                                + "(use plain functions + callbacks)",
                                "use plain functions and callbacks");
                        }
                    }
                    else if (Matches(source, index, "await"))
                    {
                        Add(
                            RuleAwait,
                            CategoryEs3Grammar,
                            "error",
                            line,
                            column,
                            line,
                            column + tokenLength - 1,
                            "ES3 violation: await "
                            + "(use callbacks (ExtendScript has no promises))",
                            "use callbacks (ExtendScript has no promises)");
                    }
                    else if (Matches(source, index, "yield"))
                    {
                        Add(
                            RuleYield,
                            CategoryEs3Grammar,
                            "error",
                            line,
                            column,
                            line,
                            column + tokenLength - 1,
                            "ES3 violation: yield "
                            + "(use plain functions + callbacks)",
                            "use plain functions and callbacks");
                    }
                    else if (Matches(source, index, "for"))
                    {
                        if (PeekForHeader(source, identifierEnd))
                        {
                            // The header's opening paren is still ahead in the
                            // scan and will raise the depth to 1.
                            forHeaderActive = true;
                            forHeaderDepth = 0;
                            forHeaderInit = true;
                        }
                    }
                    else if (Matches(source, index, "of"))
                    {
                        if (forHeaderActive &&
                            forHeaderDepth == 1 &&
                            forHeaderInit)
                        {
                            Add(
                                RuleForOf,
                                CategoryEs3Grammar,
                                "error",
                                line,
                                column,
                                line,
                                column + 1,
                                "ES3 violation: for...of "
                                + "(use an index loop)",
                                "use an index loop");
                        }
                    }
                    else if (Matches(source, index, "return"))
                    {
                        sawReturn = true;
                    }
                }

                column += tokenLength;
                index = identifierEnd;
                continue;
            }

            switch (current)
            {
                case '(':
                case '[':
                case '{':
                    if (expressionKind &&
                        !expressionShapeReported &&
                        !suppressLine &&
                        current == '{')
                    {
                        Add(
                            RuleExpressionKindNotSingleExpression,
                            CategorySnippetShape,
                            "error",
                            line,
                            column,
                            line,
                            column,
                            "kind 'expression' requires one expression; "
                            + "found a block delimiter '{'",
                            "use kind 'code' for a statement body");
                        expressionShapeReported = true;
                    }

                    if (forHeaderActive && current == '(')
                        forHeaderDepth++;

                    balance.Push((current, line, column));
                    firstTokenPending = false;
                    index++;
                    column++;
                    continue;

                case ')':
                case ']':
                case '}':
                    if (expressionKind &&
                        !expressionShapeReported &&
                        !suppressLine &&
                        current == '}')
                    {
                        Add(
                            RuleExpressionKindNotSingleExpression,
                            CategorySnippetShape,
                            "error",
                            line,
                            column,
                            line,
                            column,
                            "kind 'expression' requires one expression; "
                            + "found a block delimiter '}'",
                            "use kind 'code' for a statement body");
                        expressionShapeReported = true;
                    }

                    if (forHeaderActive)
                    {
                        if (current == ')')
                        {
                            forHeaderDepth--;
                            if (forHeaderDepth <= 0)
                                forHeaderActive = false;
                        }
                    }

                    if (balanceEnabled)
                    {
                        var expected = current switch
                        {
                            ')' => '(',
                            ']' => '[',
                            _ => '{'
                        };

                        if (balance.Count == 0 ||
                            balance.Peek().Open != expected)
                        {
                            Add(
                                RuleDelimiterBalance,
                                CategoryDelimiterBalance,
                                "error",
                                line,
                                column,
                                line,
                                column,
                                balance.Count == 0
                                    ? $"unbalanced '{current}' (extra close)"
                                    : $"unbalanced '{current}' (missing '{expected}' or extra close)",
                                "match the delimiter");
                            balanceEnabled = false;
                        }
                        else
                        {
                            balance.Pop();
                        }
                    }

                    firstTokenPending = false;
                    index++;
                    column++;
                    continue;

                case ';':
                    if (forHeaderActive &&
                        forHeaderDepth == 1 &&
                        forHeaderInit)
                    {
                        forHeaderInit = false;
                    }

                    if (expressionKind &&
                        !expressionShapeReported &&
                        !suppressLine)
                    {
                        Add(
                            RuleExpressionKindNotSingleExpression,
                            CategorySnippetShape,
                            "error",
                            line,
                            column,
                            line,
                            column,
                            "kind 'expression' requires one expression; "
                            + "found a statement separator ';'",
                            "use kind 'code' for a statement body");
                        expressionShapeReported = true;
                    }

                    firstTokenPending = false;
                    index++;
                    column++;
                    continue;
            }

            firstTokenPending = false;
            index++;
            column++;
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (state)
        {
            case ScanState.SingleQuote:
            case ScanState.DoubleQuote:
                Add(
                    RuleUnterminated,
                    CategoryUnterminated,
                    "error",
                    openLine,
                    openColumn,
                    openLine,
                    openColumn,
                    "unterminated string (truncated source?)",
                    "close the string or remove the truncated tail");
                break;

            case ScanState.BlockComment:
                Add(
                    RuleUnterminated,
                    CategoryUnterminated,
                    "error",
                    openLine,
                    openColumn,
                    openLine,
                    openColumn,
                    "unterminated block comment (truncated source?)",
                    "close the block comment or remove the truncated tail");
                break;

            case ScanState.Template:
                Add(
                    RuleUnterminated,
                    CategoryUnterminated,
                    "error",
                    openLine,
                    openColumn,
                    openLine,
                    openColumn,
                    "unterminated template literal (truncated source?)",
                    "close the template literal or remove the truncated tail");
                break;
        }

        if (balanceEnabled && balance.Count > 0)
        {
            var unclosed = balance.Peek();
            Add(
                RuleDelimiterBalance,
                CategoryDelimiterBalance,
                "error",
                unclosed.Line,
                unclosed.Column,
                unclosed.Line,
                unclosed.Column,
                $"unbalanced '{unclosed.Open}' (never closed)",
                "close it or remove it");
        }

        if (!expressionKind &&
            !sawReturn &&
            codeBodyLooksLikeBareExpression)
        {
            Add(
                RuleCodeBodyWithoutReturn,
                CategorySnippetShape,
                "info",
                1,
                1,
                1,
                1,
                "kind 'code' runs the source as a statement body; a bare "
                + "expression statement does not transport a result",
                "add an explicit `return` if you expect a value");
        }

        // Canonical order: position first, then stable rule id, then message.
        // Independent of which scan phase produced the finding.
        findings.Sort(static (left, right) =>
        {
            var byLine = left.Line.CompareTo(right.Line);
            if (byLine != 0)
                return byLine;

            var byColumn = left.Column.CompareTo(right.Column);
            if (byColumn != 0)
                return byColumn;

            var byRule = string.CompareOrdinal(left.Rule, right.Rule);
            return byRule != 0
                ? byRule
                : string.CompareOrdinal(left.Message, right.Message);
        });

        return new ScriptValidateAnalysis(
            request.Kind,
            source.Length,
            line,
            findings.Count == 0,
            truncated,
            findings);
    }

    /// <summary>
    /// Builds the operation result for a completed analysis. Findings are a
    /// successful outcome with <c>clean = false</c>, never an error, so a
    /// caller can always read the findings.
    /// </summary>
    public static OperationResult BuildResult(
        OperationRequest request,
        ScriptValidateAnalysis analysis,
        double totalMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(analysis);

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                advisory = true,
                authoritative = false,
                engineProof = false,
                analyzer = Analyzer,
                analyzerVersion = AnalyzerVersion,
                ruleset = Ruleset,
                disclaimer = Disclaimer,
                kind = analysis.Kind,
                sourceLength = analysis.SourceLength,
                sourceLineCount = analysis.SourceLineCount,
                clean = analysis.Clean,
                findingCount = analysis.Findings.Count,
                findingsTruncated = analysis.FindingsTruncated,
                findings = analysis.Findings
                    .Select(
                        static finding => new
                        {
                            rule = finding.Rule,
                            category = finding.Category,
                            severity = finding.Severity,
                            line = finding.Line,
                            column = finding.Column,
                            endLine = finding.EndLine,
                            endColumn = finding.EndColumn,
                            message = finding.Message,
                            fix = finding.Fix
                        })
                    .ToArray()
            },
            RuntimePayloadJson);

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = ProtocolValue.From(payload),
            Timing = new OperationTiming(TotalMs: totalMs)
        };
    }

    public const string RuleBlockScopedDeclaration = "es3_block_scoped_declaration";
    public const string RuleArrowFunction = "es3_arrow_function";
    public const string RuleTemplateLiteral = "es3_template_literal";
    public const string RuleClassDeclaration = "es3_class_declaration";
    public const string RuleSpreadRest = "es3_spread_rest";
    public const string RuleAsyncFunction = "es3_async_function";
    public const string RuleAwait = "es3_await";
    public const string RuleYield = "es3_yield";
    public const string RuleOptionalChaining = "es3_optional_chaining";
    public const string RuleNullishCoalescing = "es3_nullish_coalescing";
    public const string RuleForOf = "es3_for_of";
    public const string RuleUnterminated = "unterminated_construct";
    public const string RuleDelimiterBalance = "delimiter_balance";
    public const string RulePreprocessorDirective =
        "snippet_preprocessor_directive";
    public const string RuleExpressionKindNotSingleExpression =
        "expression_kind_not_single_expression";
    public const string RuleCodeBodyWithoutReturn = "code_body_without_return";

    private static readonly JsonSerializerOptions RuntimePayloadJson =
        new(JsonSerializerDefaults.Web);

    private enum ScanState
    {
        Normal,
        SingleQuote,
        DoubleQuote,
        LineComment,
        BlockComment,
        Template
    }

    /// <summary>
    /// Consumes a backslash escape at <paramref name="index"/> (known to be a
    /// backslash with at least one following character) and advances the
    /// line/column cursors over the whole sequence. A backslash before a
    /// newline is an ES3 line continuation, so it moves the line cursor.
    /// </summary>
    private static void ConsumeEscape(
        string source,
        ref int index,
        ref int line,
        ref int column)
    {
        var escaped = source[index + 1];
        index += 2;

        if (escaped == '\n')
        {
            line++;
            column = 1;
        }
        else
        {
            column += 2;
        }
    }

    private static bool PeekDeclarationTarget(string source, int from)
    {
        var length = source.Length;
        var probe = from;
        while (probe < length &&
               (IsSpace(source[probe]) || source[probe] == '\n'))
        {
            probe++;
        }

        return probe < length && IsIdentifierStart(source[probe]);
    }

    private static bool PeekForHeader(string source, int from)
    {
        var length = source.Length;
        var probe = from;
        while (probe < length &&
               (IsSpace(source[probe]) || source[probe] == '\n'))
        {
            probe++;
        }

        return probe < length && source[probe] == '(';
    }

    private static bool IsStatementKeyword(
        string source,
        int start,
        int length) =>
        length switch
        {
            2 => Matches(source, start, "if"),
            3 => Matches(source, start, "var")
                || Matches(source, start, "for")
                || Matches(source, start, "let")
                || Matches(source, start, "try"),
            5 => Matches(source, start, "while")
                || Matches(source, start, "throw")
                || Matches(source, start, "break")
                || Matches(source, start, "catch")
                || Matches(source, start, "const")
                || Matches(source, start, "class")
                || Matches(source, start, "async")
                || Matches(source, start, "await")
                || Matches(source, start, "yield"),
            6 => Matches(source, start, "return")
                || Matches(source, start, "switch"),
            7 => Matches(source, start, "finally"),
            8 => Matches(source, start, "function")
                || Matches(source, start, "continue")
                || Matches(source, start, "debugger"),
            _ => length == 4 && Matches(source, start, "with")
        };

    private static bool Matches(
        string source,
        int start,
        string expected) =>
        string.CompareOrdinal(
            source,
            start,
            expected,
            0,
            expected.Length) == 0;

    private static bool IsSpace(char value) =>
        value is ' ' or '\t' or '\r' or '\f' or '\v';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsIdentifierStart(char value) =>
        IsAsciiLetter(value) || value is '_' or '$';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || value is >= '0' and <= '9';
}

public sealed record ScriptValidateRequest(string Kind, string Source);

/// <param name="SourceLength">
/// Submitted source length in characters, not bytes.
/// </param>
/// <param name="SourceLineCount">1-based line count of the submitted source.</param>
public sealed record ScriptValidateAnalysis(
    string Kind,
    int SourceLength,
    int SourceLineCount,
    bool Clean,
    bool FindingsTruncated,
    IReadOnlyList<ScriptValidateFinding> Findings);

/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column, inclusive span start.</param>
/// <param name="EndLine">1-based inclusive span end line.</param>
/// <param name="EndColumn">1-based inclusive span end column.</param>
public sealed record ScriptValidateFinding(
    string Rule,
    string Category,
    string Severity,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Message,
    string? Fix);
