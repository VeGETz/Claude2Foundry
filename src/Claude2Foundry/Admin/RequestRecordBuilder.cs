namespace Claude2Foundry.Admin;

public static class RequestRecordBuilder
{
    private static readonly HashSet<string> RedactedHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "api-key", "authorization", "x-api-key" };

    public static Dictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in headers)
        {
            result[key] = RedactedHeaders.Contains(key) ? "[redacted]" : values.ToString();
        }
        return result;
    }
}
