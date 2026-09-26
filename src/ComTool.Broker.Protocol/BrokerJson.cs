using System.Text.Json;

namespace ComTool.Broker.Protocol;

public static class BrokerJson
{
    public static byte[] Serialize(WorkerHello value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            BrokerJsonContext.Default.WorkerHello);

    public static byte[] Serialize(WorkerHelloAck value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            BrokerJsonContext.Default.WorkerHelloAck);

    public static byte[] Serialize(BrokerCommand value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            BrokerJsonContext.Default.BrokerCommand);

    public static byte[] Serialize(BrokerResponse value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            BrokerJsonContext.Default.BrokerResponse);

    public static WorkerHello DeserializeHello(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(
            json,
            BrokerJsonContext.Default.WorkerHello)
        ?? throw new JsonException("Worker hello decoded to null.");

    public static WorkerHelloAck DeserializeHelloAck(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(
            json,
            BrokerJsonContext.Default.WorkerHelloAck)
        ?? throw new JsonException("Worker hello ack decoded to null.");

    public static BrokerCommand DeserializeCommand(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(
            json,
            BrokerJsonContext.Default.BrokerCommand)
        ?? throw new JsonException("Broker command decoded to null.");

    public static BrokerResponse DeserializeResponse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(
            json,
            BrokerJsonContext.Default.BrokerResponse)
        ?? throw new JsonException("Broker response decoded to null.");
}
