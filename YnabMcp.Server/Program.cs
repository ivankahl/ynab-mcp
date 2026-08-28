using YnabMcp.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

builder.Services.AddHttpClient("ynab", client =>
{
    client.BaseAddress = new Uri("https://api.ynab.com/v1/");
});

builder.Services.AddScoped<TokenValidator>();

builder.Services.AddScoped<YnabClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ynab");
    TokenValidator.TryGetToken(sp.GetRequiredService<IHttpContextAccessor>().HttpContext, out var token);
    return new YnabClient(http, token);
});

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.HasValue ||
        !context.Request.Path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
        !HttpMethods.IsPost(context.Request.Method))
    {
        await next();
        return;
    }

    var validator = context.RequestServices.GetRequiredService<TokenValidator>();
    var token = validator.ExtractBearer(context.Request);
    if (token is null)
    {
        await JsonErrorAsync(context, StatusCodes.Status401Unauthorized,
            "Missing or malformed Authorization bearer token. Send 'Authorization: Bearer <ynab-token>'.");
        return;
    }

    var status = await validator.ValidateAsync(token, context.RequestAborted);
    switch (status)
    {
        case TokenValidationStatus.Valid:
            context.Items["YnabToken"] = token;
            await next();
            return;
        case TokenValidationStatus.Invalid:
            await JsonErrorAsync(context, StatusCodes.Status401Unauthorized,
                "Invalid YNAB access token.");
            return;
        default:
            await JsonErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Could not validate the token because YNAB is unavailable. Try again shortly.");
            return;
    }
});

app.MapMcp("/mcp");

app.Run();

static async Task JsonErrorAsync(HttpContext context, int statusCode, string message)
{
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
}
