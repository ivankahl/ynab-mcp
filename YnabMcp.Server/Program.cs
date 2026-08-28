using YnabMcp.Server;

var builder = WebApplication.CreateBuilder(args);

var accessToken = builder.Configuration["YNAB_ACCESS_TOKEN"]
    ?? throw new InvalidOperationException(
        "YNAB access token is not configured. Set the YNAB_ACCESS_TOKEN environment variable. " +
        "Create a personal access token at https://app.ynab.com/settings/developer");

builder.Services.AddSingleton(new YnabClient(new HttpClient(), accessToken));

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
