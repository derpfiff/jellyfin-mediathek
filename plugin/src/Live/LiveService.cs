using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Mediathek.Mvw;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Live;

/// <summary>Ein Live-Programm samt Adresse seines Streams.</summary>
public sealed record LiveChannel(string Id, string Name, string Group, string Url, string? GuideId, int Number);

/// <summary>
/// Stellt die Live-Programme als M3U-Liste und ihr Programm als XMLTV bereit. Die Stream-Adressen stammen aus der Filmliste
/// von MediathekView (Sendung „Livestream“), das Programm aus der Programmübersicht der ARD.
/// </summary>
public sealed partial class LiveService
{
    // Sender und Titelteil in der Filmliste → Kennung, Anzeigename, Gruppe, Kennung im ARD-Programm (mit „zdf:“ davor: im Programm des ZDF).
    private static readonly (string Channel, string TitlePart, string Id, string Name, string Group, string? Guide)[] Known =
    {
        ("ARD", "ARD Livestream", "daserste", "Das Erste", "Vollprogramme", "daserste"),
        ("ZDF", "ZDF Livestream", "zdf", "ZDF", "Vollprogramme", "zdf:ZDF"),
        ("3Sat", "3Sat", "3sat", "3sat", "Vollprogramme", "3sat"),
        ("ARTE.DE", "ARTE.DE", "arte", "arte", "Vollprogramme", "arte"),
        ("KiKA", "KiKA", "kika", "KiKA", "Vollprogramme", "kika"),
        ("PHOENIX", "PHOENIX", "phoenix", "phoenix", "Vollprogramme", "phoenix"),
        ("ZDF", "ZDF.neo", "zdfneo", "ZDFneo", "Vollprogramme", "zdf:ZDFneo"),
        ("ZDF", "ZDF.info", "zdfinfo", "ZDFinfo", "Vollprogramme", "zdf:ZDFinfo"),
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

    // Sender mit mehreren Regionalfassungen: Kennung im ARD-Programm → bevorzugte Fassung und Name ohne Region.
    private static readonly Dictionary<string, (string Preferred, string Name)> Families = new()
    {
        ["br"] = ("br-sued", "BR Fernsehen"),
        ["mdr"] = ("mdr-sachsen", "MDR Fernsehen"),
        ["ndr"] = ("ndr-niedersachsen", "NDR Fernsehen"),
        ["rbb"] = ("rbb-berlin", "rbb Fernsehen"),
        ["swr"] = ("swr-bw", "SWR Fernsehen"),
    };

    private static readonly TimeSpan ChannelCacheTime = TimeSpan.FromHours(6);
    private static readonly TimeSpan GuideCacheTime = TimeSpan.FromHours(2);

    private readonly MvwClient _mvw;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<LiveService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private (DateTime At, IReadOnlyList<LiveChannel> Channels, string Key)? _channels;
    private (DateTime At, string Xml, string Key)? _guide;
    private List<string> _zdfTokens = new();
    private IReadOnlyList<LiveChannel> _left = Array.Empty<LiveChannel>();

    public LiveService(MvwClient mvw, IHttpClientFactory http, ILogger<LiveService> logger)
    {
        _mvw = mvw;
        _http = http;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    public async Task<IReadOnlyList<LiveChannel>> GetChannelsAsync(CancellationToken ct)
    {
        var key = Config.IncludeForeign + "/" + Config.IncludeRegionalWindows + "/" + Config.IncludeRegionalVariants;
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

            // Dritte Programme senden je Land eine eigene Fassung, die sich nur in den Regionalnachrichten unterscheidet. Ohne
            // ausdrücklichen Wunsch bleibt je Sender eine Fassung, unter dem Namen des Senders.
            if (!Config.IncludeRegionalVariants)
            {
                foreach (var family in result.Where(c => c.GuideId is not null && Families.ContainsKey(c.GuideId)).GroupBy(c => c.GuideId!).Where(g => g.Count() > 1).ToList())
                {
                    var (preferred, name) = Families[family.Key];
                    var keep = family.FirstOrDefault(c => c.Id == preferred) ?? family.First();
                    result.RemoveAll(c => c.GuideId == family.Key && c.Id != keep.Id);
                    result[result.FindIndex(c => c.Id == keep.Id)] = keep with { Name = name };
                }

                result = result.Select((c, i) => c with { Number = i + 1 }).ToList();
                number = result.Count + 1;
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

            // Sender, die dieser Server sicher nicht empfängt, bleiben aus der Liste, statt beim Einschalten mit einem Fehler
            // zu enden. „Sicher“ heißt: der Sender lehnt den Abruf zweimal hintereinander ausdrücklich ab (Ländersperre).
            // Zeitüberschreitungen und Serverfehler können vorübergehend sein; solche Sender bleiben stehen.
            using (var gate = new SemaphoreSlim(6))
            {
                var blocked = await Task.WhenAll(result.Select(async c =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (!IsBlocked(await ProbeAsync(c.Url, ct).ConfigureAwait(false)))
                        {
                            return false;
                        }

                        await Task.Delay(1500, ct).ConfigureAwait(false);
                        return IsBlocked(await ProbeAsync(c.Url, ct).ConfigureAwait(false));
                    }
                    finally
                    {
                        gate.Release();
                    }
                })).ConfigureAwait(false);
                _left = result.Where((_, i) => blocked[i]).ToList();
                result = result.Where((_, i) => !blocked[i]).Select((c, i) => c with { Number = i + 1 }).ToList();
            }

            _channels = (DateTime.UtcNow, result, key);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ein Sender mit dem Ergebnis eines Abrufversuchs von diesem Server aus.</summary>
    public sealed record LiveStatus(string Name, string Group, bool InList, bool Reachable, int Status, string Note);

    /// <summary>
    /// Fragt jeden Sender einmal ab und sagt, ob dieser Server ihn empfängt und woran es sonst liegt. Enthält auch die
    /// Sender, die wegen fehlender Erreichbarkeit nicht in der Liste stehen.
    /// </summary>
    public async Task<IReadOnlyList<LiveStatus>> GetStatusAsync(CancellationToken ct)
    {
        var listed = await GetChannelsAsync(ct).ConfigureAwait(false);
        var all = listed.Select(c => (Channel: c, InList: true)).Concat(_left.Select(c => (Channel: c, InList: false))).ToList();
        using var gate = new SemaphoreSlim(6);
        var tasks = all.Select(async entry =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            int status;
            try
            {
                status = await ProbeAsync(entry.Channel.Url, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            var ok = status is >= 200 and < 300;
            var home = entry.Channel.Id.StartsWith("orf", StringComparison.Ordinal) ? "Österreich"
                : entry.Channel.Id == "arte-fr" ? "Frankreich oder Deutschland" : "Deutschland";
            var kept = entry.InList ? " Der Sender bleibt in der Liste, weil das vorübergehend sein kann." : string.Empty;
            var note = ok ? string.Empty
                : IsBlocked(status) ? "Ländersperre: der Sender lehnt Abrufe von diesem Server ab (Antwort " + status.ToString(CultureInfo.InvariantCulture) + "), er ist nur aus " + home
                    + " abrufbar. Dafür müsste der Server dort stehen oder über ein VPN dorthin ins Netz gehen."
                    + (entry.InList ? " Bei der nächsten Aktualisierung der Senderliste wird er ausgeblendet." : " Deshalb ist er ausgeblendet.")
                : status == 0 ? "Keine Antwort vom Sender (Zeitüberschreitung oder Verbindung abgelehnt)." + kept
                : "Der Sender antwortet mit Fehler " + status.ToString(CultureInfo.InvariantCulture) + "." + kept;
            return new LiveStatus(entry.Channel.Name, entry.Channel.Group, entry.InList, ok, status, note);
        }).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Antwort des Senders, die eine Sperre bedeutet und kein vorübergehendes Problem.</summary>
    private static bool IsBlocked(int status) => status is 403 or 451;

    /// <summary>Fragt die Senderadresse einmal ab; 0, wenn keine Antwort kam.</summary>
    private async Task<int> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await _http.CreateClient(ServiceRegistrator.HttpClientName)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return 0;
        }
    }

    [GeneratedRegex("apiToken\\\\?\":\\\\?\"([A-Za-z0-9]{24,64})")]
    private static partial Regex ZdfToken();

    /// <summary>
    /// Das Programm des ZDF für einen Tag, in <paramref name="programmes"/> unter „zdf:Sender“ abgelegt. Die Schnittstelle
    /// verlangt den Schlüssel, den die Seite des ZDF jedem Besucher mitgibt; er wird dort abgelesen.
    /// </summary>
    private async Task<bool> LoadZdfAsync(
        HttpClient client,
        DateTime day,
        string root,
        Dictionary<string, SortedDictionary<DateTimeOffset, (DateTimeOffset End, string Title, string Sub, string Text, string Image)>> programmes,
        CancellationToken ct)
    {
        if (_zdfTokens.Count == 0)
        {
            using var page = new HttpRequestMessage(HttpMethod.Get, "https://www.zdf.de/live-tv");
            page.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko/20100101 Firefox/128.0");
            using var answer = await client.SendAsync(page, ct).ConfigureAwait(false);
            answer.EnsureSuccessStatusCode();
            var html = await answer.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _zdfTokens = ZdfToken().Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();
        }

        var offset = TimeZoneInfo.Local.GetUtcOffset(day);
        string Edge(DateTime t) => Uri.EscapeDataString(new DateTimeOffset(t, offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        var url = "https://api.zdf.de/cmdm/epg/broadcasts?from=" + Edge(day) + "&to=" + Edge(day.AddDays(1)) + "&limit=400&order=asc&tvServices=ZDF,ZDFneo,ZDFinfo";
        foreach (var token in _zdfTokens.ToList())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Api-Auth", "Bearer " + token);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("http://zdf.de/rels/cmdm/broadcasts", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var e in list.EnumerateArray())
            {
                var service = Text(e, "tvService");
                if (service.Length == 0
                    || !DateTimeOffset.TryParse(Text(e, "airtimeBegin"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                    || !DateTimeOffset.TryParse(Text(e, "airtimeEnd"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                    || end <= start)
                {
                    continue;
                }

                var image = string.Empty;
                if (e.TryGetProperty("http://zdf.de/rels/image", out var picture) && picture.ValueKind == JsonValueKind.Object
                    && picture.TryGetProperty("layouts", out var layouts) && layouts.ValueKind == JsonValueKind.Object)
                {
                    var src = new[] { "1280x720", "1920x1080", "768x432", "2400x1350" }.Select(n => Text(layouts, n)).FirstOrDefault(v => v.StartsWith("https://", StringComparison.Ordinal));
                    if (src is not null)
                    {
                        image = root.TrimEnd('/') + "/Mediathek/" + LiveProxy.ImagePath(src, 960);
                    }
                }

                if (!programmes.TryGetValue("zdf:" + service, out var map))
                {
                    programmes["zdf:" + service] = map = new();
                }

                var text = Text(e, "text").Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase).Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase);
                map[start] = (end, Text(e, "title"), Text(e, "subtitle"), text, image);
            }

            return true;
        }

        // Kein Schlüssel hat gepasst: beim nächsten Mal neu ablesen.
        _zdfTokens = new List<string>();
        return false;
    }

    // Die Filmliste führt einige Streams mit http; die Server liefern alle auch über https.
    private static string Https(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    /// <param name="proxyRoot">Adresse dieses Servers; gesetzt, wenn die Sender durch ihn geleitet werden.</param>
    public async Task<string> GetPlaylistAsync(string? proxyRoot, CancellationToken ct)
    {
        var channels = await GetChannelsAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var c in channels)
        {
            sb.Append("#EXTINF:-1 tvg-id=\"").Append(c.Id).Append("\" tvg-chno=\"").Append(c.Number.ToString(CultureInfo.InvariantCulture))
              .Append("\" tvg-name=\"").Append(c.Name.Replace('"', '\'')).Append("\" group-title=\"").Append(c.Group).Append("\",")
              .Append(c.Name).Append('\n').Append(proxyRoot is null ? c.Url : proxyRoot + "/Mediathek/live/m/" + c.Id + ".m3u8").Append('\n');
        }

        return sb.ToString();
    }

    /// <param name="root">Adresse dieses Servers; darüber holt Jellyfin die Bilder der Sendungen.</param>
    public async Task<string> GetGuideAsync(string root, CancellationToken ct)
    {
        var channels = await GetChannelsAsync(ct).ConfigureAwait(false);
        var key = root + "|" + string.Join(',', channels.Select(c => c.Id));
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
                                // Der Bilderdienst liefert nicht jede Breite; das Plugin holt das Bild deshalb selbst (LiveProxy.ImageAsync).
                                var src = Text(wide, "src");
                                if (src.StartsWith("https://", StringComparison.Ordinal) && src.Contains("{width}", StringComparison.Ordinal))
                                {
                                    image = root.TrimEnd('/') + "/Mediathek/" + LiveProxy.ImagePath(src, 960);
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

        if (channels.Any(c => c.GuideId is not null && c.GuideId.StartsWith("zdf:", StringComparison.Ordinal)))
        {
            for (var day = -1; day <= 3; day++)
            {
                try
                {
                    await LoadZdfAsync(client, today.AddDays(day), root, programmes, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Mediathek: Programm des ZDF für Tag {Day} ließ sich nicht laden", day);
                }
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
