namespace ComTool.Hosts.Abstractions;

/// <summary>
/// Canonical path grammar for the generic COM surfaces. Caller array indices
/// are zero-based; the Illustrator adapter translates them to COM collection
/// Item(index + 1) during traversal. Mutations require a plain terminal member;
/// reads may select an indexed terminal collection item.
/// </summary>
public static class ComAutomationPath
{
    public const int MaxPathLength = 512;
    public const int MaxPathSegments = 32;

    public static ComAutomationPathSegment[] ParseMutation(string path) =>
        Parse(path, allowTerminalIndex: false);

    public static ComAutomationPathSegment[] ParseRead(string path) =>
        Parse(path, allowTerminalIndex: true);

    private static ComAutomationPathSegment[] Parse(
        string path,
        bool allowTerminalIndex)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ComAutomationPathException(
                "invalid_com_path",
                "COM path must be non-empty.");
        }

        if (path.Length > MaxPathLength)
        {
            throw new ComAutomationPathException(
                "com_path_too_long",
                $"COM path exceeds the runtime bound of {MaxPathLength} characters.");
        }

        var raw = path.Split('.', StringSplitOptions.None);
        if (raw.Length > MaxPathSegments)
        {
            throw new ComAutomationPathException(
                "com_path_too_deep",
                $"COM path exceeds the runtime bound of {MaxPathSegments} segments.");
        }

        var parsed = new ComAutomationPathSegment[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            parsed[i] = ParseSegment(raw[i]);

        if (!allowTerminalIndex &&
            parsed[^1].Index is not null)
        {
            throw new ComAutomationPathException(
                "invalid_com_path",
                "The final COM path segment must be a plain member name without an index.");
        }

        return parsed;
    }

    private static ComAutomationPathSegment ParseSegment(string raw)
    {
        if (raw.Length == 0)
        {
            throw new ComAutomationPathException(
                "invalid_com_path",
                "COM path cannot contain empty segments.");
        }

        var name = raw;
        int? index = null;
        var bracket = raw.IndexOf('[');

        if (bracket >= 0)
        {
            if (!raw.EndsWith(']') ||
                raw.IndexOf('[', bracket + 1) >= 0)
            {
                throw new ComAutomationPathException(
                    "invalid_com_path",
                    $"Invalid COM path segment '{raw}'.");
            }

            name = raw[..bracket];
            if (!int.TryParse(
                    raw[(bracket + 1)..^1],
                    out var parsedIndex) ||
                parsedIndex < 0 ||
                parsedIndex == int.MaxValue)
            {
                throw new ComAutomationPathException(
                    "invalid_com_path",
                    $"Invalid COM index in segment '{raw}'.");
            }

            index = parsedIndex;
        }

        if (!IsIdentifier(name))
        {
            throw new ComAutomationPathException(
                "invalid_com_path",
                $"Invalid COM path segment '{raw}'.");
        }

        return new ComAutomationPathSegment(name, index);
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.Skip(1).All(
            static ch => char.IsLetterOrDigit(ch) || ch == '_');
}

public sealed record ComAutomationPathSegment(
    string Name,
    int? Index);

public sealed class ComAutomationPathException : ArgumentException
{
    public ComAutomationPathException(
        string kind,
        string message)
        : base(message)
    {
        Kind = kind;
    }

    public string Kind { get; }
}
