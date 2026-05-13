using System.Net;
using System.Net.Http.Json;
using DenGateway.Service.Clients;

namespace DenGateway.Service.Tests;

public class HttpDenChannelsClientTests
{
    [Fact]
    public async Task GetHealthAsyncMapsGatewayHealthResponse()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/gateway/health", request.RequestUri!.AbsolutePath);
            return Json(new { service = "den-channels", status = "ready", endpoints = new[] { "GET /api/gateway/health" } });
        });

        var health = await client.GetHealthAsync();

        Assert.True(health.IsAvailable);
        Assert.Equal("http", health.Mode);
        Assert.Equal("available", health.Status);
        Assert.Contains("ready", health.Message);
    }

    [Fact]
    public async Task ListMembershipsAsyncMapsMembersFromChannelId()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/gateway/memberships", request.RequestUri!.AbsolutePath);
            Assert.Equal("channelId=42", request.RequestUri.Query.TrimStart('?'));
            return Json(new
            {
                channelId = 42,
                channelSlug = "project-den-gateway",
                channelKind = "project_default",
                projectId = "den-gateway",
                members = new[]
                {
                    new
                    {
                        id = 7,
                        memberType = "agent",
                        memberIdentity = "den-gateway-runner",
                        membershipStatus = "active",
                        wakePolicy = "mentions_only",
                        canSend = true,
                        cooldownSeconds = 60,
                        maxAutoRepliesPerWindow = 2,
                        settingsJsonPreview = "{\"x\":1}"
                    }
                }
            });
        });

        var memberships = await client.ListMembershipsAsync("42");

        Assert.True(memberships.IsAvailable);
        var member = Assert.Single(memberships.Items);
        Assert.Equal("42", member.ChannelId);
        Assert.Equal("agent", member.MemberType);
        Assert.Equal("den-gateway-runner", member.MemberIdentity);
        Assert.Equal("active", member.Status);
        Assert.Equal("mentions_only", member.WakePolicy);
        Assert.Equal(60, member.CooldownSeconds);
        Assert.Equal("{\"x\":1}", member.Settings["settingsJsonPreview"]);
    }

    [Fact]
    public async Task GetChannelMessageAsyncMapsMessageSnapshot()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/gateway/messages/99", request.RequestUri!.AbsolutePath);
            return Json(new
            {
                id = 99,
                channelId = 42,
                messageKind = "human_text",
                senderType = "user",
                senderIdentity = "patch",
                sourceKind = "task_message",
                sourceId = "5704",
                sourceProjectId = "den-gateway",
                dedupeKey = "msg:99",
                deepLink = "den://project/den-gateway/message/5704",
                summary = "summary",
                body = "hello",
                createdAt = "2026-05-13T07:00:00Z"
            });
        });

        var message = await client.GetChannelMessageAsync("99");

        Assert.True(message.IsAvailable);
        Assert.NotNull(message.Value);
        Assert.Equal("99", message.Value.ChannelMessageId);
        Assert.Equal("42", message.Value.ChannelId);
        Assert.Equal("human_text", message.Value.MessageKind);
        Assert.Equal("task_message", message.Value.SourceKind);
        Assert.Equal("5704", message.Value.SourceId);
        Assert.Equal("msg:99", message.Value.DedupeKey);
    }

    [Fact]
    public async Task ReadChannelEventsAsyncMapsCursorItems()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/gateway/events", request.RequestUri!.AbsolutePath);
            Assert.Equal("projectId=den-gateway&afterId=10&limit=2", request.RequestUri.Query.TrimStart('?'));
            return Json(new
            {
                items = new[]
                {
                    new
                    {
                        id = 11,
                        channelId = 42,
                        messageKind = "human_text",
                        senderType = "user",
                        senderIdentity = "patch",
                        sourceKind = "channel_message",
                        sourceId = "11",
                        sourceProjectId = "den-gateway",
                        dedupeKey = "event:11",
                        deepLink = "den://channel/42",
                        summary = "",
                        body = "hi",
                        createdAt = "2026-05-13T07:00:00Z"
                    }
                },
                nextAfterId = 11,
                hasMore = false
            });
        });

        var events = await client.ReadChannelEventsAsync("10", "den-gateway", 2);

        Assert.True(events.IsAvailable);
        var item = Assert.Single(events.Items);
        Assert.Equal("11", item.Cursor);
        Assert.Equal("channel_message", item.SourceKind);
        Assert.Equal("event:11", item.DedupeKey);
        Assert.Equal(DateTimeOffset.Parse("2026-05-13T07:00:00Z"), item.OccurredAt);
    }

    [Fact]
    public async Task PostMirrorOrSystemMessageAsyncPostsGatewaySystemMessage()
    {
        var client = NewClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/gateway/system-messages", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<Dictionary<string, object?>>(cancellationToken);
            Assert.NotNull(body);
            Assert.Equal("42", body["channelId"]?.ToString());
            Assert.Equal("system_event", body["messageKind"]?.ToString());
            Assert.Equal("body", body["body"]?.ToString());
            Assert.Equal("dedupe", body["dedupeKey"]?.ToString());
            return Json(new { id = 100, channelId = 42, messageKind = "system_event", senderType = "system", senderIdentity = "den-gateway", body = "body", createdAt = "2026-05-13T07:00:00Z" }, HttpStatusCode.Created);
        });

        var result = await client.PostMirrorOrSystemMessageAsync(new ChannelMirrorMessage(
            ChannelId: "42",
            MessageKind: "system_event",
            Body: "body",
            SourceKind: "sentinel_control",
            SourceId: "pause-1",
            DeepLink: "den://project/den-gateway",
            DedupeKey: "dedupe",
            Metadata: new Dictionary<string, string> { ["scope"] = "all" }));

        Assert.True(result.IsAvailable);
        Assert.Equal("completed", result.Status);
    }

    private static HttpDenChannelsClient NewClient(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new DelegateHandler(handler)) { BaseAddress = new Uri("http://den-channels.test") };
        return new HttpDenChannelsClient(httpClient);
    }

    private static HttpDenChannelsClient NewClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegateHandler(handler)) { BaseAddress = new Uri("http://den-channels.test") };
        return new HttpDenChannelsClient(httpClient);
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = JsonContent.Create(value)
    };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
        {
        }

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
