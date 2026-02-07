using HORDE;

const int BUILD = 3;

if (args.Contains("--stdio"))
{
    var b = Host.CreateApplicationBuilder(args);
    b.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
    b.Services.AddSingleton<AgentManager>();
    b.Logging.ClearProviders();
    await b.Build().RunAsync();
}
else
{
    var b = WebApplication.CreateBuilder(args);
    b.Services.AddMcpServer().WithHttpTransport(o => o.IdleTimeout = TimeSpan.FromDays(365)).WithToolsFromAssembly();
    b.Services.AddSingleton<AgentManager>();

    var app = b.Build();
    app.MapMcp("/mcp");
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