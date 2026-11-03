// =============================================================================
// CodeGraphServer — the standalone host.
//
// Mounts CodeGraphModule's RPC methods onto a name -> handler table and speaks
// newline-delimited JSON-RPC over stdio. This is what replaces the application's
// module catalog: the host process knows nothing about CodeGraph, it just spawns
// this executable and calls methods by name.
//
// Wire shape is kept identical to the application's worker protocol on purpose
// (see HostContracts.cs) so the method bodies did not need rewriting:
//
//   request   {"id": 1, "method": "codegraph/explore", "params": {...}}
//   response  {"id": 1, "result": ...}
//   error     {"id": 1, "error": {"code": -32601, "message": "..."}}
//   event     {"event": "codegraph/index-progress", "params": {...}}
//
// Events are pushed unsolicited while a long method (index / sync) runs; they
// are emitted with no id, which is how the reader tells them from responses.
// =============================================================================

using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

/// <summary>Method table: name -> handler. All four Register shapes normalise here.</summary>
internal sealed class CodeGraphServer : IWorkerModuleContext
{
    private readonly Dictionary<string, Func<JsonElement, IWorkerRequestContext, Task<WorkerResponse>>> _handlers =
        new(StringComparer.Ordinal);

    public void Register(string method, Func<JsonElement, Task<WorkerResponse>> handler)
    {
        _handlers[method] = (parameters, _) => handler(parameters);
    }

    public void Register(string method, Func<JsonElement, WorkerResponse> handler)
    {
        _handlers[method] = (parameters, _) => Task.FromResult(handler(parameters));
    }

    public void Register(string method, Func<JsonElement, IWorkerRequestContext, Task<WorkerResponse>> handler)
    {
        _handlers[method] = handler;
    }

    public void Register(string method, Func<JsonElement, IWorkerRequestContext, WorkerResponse> handler)
    {
        _handlers[method] = (parameters, context) => Task.FromResult(handler(parameters, context));
    }

    public string[] GetRegisteredMethods() => _handlers.Keys.ToArray();

    public bool TryGet(string method, out Func<JsonElement, IWorkerRequestContext, Task<WorkerResponse>> handler)
        => _handlers.TryGetValue(method, out handler!);
}

/// <summary>
/// Per-request context. Cancellation comes from the host going away (stdin EOF);
/// events go straight out on stdout because that is the only channel we have.
/// </summary>
internal sealed class StdioRequestContext : IWorkerRequestContext
{
    private readonly Stream _stdout;
    private readonly SemaphoreSlim _writeGate;

    public StdioRequestContext(Stream stdout, SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        _stdout = stdout;
        _writeGate = writeGate;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    public CancellationToken ConnectionCancellationToken => CancellationToken.None;

    public IWorkerRequestContext ForBackgroundOperation() => this;

    public ValueTask EmitEventAsync<T>(string eventName, T parameters, JsonTypeInfo<T> typeInfo)
        => EmitAsync(eventName, parameters, typeInfo);

    public ValueTask EmitEventIgnoringCancellationAsync<T>(string eventName, T parameters, JsonTypeInfo<T> typeInfo)
        => EmitAsync(eventName, parameters, typeInfo);

    public ValueTask EmitMessagePackEventAsync(string eventName, ReadOnlyMemory<byte> payload)
    {
        // stdio JSON-RPC has no MessagePack framing. No call site uses this today;
        // failing loudly beats writing a binary blob into a JSON stream.
        throw new NotSupportedException("MessagePack events are not available over stdio JSON-RPC.");
    }

    private async ValueTask EmitAsync<T>(string eventName, T parameters, JsonTypeInfo<T> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, JsonFormat.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("event", eventName);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, typeInfo);
            writer.WriteEndObject();
        }

        await WriteLineAsync(buffer.WrittenMemory).ConfigureAwait(false);
    }

    internal async Task WriteLineAsync(ReadOnlyMemory<byte> utf8Json)
    {
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _stdout.WriteAsync(utf8Json, CancellationToken.None).ConfigureAwait(false);
            await _stdout.WriteAsync(JsonFormat.Newline, CancellationToken.None).ConfigureAwait(false);
            await _stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

/// <summary>Shared JSON writing options — one place, so responses and events never diverge.</summary>
internal static class JsonFormat
{
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static ReadOnlyMemory<byte> Newline { get; } = Encoding.UTF8.GetBytes("\n");
}
