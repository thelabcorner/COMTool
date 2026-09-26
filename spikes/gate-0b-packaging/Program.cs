using System.Runtime.InteropServices;
using System.Text.Json;

var payload = new
{
    gate = "0B",
    ok = true,
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
    rid = RuntimeInformation.RuntimeIdentifier,
    processId = Environment.ProcessId
};

Console.WriteLine(JsonSerializer.Serialize(payload));
