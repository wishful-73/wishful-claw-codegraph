// =============================================================================
// Host contracts — CodeGraph's OWN copy.
//
// This file deliberately duplicates five types that used to come from the host
// application (WishfulClaw.Contracts). The point of the split is that this
// repository depends on NOTHING from the host: it compiles standalone, ships as
// its own process, and talks over stdio JSON-RPC. Read this file before adding
// a host dependency back — the whole剥离 effort exists to keep that at zero.
//
// Shapes are kept byte-compatible with the host originals (same member names,
// same signatures) so the ~20 RPC handlers under Mcp/ did not need a single
// edit when the dependency was cut. Do not "clean them up" without checking
// how many call sites assume the current shape.
//
// Only the surface CodeGraph actually uses is here. Deliberately omitted:
//   - WorkerJsonHelper      (0 call sites in this repo)
//   - WorkerRoutesResult    (0)
//   - StatusResult          (0)
//   - WorkerResponse.ToJsonBytes / WriteEvent / BuildJsonElement (0)
//   - WorkerResponse.Json<T>(T) without a JsonTypeInfo — every one of the 16
//     call sites passes CodeGraphJsonContext.Default.*, so the reflection-based
//     overload (the one needing the host's AOT resolver) is gone on purpose.
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

/// <summary>
/// A module that registers named RPC methods. Kept for shape-compatibility with
/// the handlers; the standalone host wires <see cref="CodeGraphModule"/> up
/// through its own dispatcher rather than the application's module catalog.
/// </summary>
public interface IWorkerModule
{
    string Name { get; }

    void Register(IWorkerModuleContext context);

    Task InitializeAsync() => Task.CompletedTask;
}

/// <summary>Registration surface handed to a module at startup.</summary>
public interface IWorkerModuleContext
{
    void Register(string method, Func<JsonElement, Task<WorkerResponse>> handler);

    void Register(string method, Func<JsonElement, WorkerResponse> handler);

    void Register(string method, Func<JsonElement, IWorkerRequestContext, Task<WorkerResponse>> handler);

    void Register(string method, Func<JsonElement, IWorkerRequestContext, WorkerResponse> handler);

    string[] GetRegisteredMethods();
}

/// <summary>
/// Per-request context: cancellation plus the ability to push progress events
/// back to the caller while a long-running method (index / sync) is executing.
/// </summary>
public interface IWorkerRequestContext
{
    CancellationToken CancellationToken { get; }

    CancellationToken ConnectionCancellationToken { get; }

    IWorkerRequestContext ForBackgroundOperation();

    ValueTask EmitEventAsync<T>(string eventName, T parameters, JsonTypeInfo<T> typeInfo);

    ValueTask EmitEventIgnoringCancellationAsync<T>(string eventName, T parameters, JsonTypeInfo<T> typeInfo);

    ValueTask EmitMessagePackEventAsync(string eventName, ReadOnlyMemory<byte> payload);
}

/// <summary>
/// An RPC result, written lazily so a handler can stream straight into the
/// response body without allocating an intermediate string.
/// </summary>
public sealed class WorkerResponse
{
    private readonly Action<Utf8JsonWriter> _resultWriter;

    private WorkerResponse(Action<Utf8JsonWriter> resultWriter)
    {
        _resultWriter = resultWriter;
    }

    /// <summary>
    /// Serializes with an explicit <see cref="JsonTypeInfo{T}"/> — the only
    /// overload this repo uses, because it is the AOT-safe one. Call sites pass
    /// <c>CodeGraphJsonContext.Default.&lt;TypeName&gt;</c>.
    /// </summary>
    public static WorkerResponse Json<T>(T result, JsonTypeInfo<T> typeInfo)
    {
        return new WorkerResponse(writer => JsonSerializer.Serialize(writer, result, typeInfo));
    }

    public static WorkerResponse String(string result)
    {
        return new WorkerResponse(writer => writer.WriteStringValue(result));
    }

    public static WorkerResponse FromWriter(Action<Utf8JsonWriter> writeResult)
    {
        return new WorkerResponse(writeResult);
    }

    public static WorkerResponse RawJson(string result)
    {
        return new WorkerResponse(writer =>
        {
            try
            {
                using var document = JsonDocument.Parse(result);
                document.RootElement.WriteTo(writer);
            }
            catch
            {
                writer.WriteStringValue(result);
            }
        });
    }

    /// <summary>
    /// Error shape: <c>{"error": "..."}</c>. The host original routed this
    /// through its AOT resolver (<c>Json(new ErrorResult(msg))</c>); here it is
    /// written directly so no resolver is needed.
    /// </summary>
    public static WorkerResponse Error(string message)
    {
        return new WorkerResponse(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        });
    }

    /// <summary>Writes the result payload (caller owns the envelope).</summary>
    public void WriteBody(Utf8JsonWriter writer)
    {
        _resultWriter(writer);
    }
}
