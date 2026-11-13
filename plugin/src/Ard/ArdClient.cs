using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Ard;

/// <summary>Ein Eintrag einer Liste der ARD Mediathek: eine Sendung (Reihe, Serie) oder ein einzelnes Video.</summary>
public sealed record ArdTeaser(
    string Id,
    bool IsShow,
    string Title,
    string Tagline,
    string ShowId,
    string ShowTitle,
    int Duration,
    DateTime? Date,
    string ImageWide,
    string ImagePoster,
    string CoreType,
    string Rating,
    int? Season,
    int? Episode);

/// <summary>Eine Fassung eines Videos in einer bestimmten Auflösung.</summary>
public sealed record ArdStream(string Label, int Width, int Height, string Url);

/// <summary>Alles zu einem einzelnen Video, wie es die Seite des Videos nennt.</summary>
public sealed record ArdVideo(
    string Id,
    string Title,
    string Synopsis,
    string ShowId,
    string ShowTitle,
    int Duration,
    DateTime? Date,
    string ImageWide,
    string Rating,
    string Publisher,
    bool GeoBlocked,
    IReadOnlyList<ArdStream> Streams);

/// <summary>Eine Sendung mit ihren Folgen.</summary>
public sealed record ArdShow(string Id, string Title, string Synopsis, string ImageWide, string ImagePoster, string Publisher, IReadOnlyList<ArdTeaser> Episodes);

/// <summary>Eine Zeile der Startseite („Unsere Top-Serien“, „Film-Empfehlungen“ …).</summary>
public sealed record ArdRow(string Id, string Title, IReadOnlyList<ArdTeaser> Teasers);

/// <summary>
/// Liest die öffentliche Schnittstelle der ARD Mediathek (api.ardmediathek.de), aus der auch deren Webseite ihre Inhalte holt:
/// Startseite, Suche, Sendungen und Videos samt Bildern, Beschreibungen und Videoadressen. Antworten bleiben kurz im Speicher.
/// </summary>
public sealed partial class ArdClient
{
    private const string Api = "https://api.ardmediathek.de";
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<ArdClient> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, object Value)> _cache = new();
    private readonly SemaphoreSlim _parallel = new(6, 6);

    public ArdClient(IHttpClientFactory http, ILogger<ArdClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    [GeneratedRegex(@"\s*\(S(\d{1,3})/E(\d{1,4})\)")]
    private static partial Regex EpisodeMark();

    [GeneratedRegex(@"\s*[·|:,-]?\s*Staffel\s+(\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonWords();

    [GeneratedRegex(@"\bFolge\s+(\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWords();

    // Fassungen neben der Hauptfassung, die in Listen nur doppeln.
    private static readonly string[] SideVersions = { "Audiodeskription", "Gebärdensprache", "Hörfassung", "Hörfilm", "Klare Sprache", "(AD)", "(DGS)" };

    /// <summary>Liest „(S01/E08)“ am Ende eines Folgentitels und nimmt es heraus.</summary>
    public static (string Title, int? Season, int? Episode) SplitEpisodeMark(string title)
    {
        int? season = null, episode = null;
        var m = EpisodeMark().Match(title);
        if (m.Success)
        {
            season = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            title = title.Remove(m.Index, m.Length);
        }

        // „Folge 3 · Staffel 2“ nennt dasselbe in Worten; die Staffel fällt aus dem Titel, die Folge bleibt lesbar stehen.
        var words = SeasonWords().Match(title);
        if (words.Success)
        {
            season ??= int.Parse(words.Groups[1].Value, CultureInfo.InvariantCulture);
            title = title.Remove(words.Index, words.Length);
        }

        var folge = EpisodeWords().Match(title);
        if (folge.Success)
        {
            episode ??= int.Parse(folge.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return (title.Trim(' ', '·', '|', '-', ':'), season, episode);
    }

    // MARK: Abrufe

    public async Task<IReadOnlyList<ArdRow>> HomeAsync(CancellationToken ct)
    {
        return await Cached("home", async () =>
        {
            using var doc = await Get(Api + "/page-gateway/pages/ard/home?embedded=true", ct).ConfigureAwait(false);
            var rows = new List<ArdRow>();
            if (doc.RootElement.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in widgets.EnumerateArray())
                {
                    var type = Text(w, "type");
                    if (type is not ("stage" or "gridlist" or "extended_gridlist"))
                    {
                        continue;
                    }

                    var teasers = Teasers(w);
                    var title = type == "stage" ? "Empfehlungen" : Text(w, "title");
                    if (teasers.Count >= 3 && title.Length > 0 && Text(w, "id").Length > 0)
                    {
                        rows.Add(new ArdRow(Text(w, "id"), title, teasers));
                    }
                }
            }

            return (IReadOnlyList<ArdRow>)rows;
        }).ConfigureAwait(false);
    }

    /// <summary>Sucht Sendungen und Videos; Sendungen stehen vorn.</summary>
    public async Task<IReadOnlyList<ArdTeaser>> SearchAsync(string query, int shows, int videos, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(query);
        return await Cached("search|" + query.ToLowerInvariant() + "|" + shows + "|" + videos, async () =>
        {
            var found = new List<ArdTeaser>();
            foreach (var (kind, size) in new[] { ("grouping", shows), ("vod", videos) })
            {
                if (size <= 0)
                {
                    continue;
                }

                try
                {
                    using var doc = await Get(Api + "/page-gateway/widgets/ard/search/" + kind + "?searchString=" + q + "&pageNumber=0&pageSize=" + size.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                    found.AddRange(Teasers(doc.RootElement));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Mediathek: Suche ({Kind}) nach {Query} fehlgeschlagen", kind, query);
                }
            }

            // Die Mediathek stellt Sendungen vor Einzeltitel, auch wenn ein Titel genauer passt („… 2026“). Was mehr
            // Wörter der Anfrage im Namen trägt, rückt nach vorn; sonst bleibt die Reihenfolge der Mediathek.
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length > 1).ToArray();
            int Score(ArdTeaser t) => words.Count(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase));
            return (IReadOnlyList<ArdTeaser>)found.DistinctBy(t => t.Id).OrderByDescending(Score).ToList();
        }).ConfigureAwait(false);
    }

    public async Task<ArdShow?> ShowAsync(string id, int maxEpisodes, CancellationToken ct)
    {
        return await Cached("show|" + id + "|" + maxEpisodes, async () =>
        {
            using var doc = await Get(Api + "/page-gateway/pages/ard/grouping/" + Uri.EscapeDataString(id) + "?embedded=true&pageSize=48", ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var episodes = new List<ArdTeaser>();
            var total = 0;
            if (root.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in widgets.EnumerateArray())
                {
                    episodes.AddRange(Teasers(w).Where(t => !t.IsShow));
                    if (w.TryGetProperty("pagination", out var p) && p.TryGetProperty("totalElements", out var te) && te.TryGetInt32(out var n))
                    {
                        total = Math.Max(total, n);
                    }
                }
            }

            // Weitere Seiten der Folgenliste.
            for (var page = 1; episodes.Count < Math.Min(total, maxEpisodes) && page < 12; page++)
            {
                try
                {
                    using var more = await Get(Api + "/page-gateway/widgets/ard/asset/" + Uri.EscapeDataString(id) + "?pageNumber=" + page.ToString(CultureInfo.InvariantCulture)
                        + "&pageSize=48&embedded=true&seasoned=false&seasonNumber=&withAudiodescription=false&withOriginalWithSubtitle=false&withOriginalversion=false&single=false", ct).ConfigureAwait(false);
                    var next = Teasers(more.RootElement).Where(t => !t.IsShow).ToList();
                    if (next.Count == 0)
                    {
                        break;
                    }

                    episodes.AddRange(next);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Mediathek: weitere Folgen von {Show} ließen sich nicht laden", id);
                    break;
                }
            }

            var images = root.TryGetProperty("images", out var im) ? im : default;
            var show = new ArdShow(
                id,
                Text(root, "title"),
                Text(root, "synopsis"),
                Image(images, "aspect16x9", 1280),
                Image(images, "aspect3x4", 600),
                root.TryGetProperty("publicationService", out var ps) ? Text(ps, "name") : string.Empty,
                episodes.DistinctBy(t => t.Id).Take(maxEpisodes).ToList());
            return show.Title.Length > 0 ? show : null;
        }).ConfigureAwait(false);
    }

    public async Task<ArdVideo?> VideoAsync(string id, CancellationToken ct)
    {
        return await Cached("video|" + id, async () =>
        {
            {
                using var doc = await Get(Api + "/page-gateway/pages/ard/item/" + Uri.EscapeDataString(id) + "?embedded=true&mcV6=true", ct).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array || widgets.GetArrayLength() == 0)
                {
                    return null;
                }

                var w = widgets[0];
                var streams = new List<ArdStream>();
                var duration = 0;
                var geo = w.TryGetProperty("geoblocked", out var gb) && gb.ValueKind == JsonValueKind.True;
                if (w.TryGetProperty("mediaCollection", out var mc) && mc.TryGetProperty("embedded", out var emb) && emb.ValueKind == JsonValueKind.Object)
                {
                    if (emb.TryGetProperty("meta", out var meta) && meta.TryGetProperty("durationSeconds", out var ds) && ds.TryGetInt32(out var seconds))
                    {
                        duration = seconds;
                    }

                    if (emb.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        // Die Hauptfassung; Fassungen mit Audiodeskription oder Gebärdensprache stehen daneben und bleiben außen vor.
                        var main = list.EnumerateArray().FirstOrDefault(s => Text(s, "kind") == "main");
                        if (main.ValueKind != JsonValueKind.Object && list.GetArrayLength() > 0)
                        {
                            main = list[0];
                        }

                        if (main.ValueKind == JsonValueKind.Object && main.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var m in media.EnumerateArray())
                            {
                                var url = Text(m, "url");
                                if (Text(m, "mimeType") != "video/mp4" || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                                {
                                    continue;
                                }

                                var width = m.TryGetProperty("maxHResolutionPx", out var wp) && wp.TryGetInt32(out var wv) ? wv : 0;
                                var height = m.TryGetProperty("maxVResolutionPx", out var hp) && hp.TryGetInt32(out var hv) ? hv : (width > 0 ? width * 9 / 16 : 0);
                                if (streams.All(s => s.Url != url && s.Width != width))
                                {
                                    streams.Add(new ArdStream(Text(m, "forcedLabel"), width, height, url));
                                }
                            }
                        }
                    }
                }

                var show = w.TryGetProperty("show", out var sh) && sh.ValueKind == JsonValueKind.Object ? sh : default;
                var image = w.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object ? Text(img, "src") : string.Empty;
                if (!image.StartsWith("https://", StringComparison.Ordinal) || !image.Contains("{width}", StringComparison.Ordinal))
                {
                    image = string.Empty;
                }
                return new ArdVideo(
                    id,
                    Text(w, "title"),
                    Text(w, "synopsis").Replace("\r\n", "\n", StringComparison.Ordinal),
                    show.ValueKind == JsonValueKind.Object ? Text(show, "id") : string.Empty,
                    show.ValueKind == JsonValueKind.Object ? Text(show, "title") : string.Empty,
                    duration,
                    Date(w, "broadcastedOn"),
                    image,
                    Text(w, "maturityContentRating"),
                    w.TryGetProperty("publicationService", out var ps) ? Text(ps, "name") : string.Empty,
                    geo,
                    streams.OrderByDescending(s => s.Width).ToList());
            }
        }).ConfigureAwait(false);
    }

    // MARK: Hilfen

    private static List<ArdTeaser> Teasers(JsonElement container)
    {
        var result = new List<ArdTeaser>();
        if (container.ValueKind != JsonValueKind.Object || !container.TryGetProperty("teasers", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var t in list.EnumerateArray())
        {
            var type = Text(t, "type");
            var core = Text(t, "coreAssetType");
            var isShow = type == "show";
            // Nur Sendungen und abrufbare Videos; Livestreams, Sportereignisse, Themenseiten und Trailer bleiben außen vor.
            if (!isShow && type is not ("ondemand" or "poster"))
            {
                continue;
            }

            if (core is "EXTRA_TRAILER" or "PERMANENT_LIVESTREAM" or "EVENT_LIVESTREAM")
            {
                continue;
            }

            // Kennung für die Seite des Eintrags: die des Verweises, nicht die der Kachel.
            var id = t.TryGetProperty("links", out var links) && links.TryGetProperty("target", out var target) ? Text(target, "id") : string.Empty;
            if (id.Length == 0)
            {
                id = Text(t, "id");
            }

            if (id.Length == 0 || id.Contains('|', StringComparison.Ordinal))
            {
                continue;
            }

            var full = Text(t, "longTitle");
            if (full.Length == 0)
            {
                full = Text(t, "mediumTitle");
            }

            if (!isShow && SideVersions.Any(v => full.Contains(v, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var (_, season, episode) = SplitEpisodeMark(full);

            // „Titel | Werbezeile“: der Teil hinter dem Strich ist eine Beschreibung.
            var bar = full.IndexOf(" | ", StringComparison.Ordinal);
            var title = bar > 0 ? full[..bar].Trim() : full.Trim();
            var tagline = bar > 0 ? full[(bar + 3)..].Trim() : string.Empty;
            var images = t.TryGetProperty("images", out var im) ? im : default;
            var show = t.TryGetProperty("show", out var sh) && sh.ValueKind == JsonValueKind.Object ? sh : default;
            int.TryParse(Text(t, "duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration);
            if (duration == 0 && t.TryGetProperty("duration", out var dn) && dn.ValueKind == JsonValueKind.Number)
            {
                dn.TryGetInt32(out duration);
            }

            if (title.Length == 0)
            {
                continue;
            }

            result.Add(new ArdTeaser(
                id, isShow, title, tagline,
                show.ValueKind == JsonValueKind.Object ? Text(show, "id") : string.Empty,
                show.ValueKind == JsonValueKind.Object ? Text(show, "title") : string.Empty,
                duration, Date(t, "broadcastedOn"),
                Image(images, "aspect16x9", 960), Image(images, "aspect3x4", 600), core, Text(t, "maturityContentRating"), season, episode));
        }

        return result;
    }

    private static string Image(JsonElement images, string kind, int width)
    {
        if (images.ValueKind != JsonValueKind.Object || !images.TryGetProperty(kind, out var image) || image.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        // Die Adresse behält ihren Platzhalter {width}; die Breite setzt erst der Abruf ein (siehe LiveProxy.ImageAsync).
        var src = Text(image, "src");
        return src.StartsWith("https://", StringComparison.Ordinal) && src.Contains("{width}", StringComparison.Ordinal) ? src : string.Empty;
    }

    private static DateTime? Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Text(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;

    /// <summary>Höchstens sechs Abrufe laufen zugleich; mehr auf einmal beantwortet die Mediathek nur schleppend.</summary>
    private async Task<JsonDocument> Get(string url, CancellationToken ct)
    {
        await _parallel.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = await _http.CreateClient(ServiceRegistrator.HttpClientName).GetStreamAsync(url, ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            _parallel.Release();
        }
    }

    private async Task<T> Cached<T>(string key, Func<Task<T>> load)
    {
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTime && hit.Value is T value)
        {
            return value;
        }

        var loaded = await load().ConfigureAwait(false);
        if (loaded is not null)
        {
            _cache[key] = (DateTime.UtcNow, loaded);
        }

        if (_cache.Count > 3000)
        {
            foreach (var old in _cache.Where(p => DateTime.UtcNow - p.Value.At > CacheTime).Select(p => p.Key).ToList())
            {
                _cache.TryRemove(old, out _);
            }
        }

        return loaded;
    }
}
