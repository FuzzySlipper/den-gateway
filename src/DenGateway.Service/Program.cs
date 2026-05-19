using DenGateway.Service.Activity;
using DenGateway.Service.Bindings;
using DenGateway.Service.Clients;
using DenGateway.Service.DeliveryLoop;
using DenGateway.Service.Persistence;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DenGatewayOptions>()
    .Bind(builder.Configuration.GetSection(DenGatewayOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Database.Path), "DenGateway:Database:Path is required")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Sentinel.SentinelId), "DenGateway:Sentinel:SentinelId is required")
    .ValidateOnStart();

var configuredOptions = builder.Configuration.GetSection(DenGatewayOptions.SectionName).Get<DenGatewayOptions>() ?? new DenGatewayOptions();
builder.Services.AddSingleton(sp => new GatewayDatabase(sp.GetRequiredService<IOptions<DenGatewayOptions>>().Value.Database.Path));
builder.Services.AddSingleton(sp => new BindingSnapshotSettings(sp.GetRequiredService<IOptions<DenGatewayOptions>>().Value.Sentinel.BindingTtlMinutes));
builder.Services.AddSingleton<BindingSnapshotService>();
builder.Services.AddSingleton<ChannelActivityEventRouter>();
builder.Services.AddSingleton<GatewayDeliveryLoopService>();
builder.Services.AddSingleton<GatewayChannelProjectDiscoveryService>();
builder.Services.AddHostedService<GatewayDeliveryLoopHostedService>();

if (configuredOptions.DenCore.UseStub)
{
    builder.Services.AddSingleton<IDenCoreClient>(new StubDenCoreClient([]));
}
else
{
    builder.Services.AddHttpClient<IDenCoreClient, HttpDenCoreClient>(client =>
    {
        client.BaseAddress = new Uri(EnsureTrailingSlash(configuredOptions.DenCore.BaseUrl));
        client.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(configuredOptions.ServiceAuth.ServiceToken))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", configuredOptions.ServiceAuth.ServiceToken);
        }
    });
}

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

if (app.Services.GetRequiredService<IOptions<DenGatewayOptions>>().Value.Database.ApplyMigrationsOnStartup)
{
    await app.Services.GetRequiredService<GatewayDatabase>().InitializeAsync();
}

app.MapGet("/", () => Results.Redirect("/health/live"));

app.MapGet("/health/live", () => Results.Ok(new HealthLiveResponse("live", "den-gateway")));

app.MapGet("/health/ready", async (IOptions<DenGatewayOptions> options, IDenCoreClient denCoreClient, IDenChannelsClient denChannelsClient, BindingSnapshotService bindingSnapshots) =>
{
    var value = options.Value;
    var denCoreHealth = value.DenCore.UseStub
        ? ServiceHealthResult.Available("stub", "Den Core stub is configured.")
        : await denCoreClient.GetHealthAsync();
    var denChannelsHealth = value.DenChannels.UseStub
        ? ServiceHealthResult.Available("stub", "Den Channels stub is configured.")
        : await denChannelsClient.GetHealthAsync();
    var ready = denCoreHealth.IsAvailable && denChannelsHealth.IsAvailable;
    var bindingHealth = await bindingSnapshots.GetHealthAsync(DateTimeOffset.UtcNow);
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
            baseUrl = value.DenCore.BaseUrl,
            available = denCoreHealth.IsAvailable,
            status = denCoreHealth.Status,
            errorCode = denCoreHealth.ErrorCode,
            message = denCoreHealth.Message
        },
        ["denChannels"] = new
        {
            mode = value.DenChannels.UseStub ? "stub" : "http",
            baseUrl = value.DenChannels.BaseUrl,
            available = denChannelsHealth.IsAvailable,
            status = denChannelsHealth.Status,
            errorCode = denChannelsHealth.ErrorCode,
            message = denChannelsHealth.Message
        },
        ["bindings"] = bindingHealth
    };

    return ready
        ? Results.Ok(new HealthReadyResponse("ready", checks))
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/gateway/status", async (IOptions<DenGatewayOptions> options, BindingSnapshotService bindingSnapshots, DateTimeOffset? now) =>
{
    var value = options.Value;
    var bindingHealth = await bindingSnapshots.GetHealthAsync(now ?? DateTimeOffset.UtcNow);
    return Results.Ok(new GatewayStatusResponse(
        Service: "den-gateway",
        Status: "ready",
        DatabasePath: value.Database.Path,
        DenCoreMode: value.DenCore.UseStub ? "stub" : "http",
        DenChannelsMode: value.DenChannels.UseStub ? "stub" : "http",
        Sentinel: new SentinelStatusSummary(
            value.Sentinel.SentinelId,
            bindingHealth.Status == "degraded" ? "degraded" : "normal",
            value.Sentinel.PollIntervalSeconds,
            value.Sentinel.BindingTtlMinutes),
        Bindings: bindingHealth));
});

app.MapGet("/api/sentinel/status", async (IOptions<DenGatewayOptions> options, BindingSnapshotService bindingSnapshots, DateTimeOffset? now) =>
{
    var sentinel = options.Value.Sentinel;
    var bindingHealth = await bindingSnapshots.GetHealthAsync(now ?? DateTimeOffset.UtcNow);
    return Results.Ok(new SentinelStatusResponse(
        sentinel.SentinelId,
        bindingHealth.Status == "degraded" ? "degraded" : "normal",
        sentinel.PollIntervalSeconds,
        sentinel.DegradedFailureThreshold,
        sentinel.DownFailureThreshold,
        sentinel.StableSuccessThreshold,
        bindingHealth));
});

app.MapPost("/api/deliveries/claim", async (GatewayDatabase database, DeliveryClaimRequest request, CancellationToken cancellationToken) =>
{
    var result = await database.ClaimDeliveriesAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.MapPut("/api/adapter-bindings/heartbeat", async (GatewayDatabase database, AdapterBindingHeartbeatRequest request, CancellationToken cancellationToken) =>
{
    var bindingId = await database.UpsertAdapterBindingHeartbeatAsync(request.ToHeartbeat(), cancellationToken);
    return Results.Ok(new AdapterBindingHeartbeatResponse(bindingId));
});

app.MapPost("/api/deliveries/{id:long}/delivered", async (long id, GatewayDatabase database, DeliveryCallbackRequest request, CancellationToken cancellationToken) =>
{
    var result = await database.ApplyDeliveryCallbackAsync(id, "delivered", request, cancellationToken);
    return result.Status == "not_found" ? Results.NotFound(result) : Results.Ok(result);
});

app.MapPost("/api/deliveries/{id:long}/ack", async (long id, GatewayDatabase database, DeliveryCallbackRequest request, CancellationToken cancellationToken) =>
{
    var result = await database.ApplyDeliveryCallbackAsync(id, "acknowledged", request, cancellationToken);
    return result.Status == "not_found" ? Results.NotFound(result) : Results.Ok(result);
});

app.MapPost("/api/deliveries/{id:long}/fail", async (long id, GatewayDatabase database, DeliveryCallbackRequest request, CancellationToken cancellationToken) =>
{
    var result = await database.ApplyDeliveryCallbackAsync(id, "failed", request, cancellationToken);
    return result.Status == "not_found" ? Results.NotFound(result) : Results.Ok(result);
});

app.MapPost("/api/deliveries/{id:long}/complete", async (long id, GatewayDatabase database, DeliveryCallbackRequest request, CancellationToken cancellationToken) =>
{
    var result = await database.ApplyDeliveryCallbackAsync(id, "completed", request, cancellationToken);
    return result.Status == "not_found" ? Results.NotFound(result) : Results.Ok(result);
});

app.MapPost("/api/deliveries/{id:long}/expire", async (long id, GatewayDatabase database, DeliveryCallbackRequest request, CancellationToken cancellationToken) =>
{
    var result = await database.ApplyDeliveryCallbackAsync(id, "expired", request, cancellationToken);
    return result.Status == "not_found" ? Results.NotFound(result) : Results.Ok(result);
});

app.MapPost("/api/delivery-loop/poll", async (GatewayDeliveryLoopService deliveryLoop, GatewayDeliveryPollRequest request, CancellationToken cancellationToken) =>
{
    var result = await deliveryLoop.PollOnceAsync(request, cancellationToken);
    return result.Status == "rejected" ? Results.BadRequest(result) : Results.Ok(result);
});

app.MapPost("/api/channel-activity-events", async (ChannelActivityEventRouter router, GatewayChannelActivityEventRequest request,
    CancellationToken cancellationToken) =>
{
    var result = await router.RouteAsync(request, cancellationToken);
    return result.Status == "rejected" ? Results.BadRequest(result) : Results.Ok(result);
});

app.MapGet("/api/channel-activity-events/status", (ChannelActivityEventRouter router) => Results.Ok(router.GetStatus()));

app.MapPost("/api/binding-snapshots/refresh", async (BindingSnapshotService bindingSnapshots, BindingSnapshotRefreshRequest request, CancellationToken cancellationToken) =>
{
    var result = await bindingSnapshots.RefreshAsync(request.Now ?? DateTimeOffset.UtcNow, cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/binding-snapshots", async (BindingSnapshotService bindingSnapshots, DateTimeOffset? now, CancellationToken cancellationToken) =>
{
    var observedAt = now ?? DateTimeOffset.UtcNow;
    var items = await bindingSnapshots.ListAsync(observedAt, cancellationToken);
    var health = await bindingSnapshots.GetHealthAsync(observedAt, cancellationToken);
    return Results.Ok(new BindingSnapshotListResponse(items, health));
});

app.Run();

static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

public partial class Program;

public sealed class DenGatewayOptions
{
    public const string SectionName = "DenGateway";

    public DatabaseOptions Database { get; init; } = new();
    public ServiceClientOptions DenCore { get; init; } = new() { BaseUrl = "http://192.168.1.10:18080/den-core-api", UseStub = true };
    public ServiceClientOptions DenChannels { get; init; } = new() { BaseUrl = "http://192.168.1.10:18080", UseStub = true };
    public ServiceAuthOptions ServiceAuth { get; init; } = new();
    public SentinelOptions Sentinel { get; init; } = new();
    public DeliveryLoopOptions DeliveryLoop { get; init; } = new();
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

public sealed class DeliveryLoopOptions
{
    public bool Enabled { get; init; } = false;
    public string Source { get; init; } = "all";
    public string? ProjectId { get; init; }
    public string[] ProjectIds { get; init; } = [];
    public bool DiscoverProjects { get; init; } = true;
    public string[] ExcludedProjectIds { get; init; } = [];
    public bool SeedNewProjectCursorsAtLatest { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 10;
    public int Limit { get; init; } = 100;
}

public sealed record HealthLiveResponse(string Status, string Service);
public sealed record HealthReadyResponse(string Status, IReadOnlyDictionary<string, object?> Checks);
public sealed record GatewayStatusResponse(string Service, string Status, string DatabasePath, string DenCoreMode, string DenChannelsMode, SentinelStatusSummary Sentinel, BindingSnapshotHealth Bindings);
public sealed record SentinelStatusSummary(string SentinelId, string State, int PollIntervalSeconds, int BindingTtlMinutes);
public sealed record SentinelStatusResponse(string SentinelId, string State, int PollIntervalSeconds, int DegradedFailureThreshold, int DownFailureThreshold, int StableSuccessThreshold, BindingSnapshotHealth Bindings);

public sealed record AdapterBindingHeartbeatRequest(
    [property: JsonPropertyName("adapter_kind")] string AdapterKind,
    [property: JsonPropertyName("adapter_instance_id")] string AdapterInstanceId,
    [property: JsonPropertyName("agent_identity")] string? AgentIdentity,
    [property: JsonPropertyName("user_identity")] string? UserIdentity,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("capabilities_json")] string? CapabilitiesJson,
    [property: JsonPropertyName("metadata_json")] string? MetadataJson,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt)
{
    public AdapterBindingHeartbeat ToHeartbeat() => new(
        AdapterKind,
        AdapterInstanceId,
        AgentIdentity,
        UserIdentity,
        ProjectId,
        Role,
        string.IsNullOrWhiteSpace(Status) ? "active" : Status,
        string.IsNullOrWhiteSpace(CapabilitiesJson) ? "{}" : CapabilitiesJson,
        string.IsNullOrWhiteSpace(MetadataJson) ? "{}" : MetadataJson,
        LastSeenAt ?? DateTimeOffset.UtcNow,
        ExpiresAt);
}

public sealed record AdapterBindingHeartbeatResponse([property: JsonPropertyName("binding_id")] long BindingId);
