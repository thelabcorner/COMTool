using System.Text.Json;
using ComTool.Runtime;

namespace ComTool.Migration.Parity;

/// <summary>
/// Executable V1 to V2 parity report.
///
/// Reads the legacy feature matrix, the lane-owned parity manifest, the
/// semantic comparison fixtures, the checked-in registry snapshot, and the
/// live runtime operation catalog, then prints a machine-readable parity
/// report. Exits 0 when there are no error findings, 1 when there are, and
/// 2 on a harness failure such as an unreadable input.
/// </summary>
internal static class Program
{
    private const int ExitClean = 0;
    private const int ExitFindings = 1;
    private const int ExitHarnessError = 2;

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            FileNotFoundException or
            DirectoryNotFoundException or
            JsonException or
            KeyNotFoundException)
        {
            Console.Error.WriteLine(
                $"parity harness error: {exception.Message}");
            return ExitHarnessError;
        }
    }

    private static int Run(string[] args)
    {
        string? root = null;
        string format = "markdown";
        string? output = null;
        var failOnAdvisory = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    root = Next(args, ref index);
                    break;
                case "--format":
                    format = Next(args, ref index);
                    break;
                case "--out":
                    output = Next(args, ref index);
                    break;
                case "--fail-on-advisory":
                    failOnAdvisory = true;
                    break;
                case "--help" or "-h":
                    Usage();
                    return ExitClean;
                default:
                    throw new InvalidDataException(
                        $"Unknown argument '{args[index]}'.");
            }
        }

        if (format is not ("json" or "markdown"))
            throw new InvalidDataException(
                $"--format must be 'json' or 'markdown', not '{format}'.");

        var resolvedRoot = root is null
            ? V2RootLocator.Locate()
            : Path.GetFullPath(root);

        if (!File.Exists(Path.Combine(resolvedRoot, "ComTool.V2.slnx")))
            throw new DirectoryNotFoundException(
                $"'{resolvedRoot}' is not the COM Tool V2 root.");

        var catalogOperations = BuiltInOperations.Catalog
            .Definitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

        var analysis = ParityAnalyzer.Run(
            resolvedRoot,
            catalogOperations,
            "ComTool.Runtime.BuiltInOperations.Catalog");

        var report = format == "json"
            ? ParityAnalyzer.ToJson(analysis)
            : ParityAnalyzer.ToMarkdown(analysis);

        if (output is null)
        {
            Console.WriteLine(report);
        }
        else
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(Path.GetFullPath(output), report);
            Console.WriteLine(
                $"parity report written to {Path.GetFullPath(output)}");
        }

        var byState = analysis.StateCounts.Count == 0
            ? "(none)"
            : string.Join(
                ", ",
                analysis.StateCounts.Select(pair =>
                    $"{pair.Key}={pair.Value}"));

        Console.Error.WriteLine(
            $"parity status={(analysis.IsClean ? "clean" : "findings")} " +
            $"errors={analysis.Errors.Count} " +
            $"advisories={analysis.Advisories.Count} " +
            $"cases={analysis.Fixtures.Cases.Count} states[{byState}]");

        if (!analysis.IsClean)
            return ExitFindings;

        return failOnAdvisory && analysis.Advisories.Count > 0
            ? ExitFindings
            : ExitClean;
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new InvalidDataException(
                $"Argument '{args[index]}' requires a value.");

        index++;
        return args[index];
    }

    private static void Usage() =>
        Console.WriteLine(
            """
            ComTool.Migration.Parity — V1 to V2 migration parity report

              --root <dir>            COM Tool V2 root (auto-detected)
              --format json|markdown  Report format (default markdown)
              --out <file>            Write the report to a file
              --fail-on-advisory      Exit 1 when advisories are present
              --help                  Show this help

            Exit codes: 0 clean, 1 findings, 2 harness error.
            """);
}
