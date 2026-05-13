using DenGateway.Service.Clients;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DenGatewayOptions>()
    .Bind(builder.Configuration.GetSection(DenGatewayOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Database.Path), "DenGateway:Database:Path is required")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Sentinel.SentinelId), "DenGateway:Sentinel:SentinelId is required")
    .ValidateOnStart();

var configuredOptions = builder.Configuration.GetSection(DenGatewayOptions.SectionName).Get<DenGatewayOptions>() ?? new DenGatewayOptions();
if (configuredOptions.DenChannels.UseStub)
{
    builder.Services.AddSingleton<IDenChannelsClient>(new StubDenChannelsClient([]));
}
else
{
    builder.Services.AddHttpClient<IDenChannelsClient, HttpDenChannelsClient>(client =>
    {
        client.BaseAddress = new Uri(configuredOptions.DenChannels.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(10);
    });
}

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/health/live"));

app.MapGet("/health/live", () => Results.Ok(new HealthLiveResponse("live", "den-gateway")));

app.MapGet("/health/ready", async (IOptions<DenGatewayOptions> options, IDenChannelsClient denChannelsClient) =>
{
    var value = options.Value;
    var denChannelsHealth = value.DenChannels.UseStub
        ? ServiceHealthResult.Available("stub", "Den Channels stub is configured.")
        : await denChannelsClient.GetHealthAsync();
    var ready = denChannelsHealth.IsAvailable;
    var checks = new Dictionary<string, object?>
    {
        ["configuration"] = "ready",
        ["database"] = new
        {
            configured = !string.IsNullOrWhiteSpace(value.Database.Path),
            path = value.Database.Path,
            applyMigrationsOnStartup = value.Database.ApplyMigrationsOnStartup
        },
        ["denCore"] = new
        {
            mode = value.DenCore.UseStub ? "stub" : "http",
            baseUrl = value.DenCore.BaseUrl
        },
        ["denChannels"] = new
        {
            mode = value.DenChannels.UseStub ? "stub" : "http",
            baseUrl = value.DenChannels.BaseUrl,
            available = denChannelsHealth.IsAvailable,
            status = denChannelsHealth.Status,
            errorCode = denChannelsHealth.ErrorCode,
            message = denChannelsHealth.Message
        }
    };

    return ready
        ? Results.Ok(new HealthReadyResponse("ready", checks))
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/gateway/status", (IOptions<DenGatewayOptions> options) =>
{
    var value = options.Value;
    return Results.Ok(new GatewayStatusResponse(
        Service: "den-gateway",
        Status: "ready",
        DatabasePath: value.Database.Path,
        DenCoreMode: value.DenCore.UseStub ? "stub" : "http",
        DenChannelsMode: value.DenChannels.UseStub ? "stub" : "http",
        Sentinel: new SentinelStatusSummary(
            value.Sentinel.SentinelId,
            "normal",
            value.Sentinel.PollIntervalSeconds,
            value.Sentinel.BindingTtlMinutes)));
});

app.MapGet("/api/sentinel/status", (IOptions<DenGatewayOptions> options) =>
{
    var sentinel = options.Value.Sentinel;
    return Results.Ok(new SentinelStatusResponse(
        sentinel.SentinelId,
        "normal",
        sentinel.PollIntervalSeconds,
        sentinel.DegradedFailureThreshold,
        sentinel.DownFailureThreshold,
        sentinel.StableSuccessThreshold));
});

app.Run();

public partial class Program;

public sealed class DenGatewayOptions
{
    public const string SectionName = "DenGateway";

    public DatabaseOptions Database { get; init; } = new();
    public ServiceClientOptions DenCore { get; init; } = new() { BaseUrl = "http://127.0.0.1:5199", UseStub = true };
    public ServiceClientOptions DenChannels { get; init; } = new() { BaseUrl = "http://192.168.1.10:18080", UseStub = true };
    public ServiceAuthOptions ServiceAuth { get; init; } = new();
    public SentinelOptions Sentinel { get; init; } = new();
}

public sealed class DatabaseOptions
{
    public string Path { get; init; } = "data/den-gateway.db";
    public bool ApplyMigrationsOnStartup { get; init; } = true;
}

public sealed class ServiceClientOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public bool UseStub { get; init; } = true;
}

public sealed class ServiceAuthOptions
{
    public string? ServiceToken { get; init; }
}

public sealed class SentinelOptions
{
    public string SentinelId { get; init; } = "den-k8-sentinel-1";
    public int PollIntervalSeconds { get; init; } = 10;
    public int DegradedFailureThreshold { get; init; } = 2;
    public int DownFailureThreshold { get; init; } = 4;
    public int StableSuccessThreshold { get; init; } = 4;
    public int BindingTtlMinutes { get; init; } = 120;
}

public sealed record HealthLiveResponse(string Status, string Service);
public sealed record HealthReadyResponse(string Status, IReadOnlyDictionary<string, object?> Checks);
public sealed record GatewayStatusResponse(string Service, string Status, string DatabasePath, string DenCoreMode, string DenChannelsMode, SentinelStatusSummary Sentinel);
public sealed record SentinelStatusSummary(string SentinelId, string State, int PollIntervalSeconds, int BindingTtlMinutes);
public sealed record SentinelStatusResponse(string SentinelId, string State, int PollIntervalSeconds, int DegradedFailureThreshold, int DownFailureThreshold, int StableSuccessThreshold);
