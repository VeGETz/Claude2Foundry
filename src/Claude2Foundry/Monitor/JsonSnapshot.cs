using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claude2Foundry.Monitor;

/// <summary>
/// Detaches an object graph from any source JsonDocument so it can be safely
/// serialized later on a background thread, after the originating HTTP request
/// (and its pooled JsonDocument) has been disposed.
/// </summary>
public static class JsonSnapshot
{
    /// <summary>
    /// Round-trips <paramref name="source"/> through JSON, returning a
    /// <see cref="JsonNode"/> that owns its own memory and is safe to hold
    /// indefinitely. Returns <c>null</c> if <paramref name="source"/> is null.
    /// </summary>
    public static object? Take(object? source)
    {
        if (source is null) return null;
        var json = JsonSerializer.Serialize(source);
        return JsonNode.Parse(json);
    }
}
