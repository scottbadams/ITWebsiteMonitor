using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
            var notified = await NotifyAsync(
                db,
                "AlertDown",
                emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
                webhooksConfigured, webhookSender, webhookUrls,
                instanceId, target, state, nowUtc,
                subject: $"DOWN {target.Url}",
                bodyText: BuildDownBody(instanceId, target, state, downStartUtc, nowUtc, tz),
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
            var notified = await NotifyAsync(
                db,
                "AlertDownRepeat",
                emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
                webhooksConfigured, webhookSender, webhookUrls,
                instanceId, target, state, nowUtc,
                subject: $"DOWN (repeat) {target.Url}",
                bodyText: BuildDownBody(instanceId, target, state, downStartUtc, nowUtc, tz),
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

        var notified = await NotifyAsync(
            db,
            "AlertRecovered",
            emailConfigured, smtpSender, smtpSettings, smtpPasswordPlain, recipientEmails,
            webhooksConfigured, webhookSender, webhookUrls,
            instanceId, target, state, nowUtc,
            subject: $"RECOVERED {target.Url}",
            bodyText: BuildRecoveredBody(instanceId, target, state, nowUtc),
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

    private static string BuildDownBody(string instanceId, Target target, TargetState state, DateTime downStartUtc, DateTime nowUtc, TimeZoneInfo tz)
    {
        var downFor = nowUtc - downStartUtc;
        var downStartLocal = TimeZoneInfo.ConvertTimeFromUtc(downStartUtc, tz);

        return
$@"Instance: {instanceId}
URL: {target.Url}

Down since (UTC): {downStartUtc:u}
Down since (local): {downStartLocal:yyyy-MM-dd HH:mm:ss}
Down duration: {downFor}

Consecutive failures: {state.ConsecutiveFailures}
Last summary: {state.LastSummary}";
    }

    private static string BuildRecoveredBody(string instanceId, Target target, TargetState state, DateTime nowUtc)
    {
        var upSinceUtc = state.StateSinceUtc;
        var upFor = nowUtc - upSinceUtc;

        return
$@"Instance: {instanceId}
URL: {target.Url}

Up since (UTC): {upSinceUtc:u}
Up duration: {upFor}

Last summary: {state.LastSummary}";
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
