using System.Net;
using System.Text;
using DenGateway.Service.Clients;

namespace DenGateway.Service.Tests;

public class HttpDenCoreClientTests
{
    [Fact]
    public async Task ListActiveBindingsReadsCoreGatewayItemsProjection()
    {
        using var client = new HttpClient(new JsonHandler("""
            {
              "generated_at": "2026-05-14T07:32:06Z",
              "items": [
                {
                  "instance_id": "den-k8plus:den-hermes-runner:coder:canary",
                  "project_id": "den-hermes-bridge",
                  "agent_identity": "den-hermes-runner",
                  "agent_family": "hermes",
                  "role": "coder",
                  "transport_kind": "hermes_profile",
                  "session_id": "den-k8plus-den-hermes-runner-coder-canary",
                  "status": "active",
                  "checked_in_at": "2026-05-14T07:31:23Z",
                  "last_heartbeat": "2026-05-14T07:31:23Z",
                  "metadata": {"profile":"den-hermes-runner","scope":"canary"}
                }
              ]
            }
            """))
        {
            BaseAddress = new Uri("http://den-core.test/")
        };
        var core = new HttpDenCoreClient(client);

        var result = await core.ListActiveBindingsAsync();

        Assert.True(result.IsAvailable);
        var item = Assert.Single(result.Items);
        Assert.Equal("hermes_profile", item.AdapterKind);
        Assert.Equal("den-k8plus:den-hermes-runner:coder:canary", item.AdapterInstanceId);
        Assert.Equal("den-hermes-runner", item.AgentIdentity);
        Assert.Equal("den-hermes-bridge", item.ProjectId);
        Assert.Equal("coder", item.Role);
        Assert.Equal("active", item.Status);
        Assert.Equal("den-hermes-runner", item.Metadata["profile"]);
        Assert.Equal("canary", item.Metadata["scope"]);
        Assert.Equal("hermes", item.Metadata["agentFamily"]);
        Assert.Equal("den-k8plus-den-hermes-runner-coder-canary", item.Metadata["sessionId"]);
    }

    [Fact]
    public async Task SourceSummaryFlattensNestedGatewayMetadataForSyntheticSentinelEvents()
    {
        using var client = new HttpClient(new JsonHandler("""
            {
              "source_kind": "agent_stream_entry",
              "source_id": "427202",
              "source_project_id": "den-gateway",
              "title": "Agent stream gateway_sentinel_gateway_smoke_probe from den-gateway-smoke",
              "summary": "Gateway sentinel gateway_smoke_probe is normal.",
              "actor": "den-gateway-smoke",
              "severity": "normal",
              "deep_link": "den://project/den-gateway/agent-stream/427202",
              "created_at": "2026-05-14T06:00:41Z",
              "metadata": {
                "stream_kind": "ops",
                "delivery_mode": "record_only",
                "metadata": {
                  "gateway_contract": "sentinel_event/v1",
                  "gateway_metadata": {
                    "synthetic": "true",
                    "targetIdentity": "den-gateway-runner",
                    "deliveryMode": "wake",
                    "reason": "visible_agent_smoke"
                  }
                }
              }
            }
            """))
        {
            BaseAddress = new Uri("http://den-core.test/")
        };
        var core = new HttpDenCoreClient(client);

        var result = await core.GetSourceSummaryAsync("agent_stream_entry", "427202", "den-gateway");

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Value);
        Assert.Equal("agent_stream_entry", result.Value.SourceKind);
        Assert.Equal("427202", result.Value.SourceId);
        Assert.Equal("den-gateway", result.Value.SourceProjectId);
        Assert.Equal("den-gateway-runner", result.Value.Metadata["targetIdentity"]);
        Assert.Equal("wake", result.Value.Metadata["deliveryMode"]);
        Assert.Equal("visible_agent_smoke", result.Value.Metadata["reason"]);
        Assert.Equal("true", result.Value.Metadata["synthetic"]);
        Assert.Equal("ops", result.Value.Metadata["stream_kind"]);
    }

    [Fact]
    public async Task EventOutboxMapsSnakeCaseContractFields()
    {
        using var client = new HttpClient(new JsonHandler("""
            {
              "items": [
                {
                  "cursor": "000000427203",
                  "event_type": "agent_stream.gateway_sentinel_visible_agent_smoke_probe",
                  "source_kind": "agent_stream_entry",
                  "source_id": "427203",
                  "source_project_id": "den-gateway",
                  "title": "gateway sentinel smoke",
                  "summary": "wake runner",
                  "actor": "den-gateway-visible-agent-smoke",
                  "severity": "normal",
                  "deep_link": "den://project/den-gateway/agent-stream/427203",
                  "dedupe_key": "den-gateway-visible-agent-smoke:427203",
                  "occurred_at": "2026-05-14T06:10:53Z",
                  "metadata": {"stream_kind":"ops"}
                }
              ],
              "next_cursor": "000000427204",
              "has_more": false
            }
            """))
        {
            BaseAddress = new Uri("http://den-core.test/")
        };
        var core = new HttpDenCoreClient(client);

        var result = await core.ReadEventOutboxAsync(after: null, projectId: "den-gateway", limit: 50);

        Assert.True(result.IsAvailable);
        var item = Assert.Single(result.Items);
        Assert.Equal("000000427203", item.Cursor);
        Assert.Equal("agent_stream.gateway_sentinel_visible_agent_smoke_probe", item.EventType);
        Assert.Equal("agent_stream_entry", item.SourceKind);
        Assert.Equal("427203", item.SourceId);
        Assert.Equal("den-gateway", item.ProjectId);
        Assert.Equal("den-gateway-visible-agent-smoke:427203", item.DedupeKey);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
