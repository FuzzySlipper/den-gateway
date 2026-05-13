using System.Net;
using System.Net.Http.Json;
using DenGateway.Service.Clients;

namespace DenGateway.Service.Tests;

public class HttpDenCoreClientTests
{
    [Fact]
    public async Task GetHealthAsyncMapsGatewayReadinessResponse()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/gateway/readiness", request.RequestUri!.AbsolutePath);
            return Json(new
            {
                status = "ready",
                service = "den-core-gateway-contract",
                checkedAt = "2026-05-13T22:00:00Z",
                checks = new Dictionary<string, object>
                {
                    ["database"] = "ok",
                    ["gateway_contract"] = "ok"
                }
            });
        });

        var health = await client.GetHealthAsync();

        Assert.True(health.IsAvailable);
        Assert.Equal("http", health.Mode);
        Assert.Equal("available", health.Status);
        Assert.Contains("ready", health.Message);
    }

    [Fact]
    public async Task GetHealthAsyncReportsBlockedReadinessAsUnavailable()
    {
        var client = NewClient((_, _) => Json(new { status = "blocked", service = "den-core-gateway-contract", checks = new { database = "missing" } }));

        var health = await client.GetHealthAsync();

        Assert.False(health.IsAvailable);
        Assert.Equal("not_ready", health.ErrorCode);
        Assert.Contains("blocked", health.Message);
    }

    [Fact]
    public async Task ListActiveBindingsAsyncMapsGatewayBindingProjection()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/gateway/bindings", request.RequestUri!.AbsolutePath);
            Assert.Equal("status=active%2Cdegraded", request.RequestUri.Query.TrimStart('?'));
            return Json(new
            {
                bindings = new[]
                {
                    new
                    {
                        instanceId = "hermes:den-k8:runner:main",
                        projectId = "den-gateway",
                        agentIdentity = "den-gateway-runner",
                        role = "runner",
                        transportKind = "hermes_profile",
                        sessionId = "session-1",
                        status = "active",
                        checkedInAt = "2026-05-13T22:00:00Z",
                        lastHeartbeat = "2026-05-13T22:01:00Z",
                        metadata = new Dictionary<string, string> { ["profile"] = "runner" }
                    }
                }
            });
        });

        var result = await client.ListActiveBindingsAsync();

        Assert.True(result.IsAvailable);
        var binding = Assert.Single(result.Items);
        Assert.Equal("hermes_profile", binding.AdapterKind);
        Assert.Equal("hermes:den-k8:runner:main", binding.AdapterInstanceId);
        Assert.Equal("den-gateway-runner", binding.AgentIdentity);
        Assert.Equal("runner", binding.Role);
        Assert.Equal("active", binding.Status);
        Assert.Equal("session-1", binding.Metadata["sessionId"]);
        Assert.Equal("runner", binding.Metadata["profile"]);
    }

    [Fact]
    public async Task GetSourceSummaryAsyncMapsCoreSourceSummary()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/source-summaries/task_message/5833", request.RequestUri!.AbsolutePath);
            Assert.Equal("projectId=den-gateway", request.RequestUri.Query.TrimStart('?'));
            return Json(new
            {
                sourceKind = "task_message",
                sourceId = "5833",
                sourceProjectId = "den-gateway",
                title = "Task update",
                summary = "summary",
                deepLink = "den://project/den-gateway/message/5833",
                occurredAt = "2026-05-13T22:00:00Z",
                actor = "patch",
                severity = "normal",
                metadata = new Dictionary<string, string> { ["taskId"] = "1391" }
            });
        });

        var result = await client.GetSourceSummaryAsync("task_message", "5833", "den-gateway");

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Value);
        Assert.Equal("Task update", result.Value.Title);
        Assert.Equal("1391", result.Value.Metadata["taskId"]);
    }

    private static HttpDenCoreClient NewClient(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new DelegateHandler(handler)) { BaseAddress = new Uri("http://den-core.test") };
        return new HttpDenCoreClient(httpClient);
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = JsonContent.Create(value)
    };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
