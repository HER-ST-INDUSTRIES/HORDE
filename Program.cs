using HORDE;

const int BUILD = 2;

if (args.Contains("--stdio"))
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();
    builder.Services.AddSingleton<AgentManager>();
    builder.Logging.ClearProviders();
    await builder.Build().RunAsync();
}
else
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddHordeTools();

    var app = builder.Build();
    app.MapHordeTools();
    app.MapGet("/", () => new
    {
        service = "HORDE MCP Server",
        version = "1.0.0",
        build = BUILD,
        endpoint = "/mcp"
    });

    var port = Environment.GetEnvironmentVariable("PORT") ?? "5123";
    Console.WriteLine($"HORDE MCP Server v1.0.0 build {BUILD}");
    Console.WriteLine($"http://0.0.0.0:{port}/mcp");
    await app.RunAsync($"http://0.0.0.0:{port}");
}

public static class HordeExtensions
{
    public static WebApplicationBuilder AddHordeTools(this WebApplicationBuilder builder)
    {
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.IdleTimeout = TimeSpan.FromDays(365))
            .WithToolsFromAssembly();
        builder.Services.AddSingleton<AgentManager>();
        return builder;
    }

    public static WebApplication MapHordeTools(this WebApplication app)
    {
        app.MapMcp("/mcp");
        return app;
    }
}
