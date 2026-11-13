using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Mediathek.Mvw;

/// <summary>Ein Beitrag aus der Filmliste von MediathekView.</summary>
public sealed record MvwEntry(
    string Id,
    string Channel,
    string Topic,
    string Title,
    string Description,
    long Timestamp,
    int Duration,
    string UrlVideo,
    string UrlVideoHd,
    string UrlVideoLow,
    string UrlSubtitle,
    string UrlWebsite);

/// <summary>Fragt die Suche von MediathekViewWeb ab (https://mediathekviewweb.de). Antworten bleiben kurz im Speicher.</summary>
public sealed class MvwClient
{
    private const string Endpoint = "https://mediathekviewweb.de/api/query";

    /// <summary>Mehr als so viele Treffer liefert die Suche nicht in einer Antwort.</summary>
    public const int MaxPageSize = 1000;

    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _http;
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<MvwEntry> Items, int Total)> _cache = new();

    public MvwClient(IHttpClientFactory http) => _http = http;

    /// <summary>
    /// Sucht Beiträge, neueste zuerst. <paramref name="channel"/> und <paramref name="topic"/> müssen genau passen,
    /// <paramref name="text"/> wird in Titel und Sendung gesucht.
    /// </summary>
    public async Task<(IReadOnlyList<MvwEntry> Items, int Total)> QueryAsync(
        string? channel, string? topic, string? text, int offset, int size, bool future, CancellationToken ct)
    {
        size = Math.Clamp(size, 1, MaxPageSize);
        offset = Math.Max(0, offset);
        var key = string.Join('\u001f', channel, topic, text, offset, size, future);
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTime)
        {
            return (hit.Items, hit.Total);
        }

        var queries = new List<object>();
        if (!string.IsNullOrWhiteSpace(channel))
        {
            queries.Add(new { fields = new[] { "channel" }, query = channel });
        }

        if (!string.IsNullOrWhiteSpace(topic))
        {
            queries.Add(new { fields = new[] { "topic" }, query = topic });
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            queries.Add(new { fields = new[] { "title", "topic" }, query = text });
        }

        var body = JsonSerializer.Serialize(new { queries, sortBy = "timestamp", sortOrder = "desc", future, offset, size });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            // Die Suche erwartet den JSON-Text ausdrücklich als text/plain.
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        using var response = await _http.CreateClient(ServiceRegistrator.HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var items = new List<MvwEntry>();
        var total = 0;
        if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("queryInfo", out var info) && info.TryGetProperty("totalResults", out var totalElement))
            {
                total = (int)Math.Min(int.MaxValue, Number(totalElement));
            }

            if (result.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in results.EnumerateArray())
                {
                    var entry = new MvwEntry(
                        Text(e, "id"), Text(e, "channel"), Text(e, "topic"), Text(e, "title"), Text(e, "description"),
                        e.TryGetProperty("timestamp", out var ts) ? Number(ts) : 0,
                        (int)Math.Clamp(e.TryGetProperty("duration", out var d) ? Number(d) : 0, 0, int.MaxValue),
                        Text(e, "url_video"), Text(e, "url_video_hd"), Text(e, "url_video_low"), Text(e, "url_subtitle"), Text(e, "url_website"));

                    // Die Suche findet auch Teilwörter; Sender und Sendung sollen genau stimmen.
                    if (!string.IsNullOrWhiteSpace(channel) && !string.Equals(entry.Channel, channel, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(topic) && !string.Equals(entry.Topic, topic, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.Id.Length > 0 && IsWebUrl(entry.UrlVideo))
                    {
                        items.Add(entry);
                    }
                }
            }
        }

        _cache[key] = (DateTime.UtcNow, items, total);
        if (_cache.Count > 400)
        {
            foreach (var old in _cache.Where(p => DateTime.UtcNow - p.Value.At > CacheTime).Select(p => p.Key).ToList())
            {
                _cache.TryRemove(old, out _);
            }
        }

        return (items, total);
    }

    public static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;

    // Zahlen kommen mal als Zahl, mal als Zeichenkette.
    private static long Number(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var n) ? n : (long)v.GetDouble(),
        JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
        _ => 0
    };
}
