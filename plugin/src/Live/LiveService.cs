using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Mediathek.Mvw;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Live;

/// <summary>Ein Live-Programm samt Adresse seines Streams.</summary>
public sealed record LiveChannel(string Id, string Name, string Group, string Url, string? GuideId, int Number);

/// <summary>
/// Stellt die Live-Programme als M3U-Liste und ihr Programm als XMLTV bereit. Die Stream-Adressen stammen aus der Filmliste
/// von MediathekView (Sendung „Livestream“), das Programm aus der Programmübersicht der ARD.
/// </summary>
public sealed class LiveService
{
    // Sender und Titelteil in der Filmliste → Kennung, Anzeigename, Gruppe, Kennung im ARD-Programm.
    private static readonly (string Channel, string TitlePart, string Id, string Name, string Group, string? Guide)[] Known =
    {
        ("ARD", "ARD Livestream", "daserste", "Das Erste", "Vollprogramme", "daserste"),
        ("ZDF", "ZDF Livestream", "zdf", "ZDF", "Vollprogramme", null),
        ("3Sat", "3Sat", "3sat", "3sat", "Vollprogramme", "3sat"),
        ("ARTE.DE", "ARTE.DE", "arte", "arte", "Vollprogramme", "arte"),
        ("KiKA", "KiKA", "kika", "KiKA", "Vollprogramme", "kika"),
        ("PHOENIX", "PHOENIX", "phoenix", "phoenix", "Vollprogramme", "phoenix"),
        ("ZDF", "ZDF.neo", "zdfneo", "ZDFneo", "Vollprogramme", null),
        ("ZDF", "ZDF.info", "zdfinfo", "ZDFinfo", "Vollprogramme", null),
        ("ARD", "ARD ONE", "one", "ONE", "Vollprogramme", "one"),
        ("ARD", "Tagesschau", "tagesschau24", "tagesschau24", "Vollprogramme", "tagesschau24"),
        ("ARD", "ARD Alpha", "alpha", "ARD alpha", "Vollprogramme", "alpha"),
        ("BR", "BR Süd", "br-sued", "BR Fernsehen Süd", "Dritte", "br"),
        ("BR", "BR Nord", "br-nord", "BR Fernsehen Nord", "Dritte", "br"),
        ("HR", "HR Livestream", "hr", "hr-fernsehen", "Dritte", "hr"),
        ("MDR", "MDR Sachsen-Anhalt", "mdr-sachsen-anhalt", "MDR Sachsen-Anhalt", "Dritte", "mdr"),
        ("MDR", "MDR Sachsen", "mdr-sachsen", "MDR Sachsen", "Dritte", "mdr"),
        ("MDR", "MDR Thüringen", "mdr-thueringen", "MDR Thüringen", "Dritte", "mdr"),
        ("NDR", "NDR Niedersachsen", "ndr-niedersachsen", "NDR Niedersachsen", "Dritte", "ndr"),
        ("NDR", "NDR Hamburg", "ndr-hamburg", "NDR Hamburg", "Dritte", "ndr"),
        ("NDR", "NDR Schleswig-Holstein", "ndr-schleswig-holstein", "NDR Schleswig-Holstein", "Dritte", "ndr"),
        ("NDR", "NDR Mecklenburg-Vorpommern", "ndr-mecklenburg-vorpommern", "NDR Mecklenburg-Vorpommern", "Dritte", "ndr"),
        ("RBB", "RBB Berlin", "rbb-berlin", "rbb Berlin", "Dritte", "rbb"),
        ("RBB", "RBB Brandenburg", "rbb-brandenburg", "rbb Brandenburg", "Dritte", "rbb"),
        ("RBTV", "Radio Bremen", "radiobremen", "Radio Bremen TV", "Dritte", "radiobremen"),
        ("SR", "SR Livestream", "sr", "SR Fernsehen", "Dritte", "sr"),
        ("SWR", "SWR BW", "swr-bw", "SWR Baden-Württemberg", "Dritte", "swr"),
        ("SWR", "SWR RP", "swr-rp", "SWR Rheinland-Pfalz", "Dritte", "swr"),
        ("WDR", "WDR Livestream", "wdr", "WDR Fernsehen", "Dritte", "wdr"),
        ("DW", "DW Livestream", "dw", "Deutsche Welle", "Weitere", null),
        ("ARTE.FR", "ARTE.FR", "arte-fr", "arte (französisch)", "Ausland", null),
        ("ORF", "ORF-1", "orf1", "ORF 1", "Ausland", null),
        ("ORF", "ORF-2", "orf2", "ORF 2", "Ausland", null),
        ("ORF", "ORF-3", "orf3", "ORF III", "Ausland", null),
        ("ORF", "ORF-Sport", "orf-sport", "ORF Sport +", "Ausland", null),
    };

    private static readonly TimeSpan ChannelCacheTime = TimeSpan.FromHours(6);
    private static readonly TimeSpan GuideCacheTime = TimeSpan.FromHours(2);

    private readonly MvwClient _mvw;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<LiveService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private (DateTime At, IReadOnlyList<LiveChannel> Channels, string Key)? _channels;
    private (DateTime At, string Xml, string Key)? _guide;

    public LiveService(MvwClient mvw, IHttpClientFactory http, ILogger<LiveService> logger)
    {
        _mvw = mvw;
        _http = http;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    public async Task<IReadOnlyList<LiveChannel>> GetChannelsAsync(CancellationToken ct)
    {
        var key = Config.IncludeForeign + "/" + Config.IncludeRegionalWindows;
        if (_channels is { } hit && hit.Key == key && DateTime.UtcNow - hit.At < ChannelCacheTime)
        {
            return hit.Channels;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channels is { } again && again.Key == key && DateTime.UtcNow - again.At < ChannelCacheTime)
            {
                return again.Channels;
            }

            IReadOnlyList<MvwEntry> found;
            try
            {
                (found, _) = await _mvw.QueryAsync(null, "Livestream", null, 0, 200, true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _channels is not null)
            {
                // Lieber die letzte bekannte Liste als gar kein Fernsehen.
                _logger.LogWarning(ex, "Mediathek: Livestreams ließen sich nicht abfragen, die bisherige Liste bleibt");
                return _channels.Value.Channels;
            }

            var streams = found.Where(e => e.UrlVideo.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)).ToList();
            var result = new List<LiveChannel>();
            var used = new HashSet<string>();
            var number = 1;
            foreach (var k in Known)
            {
                // Längster passender Titelteil zuerst abgearbeitet (Reihenfolge der Tabelle): „MDR Sachsen-Anhalt“ vor „MDR Sachsen“.
                var entry = streams.FirstOrDefault(e => !used.Contains(e.Id)
                    && string.Equals(e.Channel, k.Channel, StringComparison.OrdinalIgnoreCase)
                    && e.Title.Contains(k.TitlePart, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    continue;
                }

                used.Add(entry.Id);
                if (k.Group == "Ausland" && !Config.IncludeForeign)
                {
                    continue;
                }

                result.Add(new LiveChannel(k.Id, k.Name, k.Group, Https(entry.UrlVideo), k.Guide, number++));
            }

            // Was die Filmliste sonst noch an Livestreams kennt, hängt hinten an.
            foreach (var e in streams.Where(e => !used.Contains(e.Id)))
            {
                var regional = e.Title.Contains("Lokalzeit", StringComparison.OrdinalIgnoreCase);
                if (regional && !Config.IncludeRegionalWindows)
                {
                    continue;
                }

                var name = e.Title.Replace("Livestream", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("(Deutschland)", string.Empty, StringComparison.Ordinal).Trim();
                var id = new string(name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
                if (id.Length == 0 || result.Any(c => c.Id == id))
                {
                    continue;
                }

                result.Add(new LiveChannel(id, name, regional ? "Lokalfenster" : "Weitere", Https(e.UrlVideo), null, number++));
            }

            _channels = (DateTime.UtcNow, result, key);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Die Filmliste führt einige Streams mit http; die Server liefern alle auch über https.
    private static string Https(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    public async Task<string> GetPlaylistAsync(CancellationToken ct)
    {
        var channels = await GetChannelsAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var c in channels)
        {
            sb.Append("#EXTINF:-1 tvg-id=\"").Append(c.Id).Append("\" tvg-chno=\"").Append(c.Number.ToString(CultureInfo.InvariantCulture))
              .Append("\" tvg-name=\"").Append(c.Name.Replace('"', '\'')).Append("\" group-title=\"").Append(c.Group).Append("\",")
              .Append(c.Name).Append('\n').Append(c.Url).Append('\n');
        }

        return sb.ToString();
    }

    public async Task<string> GetGuideAsync(CancellationToken ct)
    {
        var channels = await GetChannelsAsync(ct).ConfigureAwait(false);
        var key = string.Join(',', channels.Select(c => c.Id));
        if (_guide is { } hit && hit.Key == key && DateTime.UtcNow - hit.At < GuideCacheTime)
        {
            return hit.Xml;
        }

        // Kennung im ARD-Programm → Sendungen (Beginn, Ende, Titel, Untertitel, Beschreibung, Bild).
        var programmes = new Dictionary<string, SortedDictionary<DateTimeOffset, (DateTimeOffset End, string Title, string Sub, string Text, string Image)>>();
        var client = _http.CreateClient(ServiceRegistrator.HttpClientName);
        var today = DateTime.Now.Date;
        var loaded = 0;
        for (var day = -1; day <= 3; day++)
        {
            try
            {
                var url = "https://programm-api.ard.de/program/api/program?day=" + today.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                await using var stream = await client.GetStreamAsync(url, ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("channels", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                loaded++;
                foreach (var channel in list.EnumerateArray())
                {
                    var id = Text(channel, "id");
                    if (id.Length == 0 || !channel.TryGetProperty("timeSlots", out var slots) || slots.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    if (!programmes.TryGetValue(id, out var map))
                    {
                        programmes[id] = map = new();
                    }

                    foreach (var slot in slots.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Array))
                    {
                        foreach (var e in slot.EnumerateArray())
                        {
                            if (!DateTimeOffset.TryParse(Text(e, "broadcastedOn"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                                || !DateTimeOffset.TryParse(Text(e, "broadcastEnd"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                                || end <= start)
                            {
                                continue;
                            }

                            var image = string.Empty;
                            if (e.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object
                                && images.TryGetProperty("aspect16x9", out var wide) && wide.ValueKind == JsonValueKind.Object)
                            {
                                // Nur Adressen, hinter denen wirklich ein Bild liegt: der Bilderdienst der Mediathek liefert für
                                // einzelne Bilder eine Beschreibung statt des Bilds, für Bildsammlungen das Bild selbst.
                                var src = Text(wide, "src");
                                if (!src.Contains("/image-service/images/", StringComparison.Ordinal))
                                {
                                    image = src.Replace("{width}", "640", StringComparison.Ordinal);
                                }
                            }

                            var title = Text(e, "coreTitle");
                            map[start] = (end, title.Length > 0 ? title : Text(e, "title"), Text(e, "subline"), Text(e, "synopsis"), image);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Mediathek: Programm für Tag {Day} ließ sich nicht laden", day);
            }
        }

        if (loaded == 0 && _guide is not null)
        {
            return _guide.Value.Xml;
        }

        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv generator-info-name=\"Jellyfin Mediathek\">\n");
        foreach (var c in channels)
        {
            sb.Append("  <channel id=\"").Append(Esc(c.Id)).Append("\"><display-name lang=\"de\">").Append(Esc(c.Name)).Append("</display-name></channel>\n");
        }

        foreach (var c in channels.Where(c => c.GuideId is not null && programmes.ContainsKey(c.GuideId)))
        {
            foreach (var (start, p) in programmes[c.GuideId!])
            {
                sb.Append("  <programme start=\"").Append(Stamp(start)).Append("\" stop=\"").Append(Stamp(p.End)).Append("\" channel=\"").Append(Esc(c.Id)).Append("\">")
                  .Append("<title lang=\"de\">").Append(Esc(p.Title)).Append("</title>");
                if (p.Sub.Length > 0 && p.Sub != p.Title)
                {
                    sb.Append("<sub-title lang=\"de\">").Append(Esc(p.Sub)).Append("</sub-title>");
                }

                if (p.Text.Length > 0)
                {
                    sb.Append("<desc lang=\"de\">").Append(Esc(p.Text)).Append("</desc>");
                }

                if (MvwClient.IsWebUrl(p.Image))
                {
                    sb.Append("<icon src=\"").Append(Esc(p.Image)).Append("\"/>");
                }

                sb.Append("</programme>\n");
            }
        }

        sb.Append("</tv>\n");
        var xml = sb.ToString();
        _guide = (DateTime.UtcNow, xml, key);
        return xml;
    }

    private static string Stamp(DateTimeOffset t) =>
        t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " " + (t.Offset < TimeSpan.Zero ? "-" : "+") + t.Offset.ToString("hhmm", CultureInfo.InvariantCulture);

    private static string Esc(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;
}
