using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// Native Illustrator plug-in request/response bridge over the host-owned
/// SendScriptMessage COM method. The payload is data, never executable code.
/// </summary>
internal static class IllustratorPluginMessage
{
    public const string Operation = "plugin.message";
    internal const int MaxInputUtf8Bytes = 256 * 1024;
    internal const int MaxResponseUtf8Bytes = 512 * 1024;

    public static PluginMessageRequest ParseRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("plugin.message input must be a JSON object.");

        string? plugin = null;
        string? selector = null;
        string? message = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new ArgumentException(
                    $"Duplicate plugin.message field '{property.Name}'.");

            switch (property.Name)
            {
                case "plugin":
                    plugin = ReadBoundedString(
                        property.Value,
                        "plugin",
                        256,
                        allowEmpty: false);
                    break;
                case "selector":
                    selector = ReadBoundedString(
                        property.Value,
                        "selector",
                        512,
                        allowEmpty: false);
                    break;
                case "input":
                    message = ReadBoundedString(
                        property.Value,
                        "input",
                        MaxInputUtf8Bytes,
                        allowEmpty: true,
                        boundUtf8Bytes: true);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown plugin.message field '{property.Name}'.");
            }
        }

        if (plugin is null)
            throw new ArgumentException("'plugin' is required.");
        if (selector is null)
            throw new ArgumentException("'selector' is required.");
        if (message is null)
            throw new ArgumentException(
                "'input' is required (use an empty string when the plug-in expects no payload).");

        return new PluginMessageRequest(plugin, selector, message);
    }

    public static PluginMessageOutcome Execute(
        object appObject,
        PluginMessageRequest request,
        Func<object, string, object?[], object?>? mutationInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        var inputBytes = Encoding.UTF8.GetBytes(request.Input);
        var inputSha256 = Convert.ToHexStringLower(
            SHA256.HashData(inputBytes));

        var invoke =
            mutationInvoker ??
            ((object target, string method, object?[] args) =>
                IllustratorComInterop.InvokeMutationMethod(
                    target,
                    method,
                    args));
        var raw = invoke(
            appObject,
            "SendScriptMessage",
            [request.Plugin, request.Selector, request.Input]);

        var response = raw as string
            ?? Convert.ToString(raw, CultureInfo.InvariantCulture)
            ?? string.Empty;
        var responseBytes = Encoding.UTF8.GetBytes(response);
        var responseSha256 = Convert.ToHexStringLower(
            SHA256.HashData(responseBytes));

        if (responseBytes.Length > MaxResponseUtf8Bytes)
        {
            throw new PluginMessageResponseTooLargeException(
                responseBytes.Length,
                responseSha256,
                inputBytes.Length,
                inputSha256);
        }

        return new PluginMessageOutcome(
            response,
            inputBytes.Length,
            inputSha256,
            responseBytes.Length,
            responseSha256);
    }

    private static string ReadBoundedString(
        JsonElement value,
        string field,
        int max,
        bool allowEmpty,
        bool boundUtf8Bytes = false)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{field}' must be a string.");

        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            throw new ArgumentException($"'{field}' must be non-empty.");

        var size = boundUtf8Bytes
            ? Encoding.UTF8.GetByteCount(text)
            : text.Length;
        if (size > max)
        {
            var unit = boundUtf8Bytes ? "UTF-8 bytes" : "characters";
            throw new ArgumentException(
                $"'{field}' exceeds the {max} {unit} limit.");
        }

        return text;
    }
}

internal sealed record PluginMessageRequest(
    string Plugin,
    string Selector,
    string Input);

internal sealed record PluginMessageOutcome(
    string Response,
    int InputUtf8Bytes,
    string InputSha256,
    int ResponseUtf8Bytes,
    string ResponseSha256);

internal sealed class PluginMessageResponseTooLargeException(
    int responseUtf8Bytes,
    string responseSha256,
    int inputUtf8Bytes,
    string inputSha256)
    : Exception(
        $"The plug-in response was {responseUtf8Bytes} UTF-8 bytes; " +
        $"the safe inline limit is {IllustratorPluginMessage.MaxResponseUtf8Bytes}. " +
        "The plug-in call completed, but the oversized response is not forwarded " +
        "through the 1 MiB worker frame.")
{
    public int ResponseUtf8Bytes { get; } = responseUtf8Bytes;
    public string ResponseSha256 { get; } = responseSha256;
    public int InputUtf8Bytes { get; } = inputUtf8Bytes;
    public string InputSha256 { get; } = inputSha256;
}
