using Microsoft.AspNetCore.Http.Features;
using SteamVRTranslator.VibeVoice.Server;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>() ?? new ServiceOptions();
options.ListenUrl = Environment.GetEnvironmentVariable("VIBEVOICE_LISTEN_URL") ?? options.ListenUrl;
options.ApiKey = Environment.GetEnvironmentVariable("VIBEVOICE_API_KEY") ?? options.ApiKey;
options.DataDirectory = Environment.GetEnvironmentVariable("VIBEVOICE_DATA_DIRECTORY") ?? options.DataDirectory;
options.Normalize();
options.ValidateRemoteBinding();
builder.WebHost.UseUrls(options.ListenUrl);
builder.Logging.AddProvider(new ServiceFileLoggerProvider(options.ResolveDataDirectory()));
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
    server.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<VibeVoiceRuntimeManager>();

var app = builder.Build();
var runtime = app.Services.GetRequiredService<VibeVoiceRuntimeManager>();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogInformation("Request {Method} {Path} was cancelled by the client.",
            context.Request.Method,
            context.Request.Path);
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Request {Method} {Path} failed.",
            context.Request.Method,
            context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { error = exception.Message },
                context.RequestAborted);
        }
    }
});

app.MapGet("/", () => Results.Content(ManagementPage.Html, "text/html; charset=utf-8"));
app.MapGet("/health", () =>
{
    var status = runtime.GetStatus();
    return Results.Json(new
    {
        status = status.RuntimeRunning ? "ok" : status.Status,
        backend = "vibevoice"
    });
});
app.MapGet("/api/v1/status", (HttpRequest request) =>
    runtime.IsAuthorized(request)
        ? Results.Json(runtime.GetStatus())
        : Results.Unauthorized());
app.MapGet("/v1/models", (HttpRequest request) => runtime.IsAuthorized(request)
    ? Results.Json(new
{
    @object = "list",
    data = new[]
    {
        new { id = "vibevoice-asr-q4_k", @object = "model", owned_by = "local" }
    }
})
    : Results.Unauthorized());

app.MapPost("/api/v1/install", (HttpRequest httpRequest, InstallRequest request) =>
{
    if (!runtime.IsAuthorized(httpRequest))
    {
        return Results.Unauthorized();
    }
    return runtime.StartInstall(request)
        ? Results.Accepted("/api/v1/status", runtime.GetStatus())
        : Results.Conflict(new { error = "An installation is already running." });
});
app.MapPost("/api/v1/configure", async (
    HttpRequest httpRequest,
    ConfigureRequest request,
    CancellationToken cancellationToken) =>
{
    if (!runtime.IsAuthorized(httpRequest))
    {
        return Results.Unauthorized();
    }
    await runtime.ConfigureAsync(request, cancellationToken);
    return Results.Json(runtime.GetStatus());
});
app.MapPost("/api/v1/runtime/start", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!runtime.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    await runtime.StartRuntimeAsync(cancellationToken);
    return Results.Json(runtime.GetStatus());
});
app.MapPost("/api/v1/runtime/stop", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!runtime.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    await runtime.StopRuntimeAsync(cancellationToken);
    return Results.Json(runtime.GetStatus());
});
app.MapPost("/v1/audio/transcriptions", async (
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!runtime.IsAuthorized(context.Request))
    {
        return Results.Unauthorized();
    }

    using var response = await runtime.ProxyAsync(context.Request, cancellationToken);
    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }
    foreach (var header in response.Content.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }
    context.Response.Headers.Remove("transfer-encoding");
    await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    return Results.Empty;
});

if (options.AutoStartRuntime && runtime.GetStatus() is { RuntimeInstalled: true, ModelInstalled: true })
{
    _ = Task.Run(async () =>
    {
        try
        {
            await runtime.StartRuntimeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            app.Logger.LogError(exception, "Unable to auto-start the VibeVoice runtime.");
        }
    });
}

await app.RunAsync();
