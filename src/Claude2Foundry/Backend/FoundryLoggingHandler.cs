using System.Text;
using System.Text.Json;

namespace Claude2Foundry.Backend;

public sealed class FoundryLoggingHandler : DelegatingHandler
{
    private static readonly JsonSerializerOptions MinifyOpts = new() { WriteIndented = false };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[C2F → Foundry] {request.Method} {request.RequestUri}");
            Console.WriteLine(Minify(body));
        }

        var response = await base.SendAsync(request, ct);

        if (response.Content is not null)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[C2F ← Foundry] {response.StatusCode}");
            Console.WriteLine(Minify(body));

            response.Content = new StringContent(body, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return response;
    }

    private static string Minify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, MinifyOpts);
        }
        catch
        {
            return json;
        }
    }
}