// =============================================================================
// MCP protocol layer.
//
// This process exists to be mounted by the host as an ordinary extension MCP
// server (extension.json -> mcpServers -> stdio). The host's MCP client sends
// `initialize`, then `tools/list`, then `tools/call`; without this layer every
// one of those lands on the `codegraph/*` method table, misses, and answers
// -32601 — the handshake dies before a single tool is listed.
//
// The mapping is deliberately mechanical:
//
//   tools/list  ->  codegraph/tools-list handler  ->  CodeGraphToolDefs.ListFor
//   tools/call  ->  strip the `codegraph_` prefix ->  `codegraph/<short>` method
//
// Tool-surface narrowing (default = explore only, CODEGRAPH_MCP_TOOLS allowlist,
// tiny-repo gating, project-aware schemas) already lives in CodeGraphToolDefs
// and is NOT re-implemented here. This file only translates envelopes.
//
// The remaining `codegraph/*` methods stay callable as a raw JSON-RPC surface —
// useful for debugging, harmless for MCP clients, which never look for them.
// =============================================================================

using System.Buffers;
using System.Text;
using System.Text.Json;

internal static class McpProtocol
{
    // 2024-11-05 is the revision every shipping client accepts. We advertise
    // only `tools`: no resources, no prompts, no sampling.
    internal const string ProtocolVersion = "2024-11-05";
    internal const string ServerName = "codegraph";
    internal const string ServerVersion = "1.0.0";

    private const string ToolNamePrefix = "codegraph_";

    /// <summary>True for methods this layer owns; everything else falls through to the RPC table.</summary>
    internal static bool Owns(string method) => method switch
    {
        "initialize" => true,
        "ping" => true,
        "tools/list" => true,
        "tools/call" => true,
        "notifications/initialized" => true,
        "notifications/cancelled" => true,
        _ => false
    };

    /// <summary>Tool name (<c>codegraph_explore</c>) -> RPC method (<c>codegraph/explore</c>).</summary>
    internal static string? ToRpcMethod(string toolName)
    {
        if (!toolName.StartsWith(ToolNamePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var shortName = toolName[ToolNamePrefix.Length..];
        return shortName.Length == 0 ? null : "codegraph/" + shortName;
    }

    internal static async Task HandleAsync(
        string method,
        JsonElement? id,
        JsonElement parameters,
        CodeGraphServer server,
        IWorkerRequestContext context,
        Stream stdout,
        SemaphoreSlim writeGate)
    {
        switch (method)
        {
            case "notifications/initialized":
            case "notifications/cancelled":
                // Notifications carry no id and get no reply. Silence is the
                // correct answer; replying would break the client's framing.
                return;

            case "initialize":
                await WriteResultAsync(stdout, writeGate, id, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("protocolVersion", ProtocolVersion);
                    writer.WriteStartObject("capabilities");
                    writer.WriteStartObject("tools");
                    writer.WriteBoolean("listChanged", false);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteStartObject("serverInfo");
                    writer.WriteString("name", ServerName);
                    writer.WriteString("version", ServerVersion);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }).ConfigureAwait(false);
                return;

            case "ping":
                await WriteResultAsync(stdout, writeGate, id, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }).ConfigureAwait(false);
                return;

            case "tools/list":
                await HandleToolsListAsync(id, parameters, server, context, stdout, writeGate)
                    .ConfigureAwait(false);
                return;

            case "tools/call":
                await HandleToolsCallAsync(id, parameters, server, context, stdout, writeGate)
                    .ConfigureAwait(false);
                return;
        }
    }

    private static async Task HandleToolsListAsync(
        JsonElement? id,
        JsonElement parameters,
        CodeGraphServer server,
        IWorkerRequestContext context,
        Stream stdout,
        SemaphoreSlim writeGate)
    {
        if (!server.TryGet("codegraph/tools-list", out var handler))
        {
            await WriteErrorAsync(stdout, writeGate, id, -32603, "Tool surface unavailable: codegraph/tools-list is not registered.")
                .ConfigureAwait(false);
            return;
        }

        var payload = await SerializeAsync(() => handler(parameters, context)).ConfigureAwait(false);

        // The handler already applied every surface transform (default face,
        // allowlist, tiny-repo gating), so `tools` is copied through verbatim —
        // name / description / inputSchema / annotations are already the
        // camelCase shape MCP expects.
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            await WriteErrorAsync(stdout, writeGate, id, -32603, "Tool surface unavailable: no tools array in codegraph/tools-list result.")
                .ConfigureAwait(false);
            return;
        }

        var copy = tools.Clone();
        await WriteResultAsync(stdout, writeGate, id, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tools");
            foreach (var tool in copy.EnumerateArray())
            {
                tool.WriteTo(writer);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private static async Task HandleToolsCallAsync(
        JsonElement? id,
        JsonElement parameters,
        CodeGraphServer server,
        IWorkerRequestContext context,
        Stream stdout,
        SemaphoreSlim writeGate)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            await WriteErrorAsync(stdout, writeGate, id, -32602, "Invalid params: tools/call requires a string `name`.")
                .ConfigureAwait(false);
            return;
        }

        var toolName = nameElement.GetString()!;
        var rpcMethod = ToRpcMethod(toolName);
        if (rpcMethod is null)
        {
            await WriteErrorAsync(stdout, writeGate, id, -32602, $"Invalid params: unknown tool `{toolName}`.")
                .ConfigureAwait(false);
            return;
        }

        if (!server.TryGet(rpcMethod, out var handler))
        {
            await WriteErrorAsync(stdout, writeGate, id, -32601, $"Method not found: {rpcMethod}")
                .ConfigureAwait(false);
            return;
        }

        var arguments = parameters.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : default;

        var payload = await SerializeAsync(() => handler(arguments, context)).ConfigureAwait(false);

        // Tool-shaped results carry `text` + `isError`; everything else (index /
        // stats / analytics / ...) is a structured DTO, so its JSON is the text.
        string text = payload;
        var isError = false;
        using (var document = JsonDocument.Parse(payload))
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    text = textElement.GetString()!;
                }

                if (root.TryGetProperty("isError", out var isErrorElement)
                    && isErrorElement.ValueKind == JsonValueKind.True)
                {
                    isError = true;
                }
            }
        }

        await WriteResultAsync(stdout, writeGate, id, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (isError)
            {
                writer.WriteBoolean("isError", true);
            }
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a handler and flattens its lazy <see cref="WorkerResponse"/> into a
    /// UTF-8 JSON string. The handler itself stays untouched — it writes the
    /// same body it always did.
    /// </summary>
    private static async Task<string> SerializeAsync(Func<Task<WorkerResponse>> invoke)
    {
        var response = await invoke().ConfigureAwait(false);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, JsonFormat.WriterOptions))
        {
            response.WriteBody(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static async Task WriteResultAsync(
        Stream stdout,
        SemaphoreSlim writeGate,
        JsonElement? id,
        Action<Utf8JsonWriter> writeResult)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, JsonFormat.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WritePropertyName("result");
            writeResult(writer);
            writer.WriteEndObject();
        }

        await JsonFormat.WriteLineAsync(stdout, writeGate, buffer.WrittenMemory).ConfigureAwait(false);
    }

    internal static async Task WriteErrorAsync(
        Stream stdout,
        SemaphoreSlim writeGate,
        JsonElement? id,
        int code,
        string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, JsonFormat.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await JsonFormat.WriteLineAsync(stdout, writeGate, buffer.WrittenMemory).ConfigureAwait(false);
    }

    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
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
}
