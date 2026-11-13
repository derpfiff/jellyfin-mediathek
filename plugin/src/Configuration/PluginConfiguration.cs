using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Mediathek.Configuration;

/// <summary>Einstellungen des Plugins.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gemerkte Suchbegriffe; jeder erscheint als Ordner unter „Suche“.</summary>
    public string[] Searches { get; set; } = Array.Empty<string>();

    /// <summary>Fassungen mit Audiodeskription, Gebärdensprache oder Hörfassung ausblenden.</summary>
    public bool HideAccessibilityVersions { get; set; } = true;

    /// <summary>Trailer und Vorschauen ausblenden.</summary>
    public bool HideTrailers { get; set; } = true;

    /// <summary>Kürzere Beiträge in „Neu“ ausblenden (Minuten, 0: alles zeigen).</summary>
    public int MinimumMinutesInNew { get; set; } = 10;

    /// <summary>Österreichische und Schweizer Sender mit anzeigen.</summary>
    public bool IncludeForeign { get; set; } = true;

    /// <summary>Lokalfenster (z. B. WDR Lokalzeit) im Live-TV mit anbieten.</summary>
    public bool IncludeRegionalWindows { get; set; } = false;
}
