using System.Text.RegularExpressions;

namespace WebsiteMonitor.Monitoring.Checks;

internal static class CheckHeuristics
{
    // Values shown in the Monitor "Check" column
    public const string Generic = "Generic";
    public const string LoginOther = "Login (Other)";
    public const string RocketChat = "Rocket.Chat";
    public const string Nextcloud = "Nextcloud";
    public const string PMG = "Proxmox Mail Gateway";
    public const string PBS = "Proxmox Backup Server";
    public const string PVE = "Proxmox VE";
    public const string Zabbix = "Zabbix";
    public const string OPNsense = "OPNsense";
    public const string CipherMail = "CipherMail";

    private static readonly StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    // Keep regexes simple + compiled: this runs a lot.
    private static readonly Regex RxPasswordInput =
        new(@"<input[^>]+type\s*=\s*[""']password[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxNextcloudLoginMarker =
        new(@"id\s*=\s*[""']body-login", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string checkType, bool loginDetected) Detect(
        string? finalUrl,
        string? contentType,
        string? html)
    {
        var url = finalUrl ?? "";
        var body = html ?? "";
        var ct = contentType ?? "";

        if (string.IsNullOrWhiteSpace(body))
            return (Generic, false);

        // Accept HTML even if Content-Type is missing/misconfigured, as long as it looks like HTML.
        var looksHtml =
            body.IndexOf("<!doctype", Cmp) >= 0 ||
            body.IndexOf("<html", Cmp) >= 0 ||
            body.IndexOf("<head", Cmp) >= 0;

        var isHtml = ct.IndexOf("text/html", Cmp) >= 0 || looksHtml;
        if (!isHtml)
            return (Generic, false);

        var hasPassword = RxPasswordInput.IsMatch(body);

        // --- Proxmox family: prefer URL hints + well-known UI strings/assets ---
        // PVE web UI commonly references /pve2/ (and typically runs on 8006). :contentReference[oaicite:0]{index=0}
        if (ContainsAny(url, "/pve2", ":8006") ||
            ContainsAny(body, "Proxmox Virtual Environment", "pvemanagerlib.js", "/pve2/"))
            return (PVE, true);

        // PMG UI commonly lives under /pmg/ (and typically runs on 8006). 
        if (ContainsAny(url, "/pmg", ":8006") ||
            ContainsAny(body, "Proxmox Mail Gateway", "pmgmanagerlib", "/pmg/"))
            return (PMG, true);

        // PBS UI commonly lives under /pbs/ (and typically runs on 8007). :contentReference[oaicite:2]{index=2}
        if (ContainsAny(url, "/pbs", ":8007") ||
            ContainsAny(body, "Proxmox Backup Server", "pbsmanagerlib", "/pbs/"))
            return (PBS, true);

        // --- Nextcloud ---
        // Nextcloud login pages commonly include "Nextcloud" + a login-body marker and/or a password input.
        if (ContainsAny(body, "Nextcloud") && (hasPassword || RxNextcloudLoginMarker.IsMatch(body)))
            return (Nextcloud, true);

        // --- Rocket.Chat ---
        // Rocket.Chat pages nearly always contain the product name; treat as a login-bearing app.
        if (ContainsAny(body, "Rocket.Chat", "rocket.chat", "rocket-chat"))
            return (RocketChat, true);

        // --- Zabbix ---
        // Zabbix frontend: look for the product name and a password input.
        if (ContainsAny(body, "Zabbix", "zbx") && hasPassword)
            return (Zabbix, true);

        // --- OPNsense ---
        // OPNsense login pages often include the product name and a password input.
        if (ContainsAny(body, "OPNsense") && hasPassword)
            return (OPNsense, true);

        // --- CipherMail / Djigzo ---
        if ((ContainsAny(body, "CipherMail", "Djigzo") || ContainsAny(url, "djigzo")) && hasPassword)
            return (CipherMail, true);

        // --- Generic login page fallback ---
        if (hasPassword)
            return (LoginOther, true);

        return (Generic, false);
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        foreach (var n in needles)
        {
            if (!string.IsNullOrEmpty(n) && haystack.IndexOf(n, Cmp) >= 0)
                return true;
        }
        return false;
    }
}
