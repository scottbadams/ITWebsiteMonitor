using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebsiteMonitor.Notifications.Smtp;
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

        // IMPORTANT: DbContext must be scoped per call (DbContext is not thread-safe)
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<WebsiteMonitorDbContext>();
        var smtpSender = sp.GetRequiredService<ISmtpSender>();
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

        // Load recipients + SMTP once per instance evaluation
        var recipientEmails = await db.Recipients
            .Where(r => r.InstanceId == instanceId && r.Enabled)
            .Select(r => r.Email)
            .ToListAsync(ct);

        var smtpSettings = await db.SmtpSettings
            .SingleOrDefaultAsync(s => s.InstanceId == instanceId, ct);

        // If not configured, we can’t send automatic alerts
        if (smtpSettings == null ||
            string.IsNullOrWhiteSpace(smtpSettings.Host) ||
            smtpSettings.Port <= 0 ||
            string.IsNullOrWhiteSpace(smtpSettings.FromAddress) ||
            recipientEmails.Count == 0)
        {
            return;
        }

        string? smtpPasswordPlain = TryUnprotectSmtpPassword(dp, smtpSettings, instanceId);

        foreach (var s in states)
        {
            var target = targets.FirstOrDefault(t => t.TargetId == s.TargetId);
            if (target == null) continue;

            if (!s.IsUp)
            {
                await HandleDownAsync(db, smtpSender, tz, recipientEmails, instanceId, target, s, smtpSettings, smtpPasswordPlain, nowUtc, ct);
            }
            else
            {
                await HandleUpAsync(db, smtpSender, recipientEmails, instanceId, target, s, smtpSettings, smtpPasswordPlain, nowUtc, ct);
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
        ISmtpSender smtpSender,
        TimeZoneInfo tz,
        List<string> recipientEmails,
        string instanceId,
        Target target,
        TargetState state,
        SmtpSettings smtpSettings,
        string? smtpPasswordPlain,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var downStartUtc = state.StateSinceUtc;
        var downAge = nowUtc - downStartUtc;

        // First DOWN alert due after N seconds continuous failure
        if (state.DownFirstNotifiedUtc == null &&
            downAge >= TimeSpan.FromSeconds(_opt.DownAfterSeconds))
        {
            var sent = await TrySendAlertEmailAsync(
                smtpSender,
                smtpSettings,
                smtpPasswordPlain,
                recipientEmails,
                subject: $"DOWN {target.Url}",
                bodyText: BuildDownBody(instanceId, target, state, downStartUtc, nowUtc, tz),
                ct: ct);

            if (!sent)
            {
                db.Events.Add(new Event
                {
                    InstanceId = instanceId,
                    TargetId = target.TargetId,
                    TimestampUtc = nowUtc,
                    Type = "Error",
                    Message = $"Failed to send DOWN email for {target.Url}"
                });
                _logger.LogError("ALERT SEND FAILED: DOWN Instance={InstanceId} TargetId={TargetId} Url={Url}", instanceId, target.TargetId, target.Url);
                return;
            }

            state.DownFirstNotifiedUtc = nowUtc;
            state.LastNotifiedUtc = nowUtc;

            // Compute next repeat schedule (based on successful send time)
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
            var sent = await TrySendAlertEmailAsync(
                smtpSender,
                smtpSettings,
                smtpPasswordPlain,
                recipientEmails,
                subject: $"DOWN (repeat) {target.Url}",
                bodyText: BuildDownRepeatBody(instanceId, target, state, downStartUtc, nowUtc, tz),
                ct: ct);

            if (!sent)
            {
                db.Events.Add(new Event
                {
                    InstanceId = instanceId,
                    TargetId = target.TargetId,
                    TimestampUtc = nowUtc,
                    Type = "Error",
                    Message = $"Failed to send DOWN repeat email for {target.Url}"
                });
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
        ISmtpSender smtpSender,
        List<string> recipientEmails,
        string instanceId,
        Target target,
        TargetState state,
        SmtpSettings smtpSettings,
        string? smtpPasswordPlain,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Your rule: recovered only if a DOWN alert was actually sent for this outage
        if (state.DownFirstNotifiedUtc == null)
        {
            state.RecoveredDueUtc = null;
            state.RecoveredNotifiedUtc = null;
            return;
        }

        if (state.RecoveredNotifiedUtc != null)
            return;

        // Recovered alert due after N seconds continuous UP
        if (state.RecoveredDueUtc == null)
            state.RecoveredDueUtc = state.StateSinceUtc.AddSeconds(_opt.RecoveredAfterSeconds);

        if (nowUtc < state.RecoveredDueUtc.Value)
            return;

        var sent = await TrySendAlertEmailAsync(
            smtpSender,
            smtpSettings,
            smtpPasswordPlain,
            recipientEmails,
            subject: $"RECOVERED {target.Url}",
            bodyText: BuildRecoveredBody(instanceId, target, state, nowUtc),
            ct: ct);

        if (!sent)
        {
            db.Events.Add(new Event
            {
                InstanceId = instanceId,
                TargetId = target.TargetId,
                TimestampUtc = nowUtc,
                Type = "Error",
                Message = $"Failed to send RECOVERED email for {target.Url}"
            });
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

    private async Task<bool> TrySendAlertEmailAsync(
        ISmtpSender smtpSender,
        SmtpSettings smtpSettings,
        string? smtpPasswordPlain,
        List<string> recipients,
        string subject,
        string bodyText,
        CancellationToken ct)
    {
        // Minimal behavior: send one email per recipient; return false if any fail.
        foreach (var to in recipients)
        {
            try
            {
                await smtpSender.SendAsync(smtpSettings, smtpPasswordPlain, to, subject, bodyText, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMTP send failed To={To} Host={Host}:{Port} From={From}",
                    to, smtpSettings.Host, smtpSettings.Port, smtpSettings.FromAddress);
                return false;
            }
        }

        return true;
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

    private static string BuildDownRepeatBody(string instanceId, Target target, TargetState state, DateTime downStartUtc, DateTime nowUtc, TimeZoneInfo tz)
        => BuildDownBody(instanceId, target, state, downStartUtc, nowUtc, tz);

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

        // Next occurrence strictly after now
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
                // 5=SQLITE_BUSY, 6=SQLITE_LOCKED
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
