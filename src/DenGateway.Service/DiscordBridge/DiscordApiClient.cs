using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace DenGateway.Service.DiscordBridge;

/// <summary>Direct HTTP client for the Discord API. Does not use Hermes send_message.</summary>
public sealed class DiscordApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<DiscordBridgeOptions> _options;
    private readonly bool _enabled;
    private readonly string? _botToken;

    private const string DiscordApiBase = "https://discord.com/api/v10";

    public DiscordApiClient(IHttpClientFactory httpClientFactory, IOptions<DiscordBridgeOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _enabled = options.Value.Enabled;
        _botToken = options.Value.BotToken;
    }

    /// <summary>
    /// Post a message to a Discord channel/thread.
    /// Returns a DiscordSendResult with status and message id or error details.
    /// </summary>
    public async Task<DiscordSendResult> SendMessageAsync(
        string channelId,
        string? threadId,
        DiscordMessagePayload payload,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return new DiscordSendResult("disabled", ErrorMessage: "Discord bridge is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(_botToken))
        {
            return new DiscordSendResult("unconfigured", ErrorCode: "NO_BOT_TOKEN", ErrorMessage: "Bot token is not configured.");
        }

        var endpoint = threadId is not null
            ? $"{DiscordApiBase}/channels/{threadId}/messages"
            : $"{DiscordApiBase}/channels/{channelId}/messages";

        var client = _httpClientFactory.CreateClient("DiscordApi");
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", _botToken);

        try
        {
            using var response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var messageResponse = await response.Content.ReadFromJsonAsync<DiscordMessageResponse>(cancellationToken: cancellationToken);
                var messageId = messageResponse?.Id;
                return new DiscordSendResult("sent", DiscordMessageId: messageId);
            }

            // Attempt to parse error details from Discord error response
            var errorBody = await TryReadErrorBodyAsync(response.Content, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (statusCode == 429)
            {
                return new DiscordSendResult(
                    "rate_limited",
                    ErrorCode: "RATE_LIMITED",
                    ErrorMessage: $"Discord 429 rate limit: {errorBody ?? "no retry_after info"}");
            }

            return new DiscordSendResult(
                "failed",
                ErrorCode: $"HTTP_{statusCode}",
                ErrorMessage: errorBody ?? $"Discord API returned {statusCode}");
        }
        catch (HttpRequestException ex)
        {
            return new DiscordSendResult(
                "failed",
                ErrorCode: "NETWORK_ERROR",
                ErrorMessage: ex.Message);
        }
        catch (TaskCanceledException)
        {
            return new DiscordSendResult(
                "failed",
                ErrorCode: "TIMEOUT",
                ErrorMessage: "Discord API request timed out.");
        }
    }

    private static async Task<string?> TryReadErrorBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            var body = await content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            // Try to parse structured Discord error
            var error = JsonSerializer.Deserialize<DiscordErrorResponse>(body);
            if (error?.Message is not null)
                return $"{error.Message} (code={error.Code})";

            // Truncate raw body to avoid leaking secrets (though Discord error bodies are safe)
            return body.Length > 500 ? body[..500] + "..." : body;
        }
        catch
        {
            return null;
        }
    }
}
