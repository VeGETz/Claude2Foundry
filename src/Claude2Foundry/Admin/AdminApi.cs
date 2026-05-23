namespace Claude2Foundry.Admin;

public static class AdminApi
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/config", () => Results.StatusCode(501));
        routes.MapGet("/config/schema", () => Results.StatusCode(501));
        routes.MapPost("/config", () => Results.StatusCode(501));
        routes.MapPost("/config/test-connection", () => Results.StatusCode(501));
        routes.MapPost("/restart", () => Results.StatusCode(501));
        routes.MapGet("/health", () => Results.StatusCode(501));
        routes.MapGet("/events", () => Results.StatusCode(501));
        routes.MapGet("/events/full/{id}", (string id) => Results.StatusCode(501));
        routes.MapPost("/capture-mode", () => Results.StatusCode(501));
        routes.MapPost("/test-request", () => Results.StatusCode(501));
    }
}
