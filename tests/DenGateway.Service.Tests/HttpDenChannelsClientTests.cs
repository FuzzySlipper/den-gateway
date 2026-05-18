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
                        settingsLabel = "profile den-gateway-runner"
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
        Assert.Equal("profile den-gateway-runner", member.Settings["settingsLabel"]);
    }

    [Fact]
    public async Task ListProjectMembershipsAsyncMapsDefaultProjectSurface()
    {
        var client = NewClient((request, _) =>
        {
            Assert.Equal("/api/gateway/memberships", request.RequestUri!.AbsolutePath);
            Assert.Equal("projectId=den-network", request.RequestUri.Query.TrimStart('?'));
            return Json(new
            {
                channelId = 8,
                channelSlug = "project-den-network",
                channelKind = "project_default",
                projectId = "den-network",
                members = new[]
                {
                    new
                    {
                        id = 1,
                        memberType = "agent",
                        memberIdentity = "sysadmin",
                        membershipStatus = "active",
                        wakePolicy = "all_human_messages",
                        canSend = true,
                        cooldownSeconds = 60,
                        maxAutoRepliesPerWindow = 2,
                        settingsLabel = "sysadmin lane"
                    }
                }
            });
        });

        var memberships = await client.ListProjectMembershipsAsync("den-network");

        Assert.True(memberships.IsAvailable);
        Assert.NotNull(memberships.Value);
        Assert.Equal("8", memberships.Value.ChannelId);
        Assert.Equal("project-den-network", memberships.Value.ChannelSlug);
        Assert.Equal("project_default", memberships.Value.ChannelKind);
        Assert.Equal("den-network", memberships.Value.ProjectId);
        var member = Assert.Single(memberships.Value.Members);
        Assert.Equal("sysadmin", member.MemberIdentity);
        Assert.Equal("all_human_messages", member.WakePolicy);
        Assert.Equal("sysadmin lane", member.Settings["settingsLabel"]);
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
    public async Task GetLatestChannelEventCursorAsyncWalksPagesToLatestCursor()
    {
        var calls = 0;
        var client = NewClient((request, _) =>
        {
            calls++;
            Assert.Equal("/api/gateway/events", request.RequestUri!.AbsolutePath);
            if (calls == 1)
            {
                Assert.Equal("projectId=den-network&afterId=0&limit=200", request.RequestUri.Query.TrimStart('?'));
                return Json(new
                {
                    items = new[]
                    {
                        new { id = 10, channelId = 8, messageKind = "human_text", sourceKind = "channel_message", sourceId = "10", dedupeKey = "event:10", createdAt = "2026-05-13T07:00:00Z" }
                    },
                    nextAfterId = 10,
                    hasMore = true
                });
            }

            Assert.Equal("projectId=den-network&afterId=10&limit=200", request.RequestUri.Query.TrimStart('?'));
            return Json(new
            {
                items = new[]
                {
                    new { id = 11, channelId = 8, messageKind = "human_text", sourceKind = "channel_message", sourceId = "11", dedupeKey = "event:11", createdAt = "2026-05-13T07:01:00Z" },
                    new { id = 12, channelId = 8, messageKind = "human_text", sourceKind = "channel_message", sourceId = "12", dedupeKey = "event:12", createdAt = "2026-05-13T07:02:00Z" }
                },
                nextAfterId = 12,
                hasMore = false
            });
        });

        var result = await client.GetLatestChannelEventCursorAsync("den-network");

        Assert.True(result.IsAvailable);
        Assert.Equal("12", result.Value);
        Assert.Equal(2, calls);
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
