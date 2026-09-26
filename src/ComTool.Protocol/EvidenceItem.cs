using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EvidenceItem(
    string Kind,
    JsonElement Value);
