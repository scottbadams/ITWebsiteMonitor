using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WebsiteMonitor.Notifications.Webhooks;

public sealed class HttpClientWebhookSender : IWebhookSender
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpClientWebhookSender> _logger;

    public HttpClientWebhookSender(HttpClient http, ILogger<HttpClientWebhookSender> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task PostAsync(string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Webhook POST failed {Status} Url={Url} Body={Body}", (int)resp.StatusCode, url, body);
            resp.EnsureSuccessStatusCode();
        }
    }
}
