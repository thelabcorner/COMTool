namespace ComTool.Runtime.Examples;

/// <summary>
/// An explicit, bounded examples query. Every filter is caller-declared: there
/// is no hidden routing, no implicit default operation, and no unbounded result
/// set.
/// </summary>
public sealed record OperationExamplesQuery
{
    /// <summary>Default number of examples returned when no limit is given.</summary>
    public const int DefaultLimit = 8;

    /// <summary>Hard upper bound on returned examples.</summary>
    public const int MaxLimit = 32;

    /// <summary>Maximum accepted length for any single filter value.</summary>
    public const int MaxFilterLength = 128;

    /// <summary>Maximum accepted word count.</summary>
    public const int MaxWords = 8;

    /// <summary>Maximum accepted tag count.</summary>
    public const int MaxTags = 8;

    /// <summary>Exact registered operation name, or null.</summary>
    public string? Operation { get; init; }

    /// <summary>Lowercase tags that must all match, or empty.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Words that must all appear in the searchable text, or empty.</summary>
    public IReadOnlyList<string> Words { get; init; } = [];

    /// <summary>Maximum examples to return.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>Validates the query bounds.</summary>
    /// <exception cref="OperationExamplesException">A filter is unusable.</exception>
    public void Validate()
    {
        if (Operation is { } operation &&
            (string.IsNullOrWhiteSpace(operation) ||
             operation.Length > MaxFilterLength))
        {
            throw new OperationExamplesException(
                "invalid_examples_query_operation",
                $"An operation filter must be 1-{MaxFilterLength} characters.");
        }

        if (Limit is < 1 or > MaxLimit)
        {
            throw new OperationExamplesException(
                "invalid_examples_query_limit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        ValidateFilter(Tags, MaxTags, "tag");
        ValidateFilter(Words, MaxWords, "word");
    }

    private void ValidateFilter(
        IReadOnlyList<string> values,
        int maxCount,
        string label)
    {
        if (values.Count > maxCount)
        {
            throw new OperationExamplesException(
                "invalid_examples_query_filter",
                $"A query accepts at most {maxCount} {label} filters.");
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > MaxFilterLength)
            {
                throw new OperationExamplesException(
                    "invalid_examples_query_filter",
                    $"Each {label} filter must be 1-{MaxFilterLength} characters.");
            }
        }
    }
}
