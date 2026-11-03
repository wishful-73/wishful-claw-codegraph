// =============================================================================
// CodeGraph standalone process entry point.
//
// Speaks newline-delimited JSON-RPC over stdin/stdout. The host application
// spawns this executable, sends {"id","method","params"} lines, and reads back
// {"id","result"} / {"id","error"} / {"event","params"} lines. Nothing about the
// host's types, assemblies or worker protocol is referenced here — that is the
// whole point of the split.
//
// Environment:
//   CODEGRAPH_HOME                  overrides the graph-DB base directory
//   WISHFULCLAW_CODEGRAPH_GRAMMARS_DIR  tree-sitter native grammar directory
// =============================================================================

using System.Buffers;
using System.Text;
using System.Text.Json;

// Resolve the tree-sitter native grammars before anything touches the parser.
// Falls back to <binary>/grammars when the env var is absent.
CodeGraphNativeLibraryResolver.Install();

var server = new CodeGraphServer();
// Typed as the interface: InitializeAsync is a default implementation on
// IWorkerModule, so it is not visible through the concrete class.
IWorkerModule module = new CodeGraphModule();
module.Register(server);
await module.InitializeAsync().ConfigureAwait(false);

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var writeGate = new SemaphoreSlim(1, 1);
using var connectionCts = new CancellationTokenSource();
var context = new StdioRequestContext(stdout, writeGate, connectionCts.Token);

using var reader = new StreamReader(stdin, new UTF8Encoding(false));

while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonDocument? document = null;
    try
    {
        document = JsonDocument.Parse(line);
    }
    catch (JsonException)
    {
        // A malformed line has no id to echo back, so there is nothing to answer.
        continue;
    }

    using (document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        JsonElement? id = root.TryGetProperty("id", out var idElement) ? idElement : null;
        var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()!
            : null;

        if (string.IsNullOrEmpty(method))
        {
            await WriteErrorAsync(stdout, writeGate, id, -32600, "Invalid request: missing method.").ConfigureAwait(false);
            continue;
        }

        var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;

        if (!server.TryGet(method, out var handler))
        {
            await WriteErrorAsync(stdout, writeGate, id, -32601, $"Method not found: {method}").ConfigureAwait(false);
            continue;
        }

        try
        {
            var response = await handler(parameters, context).ConfigureAwait(false);
            await WriteResponseAsync(stdout, writeGate, id, response).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(stdout, writeGate, id, -32800, "Request cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(stdout, writeGate, id, -32603, ex.Message).ConfigureAwait(false);
        }
    }
}

// stdin closed => the host went away. Nothing left to serve.
connectionCts.Cancel();

static async Task WriteResponseAsync(Stream stdout, SemaphoreSlim gate, JsonElement? id, WorkerResponse response)
{
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer, JsonFormat.WriterOptions))
    {
        writer.WriteStartObject();
        WriteId(writer, id);
        writer.WritePropertyName("result");
        response.WriteBody(writer);
        writer.WriteEndObject();
    }

    await WriteLineAsync(stdout, gate, buffer.WrittenMemory).ConfigureAwait(false);
}

static async Task WriteErrorAsync(Stream stdout, SemaphoreSlim gate, JsonElement? id, int code, string message)
{
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer, JsonFormat.WriterOptions))
    {
        writer.WriteStartObject();
        WriteId(writer, id);
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    await WriteLineAsync(stdout, gate, buffer.WrittenMemory).ConfigureAwait(false);
}

static void WriteId(Utf8JsonWriter writer, JsonElement? id)
{
    writer.WritePropertyName("id");
    if (id.HasValue)
    {
        id.Value.WriteTo(writer);
    }
    else
    {
        writer.WriteNullValue();
    }
}

static async Task WriteLineAsync(Stream stdout, SemaphoreSlim gate, ReadOnlyMemory<byte> utf8Json)
{
    await gate.WaitAsync().ConfigureAwait(false);
    try
    {
        await stdout.WriteAsync(utf8Json).ConfigureAwait(false);
        await stdout.WriteAsync(JsonFormat.Newline).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
    }
    finally
    {
        gate.Release();
    }
}
