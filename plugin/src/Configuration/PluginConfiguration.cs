using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Mediathek.Configuration;

/// <summary>Einstellungen des Plugins.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Zuletzt gesuchte Begriffe; jeder erscheint als Ordner unter „Suche“. Die Suche trägt sie selbst ein.</summary>
    public string[] Searches { get; set; } = Array.Empty<string>();

    /// <summary>Im Live-TV auch ORF und arte (französisch) anbieten.</summary>
    public bool IncludeForeign { get; set; } = true;

    /// <summary>
    /// Live-Sender und Videos durch den eigenen Server leiten. Dann zählt für Ländersperren der Standort des Servers
    /// (z. B. hinter einem VPN), nicht der des Geräts, auf dem geschaut wird.
    /// </summary>
    public bool ViaServer { get; set; } = true;

    /// <summary>Schlüssel für die Prüfsummen weitergereichter Adressen; entsteht beim ersten Start von selbst.</summary>
    public string ProxyKey { get; set; } = string.Empty;

    /// <summary>Lokalfenster (z. B. WDR Lokalzeit) im Live-TV mit anbieten.</summary>
    public bool IncludeRegionalWindows { get; set; } = false;
}
