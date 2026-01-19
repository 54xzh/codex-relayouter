// Bridge Server：为 WinUI/Android 提供统一的 HTTP/WS 接口，并以子进程方式驱动本机 codex CLI。

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddOptions<codex_bridge_server.Bridge.BridgeSecurityOptions>()
    .Bind(builder.Configuration.GetSection("Bridge:Security"));
builder.Services.AddOptions<codex_bridge_server.Bridge.CodexOptions>()
    .Bind(builder.Configuration.GetSection("Bridge:Codex"));
builder.Services.AddSingleton<codex_bridge_server.Bridge.BridgeRequestAuthorizer>();
builder.Services.AddSingleton<codex_bridge_server.Bridge.CodexCliInfo>();
builder.Services.AddSingleton<codex_bridge_server.Bridge.CodexRunner>();
builder.Services.AddSingleton<codex_bridge_server.Bridge.CodexAppServerRunner>();
builder.Services.AddSingleton<codex_bridge_server.Bridge.CodexSessionStore>();
builder.Services.AddSingleton<codex_bridge_server.Bridge.StatusTextBuilder>();
builder.Services.AddSingleton<codex_bridge_server.Bridge.WebSocketHub>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();

app.UseWebSockets();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/status", (
    HttpContext context,
    codex_bridge_server.Bridge.BridgeRequestAuthorizer authorizer,
    codex_bridge_server.Bridge.StatusTextBuilder statusBuilder,
    string? sessionId) =>
{
    if (!authorizer.IsAuthorized(context))
    {
        return Results.Unauthorized();
    }

    var text = statusBuilder.Build(new codex_bridge_server.Bridge.StatusTextBuilder.StatusTextRequest
    {
        SessionId = sessionId,
    });

    return Results.Text(text, "text/plain; charset=utf-8");
});

app.Map("/ws", async context =>
{
    var hub = context.RequestServices.GetRequiredService<codex_bridge_server.Bridge.WebSocketHub>();
    await hub.HandleAsync(context);
});

app.MapControllers();

app.Run();
