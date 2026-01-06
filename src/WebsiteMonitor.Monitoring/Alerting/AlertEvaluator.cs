using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebsiteMonitor.Notifications.Smtp;
using WebsiteMonitor.Notifications.Webhooks;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.Monitoring.Alerting;

public sealed class AlertEvaluator
{
    private const string SmtpPasswordPurpose = "ITWebsiteMonitor.SmtpPassword.v1";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeZoneResolver _tz;
    private readonly AlertingOptions _opt;
    private readonly ILogger<AlertEvaluator> _logger;

    public AlertEvaluator(
        IServiceScopeFactory scopeFactory,
        TimeZoneResolver tz,
        IOptions<AlertingOptions> opt,
        ILogger<AlertEvaluator> logger)
    {
        _scopeFactory = scopeFactory;
        _tz = tz;
        _opt = opt.Value;
        _logger = logger;
    }

    // Alerts must stop while instance is runtime-paused (your requirement).
    public async Task EvaluateInstanceAsync(string instanceId, bool instanceRunning, DateTime nowUtc, CancellationToken ct)
    {
        if (!instanceRunning)
            return;

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<WebsiteMonitorDbContext>();
        var smtpSender = sp.GetRequiredService<ISmtpSender>();
        var webhookSender = sp.GetRequiredService<IWebhookSender>();
        var dp = sp.GetRequiredService<IDataProtectionProvider>();

        var instance = await db.Instances.SingleOrDefaultAsync(i => i.InstanceId == instanceId, ct);
        if (instance == null || !instance.Enabled)
            return;

        var tz = _tz.ResolveFromIana(instance.TimeZoneId);

        var targets = await db.Targets
            .Where(t => t.InstanceId == instanceId && t.Enabled)
            .ToListAsync(ct);

        if (targets.Count == 0)
            return;

        var targetIds = targets.Select(t => t.TargetId).ToList();

        var states = await db.States
            .Where(s => targetIds.Contains(s.TargetId))
            .ToListAsync(ct);

        // Email channel
        var recipientEmails = await db.Recipients
            .Where(r => r.InstanceId == instanceId && r.Enabled)
            .Select(r => r.Email)
            .ToListAsync(ct);

        var smtpSettings = await db.SmtpSettings
            .SingleOrDefaultAsync(s => s.InstanceId == instanceId, ct);

        var emailConfigured =
            smtpSettings != null &&
            !string.IsNullOrWhiteSpace(smtpSettings.Host) &&
            smtpSettings.Port > 0 &&
            !string.IsNullOrWhiteSpace(smtpSettings.FromAddress) &&
            recipientEmails.Count > 0;

        string? smtpPasswordPlain = null;
        if (emailConfigured && smtpSettings != null)
            smtpPasswordPlain = TryUnprotectSmtpPassword(dp, smtpSettings, instanceId);

        // Webhook channel
        var webhookUrls = await db.WebhookEndpoints
            .Where(w => w.InstanceId == instanceId && w.Enabled)
            .Select(w => w.Url)
            .ToListAsync(ct);

        var webhooksConfigured = webhookUrls.Count > 0;

        if (!emailConfigured && !webhooksConfigured)
            return;

        foreach (var s in states)
        {
            var target = targets.FirstOrDefault(t => t.TargetId == s.TargetId);
            if (target == null) continue;

            if (!s.IsUp)
            {
                await HandleDownAsync(
                    db,
                    emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
                    webhooksConfigured, webhookSender, webhookUrls,
                    tz,
                    instanceId, target, s, nowUtc, ct);
            }
            else
            {
                await HandleUpAsync(
                    db,
                    emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
                    webhooksConfigured, webhookSender, webhookUrls,
                    tz,
                    instanceId, target, s, nowUtc, ct);
            }
        }

        await SaveChangesWithGateRetryAsync(db, ct);
    }

    private string? TryUnprotectSmtpPassword(IDataProtectionProvider dp, SmtpSettings smtp, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(smtp.PasswordProtected))
            return null;

        try
        {
            var protector = dp.CreateProtector(SmtpPasswordPurpose);
            return protector.Unprotect(smtp.PasswordProtected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unprotect SMTP password. Instance={InstanceId}", instanceId);
            return null;
        }
    }

    private async Task HandleDownAsync(
        WebsiteMonitorDbContext db,
        bool emailConfigured,
        ISmtpSender smtpSender,
        SmtpSettings? smtpSettings,
        string? smtpPasswordPlain,
        List<string> recipientEmails,
        bool webhooksConfigured,
        IWebhookSender webhookSender,
        List<string> webhookUrls,
        TimeZoneInfo tz,
        string instanceId,
        Target target,
        TargetState state,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var downStartUtc = state.StateSinceUtc;
        var downAge = nowUtc - downStartUtc;

        // First DOWN alert due after N seconds continuous failure
        if (state.DownFirstNotifiedUtc == null &&
            downAge >= TimeSpan.FromSeconds(_opt.DownAfterSeconds))
        {
            var downBodyText = await BuildDownBodyAsync(db, instanceId, target, state, downStartUtc, tz, _opt.PublicBaseUrl, ct);

            var notified = await NotifyAsync(
                db,
                "AlertDown",
                emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
                webhooksConfigured, webhookSender, webhookUrls,
                instanceId, target, state, nowUtc,
                subject: "Website Check FAILED!",
                bodyText: downBodyText,
                ct: ct);

            if (!notified)
            {
                _logger.LogError("ALERT SEND FAILED: DOWN Instance={InstanceId} TargetId={TargetId} Url={Url}", instanceId, target.TargetId, target.Url);
                return;
            }

            state.DownFirstNotifiedUtc = nowUtc;
            state.LastNotifiedUtc = nowUtc;
            state.NextNotifyUtc = ComputeNextDownNotifyUtc(downStartUtc, nowUtc, tz, nowUtc);

            db.Events.Add(new Event
            {
                InstanceId = instanceId,
                TargetId = target.TargetId,
                TimestampUtc = nowUtc,
                Type = "AlertDown",
                Message = $"DOWN {target.Url} (down since {downStartUtc:u})"
            });

            _logger.LogWarning("ALERT SENT: DOWN Instance={InstanceId} TargetId={TargetId} Url={Url}", instanceId, target.TargetId, target.Url);
            return;
        }

        // Repeat while still down
        if (state.DownFirstNotifiedUtc != null &&
            state.NextNotifyUtc != null &&
            nowUtc >= state.NextNotifyUtc.Value)
        {
            var downBodyText = await BuildDownBodyAsync(db, instanceId, target, state, downStartUtc, tz, _opt.PublicBaseUrl, ct);

            var notified = await NotifyAsync(
                db,
                "AlertDownRepeat",
                emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
                webhooksConfigured, webhookSender, webhookUrls,
                instanceId, target, state, nowUtc,
                subject: "Website Check FAILED! (repeat)",
                bodyText: downBodyText,
                ct: ct);

            if (!notified)
            {
                _logger.LogError("ALERT SEND FAILED: DOWN REPEAT Instance={InstanceId} TargetId={TargetId} Url={Url}", instanceId, target.TargetId, target.Url);
                return;
            }

            state.LastNotifiedUtc = nowUtc;
            state.NextNotifyUtc = ComputeNextDownNotifyUtc(downStartUtc, nowUtc, tz, nowUtc);

            db.Events.Add(new Event
            {
                InstanceId = instanceId,
                TargetId = target.TargetId,
                TimestampUtc = nowUtc,
                Type = "AlertDownRepeat",
                Message = $"DOWN repeat {target.Url} (down since {downStartUtc:u})"
            });

            _logger.LogWarning("ALERT SENT: DOWN REPEAT Instance={InstanceId} TargetId={TargetId} Url={Url}", instanceId, target.TargetId, target.Url);
        }
    }

    private async Task HandleUpAsync(
        WebsiteMonitorDbContext db,
        bool emailConfigured,
        ISmtpSender smtpSender,
        SmtpSettings? smtpSettings,
        string? smtpPasswordPlain,
        List<string> recipientEmails,
        bool webhooksConfigured,
        IWebhookSender webhookSender,
        List<string> webhookUrls,
        TimeZoneInfo tz,
        string instanceId,
        Target target,
        TargetState state,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Recovered only if a DOWN alert was actually sent for this outage
        if (state.DownFirstNotifiedUtc == null)
        {
            state.RecoveredDueUtc = null;
            state.RecoveredNotifiedUtc = null;
            return;
        }

        if (state.RecoveredNotifiedUtc != null)
            return;

        if (state.RecoveredDueUtc == null)
            state.RecoveredDueUtc = state.StateSinceUtc.AddSeconds(_opt.RecoveredAfterSeconds);

        if (nowUtc < state.RecoveredDueUtc.Value)
            return;

        var recoveredBodyText = await BuildRecoveredBodyAsync(db, instanceId, target, state, tz, _opt.PublicBaseUrl, ct);

        var notified = await NotifyAsync(
            db,
            "AlertRecovered",
            emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
            webhooksConfigured, webhookSender, webhookUrls,
            instanceId, target, state, nowUtc,
            subject: "Website Check SUCCESSFUL",
            bodyText: recoveredBodyText,
            ct: ct);

        if (!notified)
        {
            _logger.LogError("ALERT SEND FAILED: RECOVERED Instance={InstanceId} TargetId={TargetId} Url={Url}", instanceId, target.TargetId, target.Url);
            return;
        }

        state.RecoveredNotifiedUtc = nowUtc;

        db.Events.Add(new Event
        {
            InstanceId = instanceId,
            TargetId = target.TargetId,
            TimestampUtc = nowUtc,
            Type = "AlertRecovered",
            Message = $"RECOVERED {target.Url} (up since {state.StateSinceUtc:u})"
        });

        _logger.LogInformation("ALERT SENT: RECOVERED Instance={InstanceId} TargetId={TargetId} Url={Url}", instanceId, target.TargetId, target.Url);

        // Reset outage tracking so next outage starts clean
        state.DownFirstNotifiedUtc = null;
        state.LastNotifiedUtc = null;
        state.NextNotifyUtc = null;
        state.RecoveredDueUtc = null;
    }

    private async Task<bool> NotifyAsync(
        WebsiteMonitorDbContext db,
        string eventType,
        bool emailConfigured,
        ISmtpSender smtpSender,
        SmtpSettings? smtpSettings,
        string? smtpPasswordPlain,
        List<string> recipients,
        bool webhooksConfigured,
        IWebhookSender webhookSender,
        List<string> webhookUrls,
        string instanceId,
        Target target,
        TargetState state,
        DateTime nowUtc,
        string subject,
        string bodyText,
        CancellationToken ct)
    {
        var emailOk = false;
        var webhookOk = false;

        if (emailConfigured && smtpSettings != null)
        {
            emailOk = await TrySendAlertEmailAsync(
                smtpSender, smtpSettings, smtpPasswordPlain, recipients, subject, bodyText, ct);

            if (!emailOk)
            {
                db.Events.Add(new Event
                {
                    InstanceId = instanceId,
                    TargetId = target.TargetId,
                    TimestampUtc = nowUtc,
                    Type = "Error",
                    Message = $"Failed to send EMAIL for {eventType} {target.Url}"
                });
            }
        }

        if (webhooksConfigured)
        {
            var payload = new
            {
                eventType,
                instanceId,
                targetId = target.TargetId,
                url = target.Url,
                isUp = state.IsUp,
                stateSinceUtc = state.StateSinceUtc,
                timestampUtc = nowUtc,
                summary = state.LastSummary
            };

            webhookOk = await TrySendWebhooksAsync(webhookSender, webhookUrls, payload, ct);

            if (!webhookOk)
            {
                db.Events.Add(new Event
                {
                    InstanceId = instanceId,
                    TargetId = target.TargetId,
                    TimestampUtc = nowUtc,
                    Type = "Error",
                    Message = $"Failed to send WEBHOOK for {eventType} {target.Url}"
                });
            }
        }

        return emailOk || webhookOk;
    }

    private async Task<bool> TrySendAlertEmailAsync(
        ISmtpSender smtpSender,
        SmtpSettings smtpSettings,
        string? smtpPasswordPlain,
        List<string> recipients,
        string subject,
        string bodyText,
        CancellationToken ct)
    {
        var anyOk = false;

        foreach (var to in recipients)
        {
            try
            {
                await smtpSender.SendAsync(smtpSettings, smtpPasswordPlain, to, subject, bodyText, ct);
                anyOk = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMTP send failed To={To} Host={Host}:{Port} From={From}",
                    to, smtpSettings.Host, smtpSettings.Port, smtpSettings.FromAddress);
            }
        }

        return anyOk;
    }

    private async Task<bool> TrySendWebhooksAsync(
        IWebhookSender sender,
        List<string> urls,
        object payload,
        CancellationToken ct)
    {
        var anyOk = false;

        foreach (var url in urls)
        {
            try
            {
                await sender.PostAsync(url, payload, ct);
                anyOk = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook POST failed Url={Url}", url);
            }
        }

        return anyOk;
    }

        private static async Task<string> BuildDownBodyAsync(
        WebsiteMonitorDbContext db,
        string instanceId,
        Target target,
        TargetState state,
        DateTime downStartUtc,
        TimeZoneInfo tz,
    string? publicBaseUrl,
        CancellationToken ct)
    {
        var lastCheck = await db.Checks
            .Where(c => c.TargetId == target.TargetId)
            .OrderByDescending(c => c.TimestampUtc)
            .FirstOrDefaultAsync(ct);

        // Try to show when this target was last known UP (from the most recent recovered alert event)
        var upSinceUtc = await GetLastUtcFromEventAsync(
            db, instanceId, target.TargetId,
            types: new[] { "AlertRecovered" },
            marker: "up since ",
            ct);

        
        var ssPublicBaseUrl = await TryGetPublicBaseUrlFromSystemSettingsAsync(db, ct);
        var effectivePublicBaseUrl = !string.IsNullOrWhiteSpace(ssPublicBaseUrl) ? ssPublicBaseUrl : publicBaseUrl;

        return BuildAlertHtmlBody(
            title: "ITWebsiteMonitor detected a Failed check",
            instanceId: instanceId,
            isUp: false,
            publicBaseUrl: effectivePublicBaseUrl,
            target: target,
            state: state,
            lastCheck: lastCheck,
            downSinceUtc: downStartUtc,
            upSinceUtc: upSinceUtc,
            tz: tz);
}

    private static async Task<string> BuildRecoveredBodyAsync(
        WebsiteMonitorDbContext db,
        string instanceId,
        Target target,
        TargetState state,
        TimeZoneInfo tz,
    string? publicBaseUrl,
        CancellationToken ct)
    {
        var lastCheck = await db.Checks
            .Where(c => c.TargetId == target.TargetId)
            .OrderByDescending(c => c.TimestampUtc)
            .FirstOrDefaultAsync(ct);

        // Try to show when this outage started (from the most recent DOWN alert event)
        var downSinceUtc = await GetLastUtcFromEventAsync(
            db, instanceId, target.TargetId,
            types: new[] { "AlertDownFirst", "AlertDownRepeat" },
            marker: "down since ",
            ct);

        var ssPublicBaseUrl = await TryGetPublicBaseUrlFromSystemSettingsAsync(db, ct);
        var effectivePublicBaseUrl = !string.IsNullOrWhiteSpace(ssPublicBaseUrl) ? ssPublicBaseUrl : publicBaseUrl;

        return BuildAlertHtmlBody(
            title: "ITWebsiteMonitor detected a Successful check",
            instanceId: instanceId,
            isUp: true,
            publicBaseUrl: effectivePublicBaseUrl,
            target: target,
            state: state,
            lastCheck: lastCheck,
            downSinceUtc: downSinceUtc,
            upSinceUtc: state.StateSinceUtc,
            tz: tz);
}

    private static async Task<DateTime?> GetLastUtcFromEventAsync(
        WebsiteMonitorDbContext db,
        string instanceId,
        long targetId,
        string[] types,
        string marker,
        CancellationToken ct)
    {
        var msg = await db.Events
            .Where(e => e.InstanceId == instanceId && e.TargetId == targetId && types.Contains(e.Type))
            .OrderByDescending(e => e.TimestampUtc)
            .Select(e => e.Message)
            .FirstOrDefaultAsync(ct);

        return TryParseUtcAfterMarker(msg, marker);
    }

    private static DateTime? TryParseUtcAfterMarker(string? message, string marker)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var i = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        i += marker.Length;
        var end = message.IndexOf(')', i);
        if (end < 0) end = message.Length;

        var raw = message.Substring(i, end - i).Trim();

        // Event messages use DateTime.ToString("u"), which ends with 'Z'
        if (DateTime.TryParseExact(
                raw,
                "yyyy-MM-dd HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return dt;
        }

        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out dt))
        {
            return dt;
        }

        return null;
    }

        
    private static async Task<string?> TryGetPublicBaseUrlFromSystemSettingsAsync(WebsiteMonitorDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.SystemSettings.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.PublicBaseUrl)
                .FirstOrDefaultAsync(ct);
        }
        catch
        {
            return null;
        }
    }

private static string? NormalizePublicBaseUrl(string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            return null;

        return publicBaseUrl.Trim().TrimEnd('/');
    }

    private static string BuildAlertHtmlBody(
        string title,
        string instanceId,
        bool isUp,
        string? publicBaseUrl,
        Target target,
        TargetState state,
        Check? lastCheck,
        DateTime? downSinceUtc,
        DateTime? upSinceUtc,
        TimeZoneInfo tz)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

        static string Link(string url, string? displayText = null)
        {
            var t = displayText ?? url;
            return $"<a href=\"{H(url)}\" style=\"color:#6ab0ff; text-decoration:underline;\">{H(t)}</a>";
        }

        static void Row(StringBuilder sb, string label, string valueHtml)
        {
            sb.Append("<tr>");
            sb.Append("<td style=\"background:#4f86d9; color:#ffffff; font-weight:700; padding:10px 12px; border:1px solid #6f6f6f; width:180px; vertical-align:top;\">");
            sb.Append(H(label));
            sb.Append("</td>");
            sb.Append("<td style=\"background:transparent; padding:10px 12px; border:1px solid #6f6f6f; vertical-align:top;\">");
            sb.Append(valueHtml);
            sb.Append("</td>");
            sb.Append("</tr>");
        }

        var baseUrl = NormalizePublicBaseUrl(publicBaseUrl) ?? "http://localhost:5041";
        var homeUrl = baseUrl + "/";
        var logoUrl = baseUrl + "/images/itgreatfalls-logo.png";

        var instanceHomeUrl = homeUrl;
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            instanceHomeUrl = homeUrl + "?instanceId=" + Uri.EscapeDataString(instanceId);
        }

        var port = GetTcpPort(target.Url);
        var usedIp = lastCheck?.UsedIp ?? state.LastUsedIp ?? string.Empty;
        var finalUrl = lastCheck?.FinalUrl ?? state.LastFinalUrl ?? target.Url;
        var httpCode = lastCheck?.HttpStatusCode;
        var loginType = lastCheck?.DetectedLoginType ?? state.LastDetectedLoginType ?? string.Empty;

        var tcpOk = lastCheck?.TcpOk ?? state.LastSummary.Contains("TCP OK", StringComparison.OrdinalIgnoreCase);
        var httpOk = lastCheck?.HttpOk ?? state.LastSummary.Contains("HTTP OK", StringComparison.OrdinalIgnoreCase);
        var loginDetected = lastCheck?.LoginDetected ?? state.LoginDetectedLast;

        var tcpValue = tcpOk
            ? (string.IsNullOrWhiteSpace(usedIp) ? "OK" : $"OK ({usedIp})")
            : "FAILED";

        var httpValue = httpOk
            ? (httpCode.HasValue ? httpCode.Value.ToString(CultureInfo.InvariantCulture) : "OK")
            : (httpCode.HasValue ? $"FAIL ({httpCode.Value})" : "FAIL (no code)");

        var loginValue = loginDetected
            ? (string.IsNullOrWhiteSpace(loginType) ? "Detected" : $"Detected ({loginType})")
            : (string.IsNullOrWhiteSpace(loginType) ? "Not detected" : $"Not detected ({loginType})");

        var detailsText = $"TCP: {tcpValue}\nHTTP: {httpValue}\nLogin: {loginValue}";
        var detailsHtml = $"<div style=\"white-space:pre-line;\">{H(detailsText)}</div>";

        var result = isUp ? "SUCCESSFUL" : "FAILED";

        var tcpMs = lastCheck?.TcpLatencyMs;
        var httpMs = lastCheck?.HttpLatencyMs;
        int? totalMs = (tcpMs.HasValue || httpMs.HasValue) ? (tcpMs ?? 0) + (httpMs ?? 0) : null;

        var sb = new StringBuilder();

        // NOTE: Email client support varies. Use a table-based wrapper + bgcolor for reliable dark backgrounds
        // across Outlook/Word rendering and clients that ignore <body> background styles.
        sb.Append("<!doctype html><html><head>");
        sb.Append("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"> ");
        // Dark-mode hints (not honored by all clients)
        sb.Append("<meta name=\"color-scheme\" content=\"dark light\"> ");
        sb.Append("<meta name=\"supported-color-schemes\" content=\"dark light\"> ");
        sb.Append("</head><body style=\"margin:0; padding:0; background:transparent; font-family:Segoe UI, Arial, sans-serif; -webkit-text-size-adjust:100%; -ms-text-size-adjust:100%;\">");

        // Outer wrapper enforces background + padding
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:transparent;\">");
        sb.Append("<tr><td align=\"center\" style=\"padding:18px;\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"max-width:1200px;\">");

        // Header
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-bottom:14px;\"><tr>");
        if (logoUrl != null)
        {
            sb.Append("<td style=\"width:75px; padding-right:12px; vertical-align:middle;\">");
            sb.Append($"<a href=\"{H(instanceHomeUrl)}\" style=\"display:inline-block; text-decoration:none;\">");
            sb.Append($"<img src=\"{H(logoUrl)}\" width=\"75\" height=\"75\" style=\"display:block; border-radius:6px;\">");
            sb.Append("</a>");
            sb.Append("</td>");
        }

        sb.Append("<td style=\"vertical-align:middle;\">");
        sb.Append($"<a href=\"{H(instanceHomeUrl)}\" style=\"color:inherit; text-decoration:none;\">");
        sb.Append($"<div style=\"font-size:20px; font-weight:700; line-height:1.2;\">{H(title)}</div>");
        sb.Append("</a>");
        sb.Append("</td>");

        sb.Append("<td style=\"text-align:right; vertical-align:middle;\">");
        if (homeUrl != null)
        {
            sb.Append($"<a href=\"{H(instanceHomeUrl)}\" style=\"color:#6ab0ff; font-size:14px; text-decoration:underline;\">Open Home Page</a>");
        }
        sb.Append("</td>");

        sb.Append("</tr></table>");

        // Details table
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse; border:1px solid #6f6f6f;\">");

        // URL
        var urlValue = Uri.TryCreate(target.Url, UriKind.Absolute, out _)
            ? Link(target.Url)
            : H(target.Url);
        Row(sb, "URL", urlValue);

        // TCP
        Row(sb, $"TCP {port}", H(tcpValue));

        // Details
        Row(sb, "Details", detailsHtml);

        // Resolved IPs
        Row(sb, "Resolved IPs", string.IsNullOrWhiteSpace(usedIp) ? "-" : H(usedIp));

        // Result
        Row(sb, "Result", H(result));

        // HTTP
        Row(sb, "HTTP", H(httpValue));

        // Final URL
        var finalUrlValue = Uri.TryCreate(finalUrl, UriKind.Absolute, out _)
            ? Link(finalUrl)
            : H(finalUrl);
        Row(sb, "Final URL", finalUrlValue);

        // ms
        if (totalMs.HasValue)
            Row(sb, "ms", H(totalMs.Value.ToString(CultureInfo.InvariantCulture)));

        // Down Since / Up Since
        if (downSinceUtc.HasValue)
            Row(sb, "Down Since", H(FormatLocal(downSinceUtc.Value, tz)));

        if (!string.IsNullOrWhiteSpace(loginType))
            Row(sb, "Check", H(loginType));

        if (upSinceUtc.HasValue)
            Row(sb, "Up Since", H(FormatLocal(upSinceUtc.Value, tz)));

        sb.Append("</table>");

        // Close wrapper tables
        sb.Append("</table>");
        sb.Append("</td></tr></table>");
        sb.Append("</body></html>");

        return sb.ToString();
    }

private static int GetTcpPort(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (!uri.IsDefaultPort) return uri.Port;
            return uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        }

        return 443;
    }

    private static string FormatLocal(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.ToString("ddd MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private DateTime ComputeNextDownNotifyUtc(DateTime downStartUtc, DateTime lastSentUtc, TimeZoneInfo tz, DateTime nowUtc)
    {
        var age = nowUtc - downStartUtc;

        if (age < TimeSpan.FromHours(24))
            return lastSentUtc.AddSeconds(_opt.RepeatEverySeconds_Under24h);

        if (age < TimeSpan.FromHours(_opt.DailyAfterHours))
            return lastSentUtc.AddSeconds(_opt.RepeatEverySeconds_24hTo72h);

        // After 72h: once per day at 10:00 local
        var localNow = _tz.ToLocal(nowUtc, tz);

        var candidateLocal = new DateTime(
            localNow.Year, localNow.Month, localNow.Day,
            _opt.DailyHourLocal, _opt.DailyMinuteLocal, 0);

        var candidateUtc = _tz.ToUtc(candidateLocal, tz);

        if (candidateUtc <= nowUtc)
        {
            candidateLocal = candidateLocal.AddDays(1);
            candidateUtc = _tz.ToUtc(candidateLocal, tz);
        }

        return candidateUtc;
    }

private async Task SaveChangesWithGateRetryAsync(WebsiteMonitorDbContext db, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await SqliteWriteGate.Gate.WaitAsync(ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && attempt <= 10)
            {
                _logger.LogWarning(ex,
                    "SQLite busy/locked in AlertEvaluator; attempt {Attempt}/10 Err={Err} ExtErr={ExtErr}.",
                    attempt, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
            }
            catch (SqliteException ex)
            {
                _logger.LogError(ex,
                    "SQLite failure in AlertEvaluator SaveChanges Err={Err} ExtErr={ExtErr} Message={Message}",
                    ex.SqliteErrorCode, ex.SqliteExtendedErrorCode, ex.Message);
                throw;
            }
            finally
            {
                SqliteWriteGate.Gate.Release();
            }

            var delayMs = Math.Min(5000, 100 * attempt * attempt);
            await Task.Delay(delayMs, ct);
        }
    }
}
