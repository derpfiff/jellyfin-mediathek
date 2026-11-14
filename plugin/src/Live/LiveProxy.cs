using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Mediathek.Live;

/// <summary>
/// Reicht die Live-Streams der Sender durch den eigenen Server. Jellyfin (genauer: sein ffmpeg) und Player holen den Stream
/// dann von hier statt beim Sender: das hilft, wenn nur der Server den Sender erreicht (Ländersperre, VPN, Proxy), und es
/// wählt gleich die beste Bildqualität statt der ersten in der Liste.
///
/// Weitergereicht wird nur, was der Server selbst ausgegeben hat: jede Adresse trägt eine Prüfsumme mit einem Schlüssel,
/// den das Plugin beim ersten Start würfelt und in seinen Einstellungen behält. Als offener Proxy lässt sich das nicht benutzen.
/// Auf demselben Weg kommen die Bilder der Mediathek (siehe <see cref="ImageAsync"/>).
/// </summary>
public sealed partial class LiveProxy
{
    private static readonly object KeyLock = new();
    private static byte[]? _key;

    /// <summary>Der Schlüssel bleibt über Neustarts gleich, damit schon ausgegebene Bildadressen gültig bleiben.</summary>
    private static byte[] Key
    {
        get
        {
            lock (KeyLock)
            {
                if (_key is not null)
                {
                    return _key;
                }

                var plugin = Plugin.Instance;
                var stored = plugin?.Configuration.ProxyKey ?? string.Empty;
                if (stored.Length < 32 || plugin is null)
                {
                    stored = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    if (plugin is not null)
                    {
                        plugin.Configuration.ProxyKey = stored;
                        plugin.SaveConfiguration();
                    }
                }

                return _key = Encoding.ASCII.GetBytes(stored);
            }
        }
    }

    private readonly IHttpClientFactory _http;

    public LiveProxy(IHttpClientFactory http) => _http = http;

    [GeneratedRegex("URI=\"([^\"]+)\"")]
    private static partial Regex UriAttribute();

    [GeneratedRegex(@"RESOLUTION=(\d+)x(\d+)")]
    private static partial Regex Resolution();

    [GeneratedRegex(@"[:,]BANDWIDTH=(\d+)")]
    private static partial Regex Bandwidth();

    [GeneratedRegex("AUDIO=\"([^\"]+)\"")]
    private static partial Regex AudioGroup();

    private static string Sign(string url) =>
        Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();

    /// <summary>
    /// Pfad (ab <c>Mediathek/</c>) für ein Bild der Mediathek. <paramref name="template"/> ist die Adresse des Bilderdiensts
    /// mit dem Platzhalter <c>{width}</c>.
    /// </summary>
    public static string ImagePath(string template, int width)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(template)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "img/" + Sign(template) + "/" + width.ToString(CultureInfo.InvariantCulture) + "/" + data + ".jpg";
    }

    /// <summary>
    /// Holt ein Bild der Mediathek. Der Bilderdienst liefert für einzelne Breiten kein Bild, sondern nur eine Beschreibung,
    /// und welche Breite fehlt, ist von Bild zu Bild verschieden; deshalb wird der Reihe nach die nächste versucht.
    /// </summary>
    public async Task ImageAsync(HttpContext context, string signature, int width, string data, CancellationToken ct)
    {
        string template;
        try
        {
            var dot = data.LastIndexOf('.');
            var payload = (dot > 0 ? data[..dot] : data).Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            template = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        catch (FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(template)), Encoding.ASCII.GetBytes(signature))
            || !template.StartsWith("https://", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var client = _http.CreateClient(ServiceRegistrator.HttpClientName);
        foreach (var w in new[] { width, 768, 960, 512, 1280, 480, 384, 1920 }.Where(w => w is >= 64 and <= 1920).Distinct())
        {
            var url = template.Replace("{width}", w.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!response.IsSuccessStatusCode || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Response.ContentType = type;
            context.Response.Headers["Cache-Control"] = "public, max-age=604800";
            if (response.Content.Headers.ContentLength is { } length)
            {
                context.Response.ContentLength = length;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await stream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>Pfadstück „Prüfsumme/Adresse.Endung“ für eine weiterzureichende Adresse.</summary>
    private static string Token(Uri target)
    {
        var url = target.AbsoluteUri;
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // ffmpeg erkennt Wiedergabelisten und Segmente an der Endung.
        var extension = Path.GetExtension(target.AbsolutePath);
        if (extension.Length is 0 or > 6 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            extension = ".bin";
        }

        return Sign(url) + "/" + data + extension;
    }

    /// <summary>
    /// Die Wiedergabeliste eines Senders, reduziert auf die beste Bildqualität samt Standard-Tonspur. Adressen zeigen auf
    /// <c>../p/…</c>, also relativ zur Adresse dieser Liste (<c>…/Mediathek/live/m/{id}.m3u8</c>).
    /// </summary>
    public async Task<string> MasterAsync(string url, CancellationToken ct)
    {
        // Manche Sender leiten erst weiter; relative Adressen in der Liste gelten ab der Adresse, die am Ende geantwortet hat.
        using var first = await _http.CreateClient(ServiceRegistrator.HttpClientName).GetAsync(new Uri(url), ct).ConfigureAwait(false);
        first.EnsureSuccessStatusCode();
        var source = first.RequestMessage?.RequestUri ?? new Uri(url);
        var text = await first.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

        // Ohne Auswahl (der Sender liefert direkt die Segmentliste): einfach durchreichen.
        if (!lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)))
        {
            return Rewrite(text, source, "../p/");
        }

        (string Info, string Uri, int Height, long Bits)? best = null;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                continue;
            }

            var uri = lines.Skip(i + 1).FirstOrDefault(l => l.Length > 0 && l[0] != '#');
            if (uri is null)
            {
                continue;
            }

            var res = Resolution().Match(lines[i]);
            var height = res.Success ? int.Parse(res.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            var bw = Bandwidth().Match(lines[i]);
            var bits = bw.Success ? long.Parse(bw.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (height > 1080)
            {
                continue;
            }

            if (best is null || height > best.Value.Height || (height == best.Value.Height && bits > best.Value.Bits))
            {
                best = (lines[i], uri, height, bits);
            }
        }

        if (best is null)
        {
            return Rewrite(text, source, "../p/");
        }

        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        // Wann diese Liste ausgegeben wurde, steht an den Adressen der Segmentlisten: daran erkennt ProxyAsync den ersten
        // Abruf eines neuen Zuschauers (siehe HoldBack).
        var opened = "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var info = best.Value.Info;
        var group = AudioGroup().Match(info);
        string? audioLine = null;
        if (group.Success)
        {
            var wanted = "GROUP-ID=\"" + group.Groups[1].Value + "\"";
            var audio = lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && l.Contains("TYPE=AUDIO", StringComparison.Ordinal) && l.Contains(wanted, StringComparison.Ordinal)).ToList();
            var chosen = audio.FirstOrDefault(l => l.Contains("DEFAULT=YES", StringComparison.Ordinal)) ?? audio.FirstOrDefault();
            if (chosen is not null)
            {
                audioLine = UriAttribute().Replace(chosen, m => "URI=\"../p/" + Token(new Uri(source, m.Groups[1].Value)) + opened + "\"");
            }
        }

        var hasAudio = audioLine is not null;

        // Untertitel-Listen und, falls keine Tonliste übrig ist, der Verweis darauf fallen weg.
        info = Regex.Replace(info, ",SUBTITLES=\"[^\"]*\"", string.Empty);
        if (!hasAudio)
        {
            info = Regex.Replace(info, ",AUDIO=\"[^\"]*\"", string.Empty);
        }

        // Erst das Bild, dann der Ton: in dieser Reihenfolge legt ffmpeg die Spuren an, und Jellyfin erwartet das Bild vorn.
        sb.Append(info).Append('\n').Append("../p/").Append(Token(new Uri(source, best.Value.Uri))).Append(opened).Append('\n');
        if (audioLine is not null)
        {
            sb.Append(audioLine).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Holt eine zuvor ausgegebene Adresse und reicht sie weiter; Wiedergabelisten werden dabei umgeschrieben.</summary>
    public async Task ProxyAsync(HttpContext context, string signature, string data, CancellationToken ct)
    {
        var dot = data.LastIndexOf('.');
        var payload = (dot > 0 ? data[..dot] : data).Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        Uri target;
        try
        {
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(url)), Encoding.ASCII.GetBytes(signature))
                || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            target = parsed;
        }
        catch (FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var response = await _http.CreateClient(ServiceRegistrator.HttpClientName).GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            return;
        }

        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var isPlaylist = type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || target.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        context.Response.Headers["Cache-Control"] = "no-store";
        if (isPlaylist)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (long.TryParse(context.Request.Query["t"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var opened)
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - opened is >= 0 and < HoldMilliseconds)
            {
                text = HoldBack(text);
            }

            context.Response.ContentType = "application/vnd.apple.mpegurl";
            // Diese Liste liegt selbst unter …/p/{Prüfsumme}/…, ihre Einträge also einen Ordner höher.
            await context.Response.WriteAsync(Rewrite(text, response.RequestMessage?.RequestUri ?? target, "../"), ct).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = type.Length > 0 ? type : "application/octet-stream";
        if (response.Content.Headers.ContentLength is { } length)
        {
            context.Response.ContentLength = length;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    /// <summary>Pfad (ab <c>Mediathek/</c>), unter dem ein Video der Mediathek durch den Server läuft.</summary>
    public static string VodPath(string url) => "vod/" + Token(new Uri(url));

    private static readonly HttpClient Vod = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AutomaticDecompression = System.Net.DecompressionMethods.None })
    {
        // Ein Film läuft länger als jede sinnvolle Frist für eine einzelne Anfrage.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Reicht ein Video durch, samt Teilabrufen (<c>Range</c>), damit Player darin springen können. Für Ländersperren zählt
    /// so der Standort des Servers.
    /// </summary>
    public async Task VodAsync(HttpContext context, string signature, string data, CancellationToken ct)
    {
        var dot = data.LastIndexOf('.');
        var payload = (dot > 0 ? data[..dot] : data).Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        Uri? target;
        try
        {
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(url)), Encoding.ASCII.GetBytes(signature))
                || !Uri.TryCreate(url, UriKind.Absolute, out target) || target.Scheme != Uri.UriSchemeHttps)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        catch (FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await RelayAsync(context, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pfad (ab <c>Mediathek/</c>), unter dem ein Video abgespielt wird, dessen Adresse beim Sender noch nicht bekannt ist:
    /// sie wird erst beim Abruf herausgesucht (siehe <c>MediathekController.Play</c>).
    /// </summary>
    public static string PlayPath(string videoId, int width) =>
        "play/" + PlaySignature(videoId) + "/" + width.ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(videoId) + ".mp4";

    public static string PlaySignature(string videoId) => Sign("play|" + videoId);

    /// <summary>Reicht die Datei unter <paramref name="target"/> durch, samt Teilabrufen.</summary>
    public async Task RelayAsync(HttpContext context, Uri target, CancellationToken ct)
    {
        var head = HttpMethods.IsHead(context.Request.Method);
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, target);
        request.Headers.UserAgent.ParseAdd("Jellyfin-Mediathek/0.2");
        foreach (var name in new[] { "Range", "If-Range" })
        {
            var value = context.Request.Headers[name].ToString();
            if (value.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Vod.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/mp4";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        if (response.Content.Headers.ContentLength is { } length)
        {
            context.Response.ContentLength = length;
        }

        if (response.Content.Headers.ContentRange is { } range)
        {
            context.Response.Headers["Content-Range"] = range.ToString();
        }

        foreach (var name in new[] { "ETag", "Last-Modified" })
        {
            if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
            {
                context.Response.Headers[name] = values.ToArray();
            }
        }

        if (head)
        {
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Ein Abruf der ganzen Datei von vorn ist ein Download (Player holen Teilstücke): sein Stand wird mitgezählt,
        // damit Oberflächen ihn anzeigen können (siehe Transfer).
        var total = response.Content.Headers.ContentLength ?? 0;
        // „Ab dem ersten Byte bis zum Ende“ ist dasselbe wie ohne Bereichsangabe; so holt Jellyfin die Datei, wenn eine
        // App sie über seinen eigenen Weg lädt.
        var wanted = context.Request.Headers["Range"].ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
        var whole = wanted.Length == 0 ? response.StatusCode == System.Net.HttpStatusCode.OK : wanted == "bytes=0-";
        if (!whole || total < 4 * 1024 * 1024)
        {
            await stream.CopyToAsync(context.Response.Body, 128 * 1024, ct).ConfigureAwait(false);
            return;
        }

        var key = TransferKey(target.AbsoluteUri);
        var mine = Guid.NewGuid();
        Transfers[key] = (mine, 0, total, DateTime.UtcNow, false);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        long sent = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sent += read;
                if (Transfers.TryGetValue(key, out var now) && now.Owner == mine)
                {
                    Transfers[key] = (mine, sent, total, DateTime.UtcNow, false);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            if (Transfers.TryGetValue(key, out var last) && last.Owner == mine)
            {
                Transfers[key] = (mine, sent, total, DateTime.UtcNow, sent >= total);
            }

            foreach (var old in Transfers.Where(t => DateTime.UtcNow - t.Value.At > TimeSpan.FromHours(6)).Select(t => t.Key).ToList())
            {
                Transfers.TryRemove(old, out _);
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Guid Owner, long Sent, long Total, DateTime At, bool Done)> Transfers = new();

    /// <summary>Unter diesem Schlüssel führt das Plugin den Stand eines Downloads der Datei mit dieser Adresse.</summary>
    public static string TransferKey(string url) => Sign("dl|" + url);

    /// <summary>Stand des jüngsten Downloads zu einem Schlüssel: Anteil in Prozent, fertig, Sekunden seit dem letzten Stück.</summary>
    public static (double Percent, bool Done, double Idle)? Transfer(string key) =>
        Transfers.TryGetValue(key, out var t) ? (t.Total > 0 ? t.Sent * 100.0 / t.Total : 0, t.Done, (DateTime.UtcNow - t.At).TotalSeconds) : null;

    /// <summary>So lange nach dem Einschalten gilt ein Abruf der Segmentliste als der erste dieses Zuschauers.</summary>
    private const int HoldMilliseconds = 3000;

    /// <summary>So viele Sekunden Programm bekommt ein Zuschauer beim Einschalten als Vorrat.</summary>
    private const double HeadStartSeconds = 20;

    [GeneratedRegex(@"^#EXTINF:([\d.]+)")]
    private static partial Regex SegmentLength();

    /// <summary>
    /// Vorrat beim Einschalten. ffmpeg beginnt einen Live-Sender drei Segmente vor dem Ende der Liste und bekommt danach
    /// nur noch, was der Sender in Echtzeit nachliefert: Das Abspielgerät hängt dann ohne Puffer an der Live-Kante, wartet
    /// beim Einschalten sekundenlang auf die ersten Stücke und bleibt bei jeder kleinen Verzögerung stehen. Beim ersten
    /// Abruf fehlen der Liste deshalb die jüngsten rund zwanzig Sekunden. ffmpeg beginnt entsprechend früher, findet beim
    /// nächsten Abruf alles Fehlende auf einmal vor und reicht es sofort weiter: Das Bild steht schneller, und das Gerät
    /// hat einen Vorrat. Der Preis sind zwanzig Sekunden mehr Abstand zum Live-Signal.
    /// </summary>
    private static string HoldBack(string playlist)
    {
        var lines = playlist.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (lines.Any(l => l.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal) || l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)))
        {
            return playlist;
        }

        // Je Segment die Zeile, mit der es beginnt (#EXTINF), und seine Länge.
        var starts = Enumerable.Range(0, lines.Length).Where(i => lines[i].StartsWith("#EXTINF", StringComparison.Ordinal)).ToList();
        var held = 0;
        var seconds = 0.0;
        // Mindestens sechs Segmente bleiben stehen, damit ffmpeg wie gewohnt drei vor dem Ende beginnen kann.
        while (held < starts.Count - 6 && seconds < HeadStartSeconds)
        {
            held++;
            var length = SegmentLength().Match(lines[starts[^held]]);
            seconds += length.Success && double.TryParse(length.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 2;
        }

        if (held == 0)
        {
            return playlist;
        }

        // Alles ab dem ersten zurückgehaltenen Segment fällt weg; Zeilen direkt davor, die schon zu ihm gehören
        // (Zeitstempel, Sprungmarke), ebenfalls.
        var cut = starts[^held];
        while (cut > 0 && (lines[cut - 1].StartsWith("#EXT-X-PROGRAM-DATE-TIME", StringComparison.Ordinal) || lines[cut - 1] == "#EXT-X-DISCONTINUITY"))
        {
            cut--;
        }

        return string.Join('\n', lines[..cut]) + "\n";
    }

    /// <summary>So viele Segmente bleiben am Ende einer Live-Liste stehen.</summary>
    private const int KeepSegments = 24;

    [GeneratedRegex(@"^#EXT-X-MEDIA-SEQUENCE:(\d+)")]
    private static partial Regex MediaSequence();

    [GeneratedRegex(@"^#EXT-X-DISCONTINUITY-SEQUENCE:(\d+)")]
    private static partial Regex DiscontinuitySequence();

    /// <summary>
    /// Die Sender führen in ihren Live-Listen zwei Stunden Rückschau, mehrere tausend Einträge, und Player laden die Liste
    /// alle paar Sekunden neu. Für das laufende Programm genügt das Ende: der Anfang wird abgeschnitten und die Zählung
    /// der Segmente entsprechend weitergestellt, sodass Player keinen Unterschied bemerken.
    /// </summary>
    private static string TrimLive(string playlist)
    {
        var lines = playlist.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var uris = Enumerable.Range(0, lines.Length).Where(i => lines[i].Length > 0 && lines[i][0] != '#').ToList();
        if (uris.Count <= KeepSegments * 2 || lines.Any(l => l.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal) || l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)))
        {
            return playlist;
        }

        var firstSegmentTag = Enumerable.Range(0, lines.Length).First(i => lines[i].StartsWith("#EXTINF", StringComparison.Ordinal));
        var removed = uris.Count - KeepSegments;
        // Erste Zeile, die bleibt: die erste nach der letzten gestrichenen Adresse.
        var cut = uris[removed - 1] + 1;
        var dropped = lines[firstSegmentTag..cut];
        var discontinuities = dropped.Count(l => l == "#EXT-X-DISCONTINUITY");
        // Schlüssel und Kopfdaten gelten ab ihrer Stelle für alles Folgende und müssen mit nach vorn.
        var carried = new[] { "#EXT-X-KEY", "#EXT-X-MAP" }
            .Select(tag => dropped.LastOrDefault(l => l.StartsWith(tag, StringComparison.Ordinal)))
            .Where(l => l is not null).Select(l => l!).ToList();

        var sb = new StringBuilder();
        var sawDiscontinuitySequence = false;
        foreach (var header in lines[..firstSegmentTag])
        {
            var seq = MediaSequence().Match(header);
            var dis = DiscontinuitySequence().Match(header);
            if (seq.Success)
            {
                sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append((long.Parse(seq.Groups[1].Value, CultureInfo.InvariantCulture) + removed).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            else if (dis.Success)
            {
                sawDiscontinuitySequence = true;
                sb.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append((long.Parse(dis.Groups[1].Value, CultureInfo.InvariantCulture) + discontinuities).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            else
            {
                sb.Append(header).Append('\n');
            }
        }

        if (!sawDiscontinuitySequence && discontinuities > 0)
        {
            sb.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(discontinuities.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var line in carried)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append(string.Join('\n', lines[cut..]));
        return sb.ToString();
    }

    private static string Rewrite(string playlist, Uri source, string prefix)
    {
        var sb = new StringBuilder();
        foreach (var raw in TrimLive(playlist).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw;
            if (line.Length > 0 && line[0] != '#')
            {
                line = Uri.TryCreate(source, line.Trim(), out var absolute) ? prefix + Token(absolute) : line;
            }
            else if (line.StartsWith("#EXT", StringComparison.Ordinal) && line.Contains("URI=\"", StringComparison.Ordinal))
            {
                line = UriAttribute().Replace(line, m => Uri.TryCreate(source, m.Groups[1].Value, out var absolute) ? "URI=\"" + prefix + Token(absolute) + "\"" : m.Value);
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }
}
