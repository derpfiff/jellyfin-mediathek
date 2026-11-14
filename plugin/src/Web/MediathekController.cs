using System.Text;
using Jellyfin.Plugin.Mediathek.Ard;
using Jellyfin.Plugin.Mediathek.Channel;
using Jellyfin.Plugin.Mediathek.Live;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Mediathek.Web;

/// <summary>Senderliste und Programm für das Live-TV von Jellyfin, dazu Suche und Einrichtung.</summary>
[ApiController]
[Route("Mediathek")]
public class MediathekController : ControllerBase
{
    private const int RememberedSearches = 24;

    private readonly LiveService _live;
    private readonly LiveProxy _proxy;
    private readonly IChannelManager _channels;
    private readonly IUserManager _users;
    private readonly IDtoService _dto;
    private readonly ArdClient _ard;
    private readonly IServerApplicationHost _host;
    private readonly ITunerHostManager _tuners;
    private readonly IListingsManager _listings;
    private readonly IConfigurationManager _config;
    private readonly ILibraryManager _library;
    private readonly LiveWarmup _warmup;
    private readonly MediathekChannel _mediathek;

    public MediathekController(LiveService live, LiveProxy proxy, ArdClient ard, IDtoService dto, IChannelManager channels, IUserManager users, IServerApplicationHost host, ITunerHostManager tuners, IListingsManager listings, IConfigurationManager config, ILibraryManager library, LiveWarmup warmup, MediathekChannel mediathek)
    {
        _mediathek = mediathek;
        _live = live;
        _proxy = proxy;
        _channels = channels;
        _ard = ard;
        _dto = dto;
        _users = users;
        _host = host;
        _tuners = tuners;
        _listings = listings;
        _config = config;
        _library = library;
        _warmup = warmup;
    }

    // Beide Adressen ruft Jellyfin selbst ab, ohne Anmeldung. Sie enthalten nur öffentliche Angaben der Sender.
    [HttpGet("live.m3u")]
    [AllowAnonymous]
    public async Task<ActionResult> GetPlaylist(CancellationToken ct)
    {
        var viaServer = Plugin.Instance?.Configuration.ViaServer ?? true;
        var root = viaServer ? Request.Scheme + "://" + Request.Host + Request.PathBase : null;
        return Content(await _live.GetPlaylistAsync(root, ct).ConfigureAwait(false), "audio/x-mpegurl", Encoding.UTF8);
    }

    /// <summary>Wiedergabeliste eines Live-Senders in bester Qualität; die Segmente laufen über <c>live/p</c>.</summary>
    [HttpGet("live/m/{id}.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> GetLiveMaster([FromRoute] string id, CancellationToken ct)
    {
        var channel = (await _live.GetChannelsAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.Id == id);
        if (channel is null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-store";
        try
        {
            return Content(await _proxy.MasterAsync(channel.Url, ct).ConfigureAwait(false), "application/vnd.apple.mpegurl", Encoding.UTF8);
        }
        catch (HttpRequestException)
        {
            // Der Sender ist von hier nicht erreichbar (z. B. Ländersperre).
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Ein Bild der Mediathek in passender Größe (siehe <see cref="LiveProxy.ImageAsync"/>).</summary>
    [HttpGet("img/{signature}/{width:int}/{data}")]
    [AllowAnonymous]
    public Task GetImage([FromRoute] string signature, [FromRoute] int width, [FromRoute] string data, CancellationToken ct) =>
        _proxy.ImageAsync(HttpContext, signature, width, data, ct);

    [HttpGet("live/p/{signature}/{data}")]
    [AllowAnonymous]
    public Task GetLivePart([FromRoute] string signature, [FromRoute] string data, CancellationToken ct) =>
        _proxy.ProxyAsync(HttpContext, signature, data, ct);

    /// <summary>Ein Video der Mediathek, durch den Server gereicht (siehe <see cref="LiveProxy.VodAsync"/>).</summary>
    [HttpGet("vod/{signature}/{data}")]
    [HttpHead("vod/{signature}/{data}")]
    [AllowAnonymous]
    public Task GetVideo([FromRoute] string signature, [FromRoute] string data, CancellationToken ct) =>
        _proxy.VodAsync(HttpContext, signature, data, ct);

    [HttpGet("guide.xml")]
    [AllowAnonymous]
    public async Task<ActionResult> GetGuide(CancellationToken ct) =>
        Content(await _live.GetGuideAsync(Request.Scheme + "://" + Request.Host + Request.PathBase, ct).ConfigureAwait(false), "application/xml", Encoding.UTF8);

    /// <summary>Die Reihen der Mediathek-Startseite in ihrer Reihenfolge; die Titel einer Reihe liefert <c>row</c>.</summary>
    [HttpGet("home")]
    [Authorize]
    public async Task<ActionResult> Home(CancellationToken ct)
    {
        var rows = await _ard.HomeAsync(ct).ConfigureAwait(false);
        return new JsonResult(new { rows = rows.Take(18).Select(r => new { id = r.Id, title = r.Title, count = r.Teasers.Count }) });
    }

    /// <summary>Die Titel einer Startseiten-Reihe als Titel des Kanals, in der Reihenfolge der Mediathek.</summary>
    [HttpGet("row")]
    [Authorize]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> Row([FromQuery] string id, CancellationToken ct)
    {
        var rows = await _ard.HomeAsync(ct).ConfigureAwait(false);
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            return NotFound();
        }

        await Prefetch(row.Teasers, ct).ConfigureAwait(false);
        return await Listing(new[] { MediathekChannel.RowId(row.Id) }, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sucht in der Mediathek und liefert die Treffer als Titel des Kanals, also mit Jellyfin-Kennungen: Oberflächen können
    /// sie wie jeden anderen Titel anzeigen, öffnen und abspielen. Der Begriff erscheint danach auch als Ordner unter „Suche“.
    /// </summary>
    [HttpGet("search")]
    [Authorize]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> Search([FromQuery] string q, CancellationToken ct)
    {
        q = string.Join(' ', (q ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var plugin = Plugin.Instance;
        if (plugin is null || q.Length is < 2 or > 80 || q.Contains('|', StringComparison.Ordinal))
        {
            return BadRequest();
        }

        // Jellyfin legt Kanal-Titel nur an, wenn ihr Ordner aufgelistet wird. Der Begriff wird deshalb gemerkt (neueste vorn)
        // und sein Ordner anschließend auf dem üblichen Weg geöffnet.
        var config = plugin.Configuration;
        // Oberflächen suchen schon beim Tippen: Anfänge des Begriffs („tag“, „tages“ vor „tagesschau“) fallen wieder heraus.
        var remembered = config.Searches.Where(s => !q.StartsWith(s, StringComparison.OrdinalIgnoreCase)).Prepend(q).Take(RememberedSearches).ToArray();
        if (!remembered.SequenceEqual(config.Searches))
        {
            config.Searches = remembered;
            plugin.SaveConfiguration();
        }

        var found = await _ard.SearchAsync(q, 12, 36, ct).ConfigureAwait(false);
        await Prefetch(found, ct).ConfigureAwait(false);
        return await Listing(new[] { MediathekChannel.SearchFolder, MediathekChannel.SearchId(q) }, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Der Inhalt einer Sendung oder Staffel (Kennung des Ordners in Jellyfin): ihre Staffeln oder Folgen, vollständig
    /// auch dann, wenn einzelne Folgen gerade zusätzlich in einer Reihe der Startseite oder einer Suche stehen.
    /// </summary>
    [HttpGet("children")]
    [Authorize]
    public Task<ActionResult<QueryResult<BaseItemDto>>> Children([FromQuery] Guid id, CancellationToken ct) =>
        Listing(Array.Empty<string>(), id, ct);

    /// <summary>
    /// Die angefangenen Titel der Mediathek, zuletzt gesehene vorn. Jellyfin merkt sich die Stelle wie bei jedem anderen
    /// Titel; seine eigene Liste „Weiterschauen“ führt Titel aus Kanälen aber nicht überall auf.
    /// </summary>
    [HttpGet("resume")]
    [Authorize]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> Resume()
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || _users.GetUserById(userId) is not { } user)
        {
            return Unauthorized();
        }

        var all = await _channels.GetChannelsInternalAsync(new ChannelQuery { UserId = userId }).ConfigureAwait(false);
        var channel = all.Items.FirstOrDefault(c => string.Equals(c.Name, "Mediathek", StringComparison.Ordinal));
        if (channel is null)
        {
            return new QueryResult<BaseItemDto>();
        }

        var items = _library.GetItemList(new InternalItemsQuery(user)
        {
            ChannelIds = new[] { channel.Id },
            IsResumable = true,
            IsFolder = false,
            Recursive = true,
        });
        var options = new DtoOptions { Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio } };
        var dtos = _dto.GetBaseItemDtos(items, options, user)
            .OrderByDescending(d => d.UserData?.LastPlayedDate ?? DateTime.MinValue)
            .Take(24)
            .ToList();
        return new QueryResult<BaseItemDto>(dtos);
    }

    /// <summary>
    /// Wo sich ein Titel der Mediathek als Datei abholen lässt: die beste Fassung, durch den Server gereicht (mit
    /// Teilabrufen, also fortsetzbar). Apps mit Download-Verwaltung laden so von ihrem Server wie bei jedem anderen Titel.
    /// </summary>
    [HttpGet("download")]
    [Authorize]
    public async Task<ActionResult> Download([FromQuery] Guid id, CancellationToken ct)
    {
        // Die Kennung des Titels im Kanal ist „c|…“ oder „m|…“ mit der Kennung des Videos in der Mediathek.
        var external = _library.GetItemById(id)?.ExternalId ?? string.Empty;
        var cut = external.IndexOf('|', StringComparison.Ordinal);
        if (cut <= 0 || external[..cut] is not ("c" or "m" or "v"))
        {
            return NotFound();
        }

        var best = await Best(external[(cut + 1)..], int.MaxValue, ct).ConfigureAwait(false);
        if (best is null)
        {
            return NotFound();
        }

        return new JsonResult(new { path = "Mediathek/" + LiveProxy.VodPath(best.Url), container = "mp4", bitrate = MediathekChannel.BitrateFor(best.Width) });
    }

    /// <summary>
    /// Spielt ein Video, dessen Adresse beim Sender erst jetzt herausgesucht wird: Listen langer Sendungen nennen nur die
    /// Kennung des Videos. Je nach Einstellung läuft die Datei durch den Server oder der Abruf wird zum Sender umgeleitet.
    /// </summary>
    [HttpGet("play/{signature}/{width:int}/{name}")]
    [HttpHead("play/{signature}/{width:int}/{name}")]
    [AllowAnonymous]
    public async Task Play([FromRoute] string signature, [FromRoute] int width, [FromRoute] string name, CancellationToken ct)
    {
        var videoId = name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(LiveProxy.PlaySignature(videoId)), Encoding.ASCII.GetBytes(signature)))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ArdStream? stream;
        try
        {
            stream = await Best(videoId, width, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        if (stream is null || !Uri.TryCreate(stream.Url, UriKind.Absolute, out var target))
        {
            // Abgelaufen oder von hier aus gesperrt.
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (Plugin.Instance?.Configuration.ViaServer ?? true)
        {
            await _proxy.RelayAsync(HttpContext, target, ct).ConfigureAwait(false);
        }
        else
        {
            Response.Headers["Cache-Control"] = "no-store";
            Response.Redirect(target.AbsoluteUri);
        }
    }

    /// <summary>Die Fassung eines Videos mit der gewünschten Bildbreite, sonst die nächstkleinere, sonst die kleinste.</summary>
    private async Task<ArdStream?> Best(string videoId, int width, CancellationToken ct)
    {
        var video = await _ard.VideoAsync(videoId, ct).ConfigureAwait(false);
        if (video is null || video.Streams.Count == 0)
        {
            return null;
        }

        return video.Streams.Where(s => s.Width <= width).MaxBy(s => s.Width) ?? video.Streams.MinBy(s => s.Width);
    }

    // Jellyfin legt den Inhalt eines Kanal-Ordners in einer Datei ab; öffnen zwei Anfragen zugleich Ordner desselben Kanals,
    // liest die eine, was die andere gerade schreibt. Das Öffnen läuft deshalb nacheinander.
    private static readonly SemaphoreSlim ListingGate = new(1, 1);

    /// <summary>
    /// Holt die Angaben zu den Einträgen vorab und gleichzeitig. Das anschließende Öffnen des Ordners findet sie dann im
    /// Speicher und braucht nur noch einen Augenblick, obwohl es nacheinander läuft.
    /// </summary>
    private async Task Prefetch(IReadOnlyList<ArdTeaser> teasers, CancellationToken ct)
    {
        await Task.WhenAll(teasers.Select(async t =>
        {
            try
            {
                if (t.IsShow)
                {
                    await _ard.ShowAsync(t.Id, 0, ct).ConfigureAwait(false);
                }
                else
                {
                    await _ard.VideoAsync(t.Id, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Der Eintrag fehlt dann in der Liste; das Öffnen des Ordners versucht es noch einmal.
            }
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// Öffnet im Kanal einen Ordner und liefert seinen Inhalt in der Reihenfolge der Mediathek. Der Ordner ist das Ende
    /// von <paramref name="path"/> (Kennungen des Plugins, von der obersten Ebene abwärts) oder, falls angegeben,
    /// <paramref name="folderId"/> (Kennung in Jellyfin).
    /// </summary>
    /// <remarks>
    /// Jellyfin führt jeden Titel eines Kanals in genau einem Ordner, dem zuletzt gelesenen. Steht eine Sendung zugleich
    /// in einer Reihe der Startseite und in einer Suche, fehlte sie deshalb in einer von beiden. Die Titel werden hier
    /// darum nicht über ihren Ordner gesucht, sondern einzeln über ihre Kennung, wie sie der Kanal für diesen Ordner meldet.
    /// </remarks>
    private async Task<ActionResult<QueryResult<BaseItemDto>>> Listing(IReadOnlyList<string> path, Guid? folderId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || _users.GetUserById(userId) is not { } user)
        {
            return Unauthorized();
        }

        var all = await _channels.GetChannelsInternalAsync(new ChannelQuery { UserId = userId }).ConfigureAwait(false);
        var channel = all.Items.FirstOrDefault(c => string.Equals(c.Name, "Mediathek", StringComparison.Ordinal));
        if (channel is null)
        {
            return NotFound();
        }

        async Task<IReadOnlyList<BaseItem>> Children(Guid parent)
        {
            var listing = await _channels.GetChannelItemsInternal(
                new InternalItemsQuery(user) { ChannelIds = new[] { channel.Id }, ParentId = parent }, new Progress<double>(), ct).ConfigureAwait(false);
            return listing.Items;
        }

        await ListingGate.WaitAsync(ct).ConfigureAwait(false);
        IReadOnlyList<BaseItem> content;
        try
        {
            var parent = Guid.Empty;
            string? external = null;
            if (folderId is { } wanted)
            {
                if (_library.GetItemById(wanted) is not { } folder || !folder.ChannelId.Equals(channel.Id) || string.IsNullOrEmpty(folder.ExternalId))
                {
                    return NotFound();
                }

                parent = folder.Id;
                external = folder.ExternalId;
            }

            foreach (var step in path)
            {
                var folder = (await Children(parent).ConfigureAwait(false)).FirstOrDefault(i => string.Equals(i.ExternalId, step, StringComparison.OrdinalIgnoreCase));
                if (folder is null)
                {
                    return new QueryResult<BaseItemDto>();
                }

                parent = folder.Id;
                external = step;
            }

            // Lässt Jellyfin die Titel anlegen, falls es den Ordner noch nicht (oder länger nicht) gelesen hat.
            content = await Children(parent).ConfigureAwait(false);
            try
            {
                var wantedIds = (await _mediathek.ItemsFor(external, ct).ConfigureAwait(false)).Select(_mediathek.ItemGuid).Distinct().ToList();
                List<BaseItem> Known() => wantedIds.Select(id => _library.GetItemById(id)).Where(i => i is not null).Select(i => i!).ToList();
                var known = Known();
                if (known.Count < wantedIds.Count)
                {
                    // Ein Titel fehlt ganz: ein anderer Ordner hatte ihn übernommen und inzwischen wieder abgegeben.
                    // Jellyfin liest diesen Ordner dann noch einmal frisch und legt ihn neu an.
                    MediathekChannel.Fresh = Guid.NewGuid().ToString("N");
                    try
                    {
                        await Children(parent).ConfigureAwait(false);
                    }
                    finally
                    {
                        MediathekChannel.Fresh = null;
                    }

                    known = Known();
                }

                content = known;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Die Mediathek antwortet gerade nicht: dann bleibt es bei dem, was Jellyfin zu diesem Ordner kennt.
            }
        }
        finally
        {
            ListingGate.Release();
        }

        var options = new DtoOptions { Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio } };
        var dtos = _dto.GetBaseItemDtos(content.ToList(), options, user);
        return new QueryResult<BaseItemDto>(dtos);
    }

    /// <summary>Trägt Senderliste und Programm im Live-TV von Jellyfin ein (einmalig; erneutes Aufrufen ändert nichts).</summary>
    /// <summary>Welche Sender dieser Server empfängt und woran es bei den übrigen liegt.</summary>
    [HttpGet("live/status")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> LiveStatus(CancellationToken ct) =>
        new JsonResult(new { channels = await _live.GetStatusAsync(ct).ConfigureAwait(false) });

    /// <summary>Bereitet die Sender für schnelles Einschalten vor (siehe <see cref="LiveWarmup"/>); läuft sonst von selbst.</summary>
    [HttpPost("live/warmup")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> WarmupLive(CancellationToken ct) =>
        new JsonResult(new { prepared = await _warmup.RunAsync(ct).ConfigureAwait(false) });

    [HttpPost("live/setup")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> SetupLive(CancellationToken ct)
    {
        // Jellyfin holt beide Adressen von sich selbst.
        var root = _host.GetApiUrlForLocalAccess().TrimEnd('/');
        var playlist = root + "/Mediathek/live.m3u";
        var guide = root + "/Mediathek/guide.xml";

        // Schon eingerichtet? Dann werden die vorhandenen Einträge aufgefrischt statt verdoppelt.
        var existing = _config.GetConfiguration<LiveTvOptions>("livetv");
        var oldTuner = existing.TunerHosts?.FirstOrDefault(t => (t.Url ?? string.Empty).EndsWith("/Mediathek/live.m3u", StringComparison.OrdinalIgnoreCase));
        var oldProvider = existing.ListingProviders?.FirstOrDefault(p => (p.Path ?? string.Empty).EndsWith("/Mediathek/guide.xml", StringComparison.OrdinalIgnoreCase));

        var tuner = new TunerHostInfo
        {
            Id = oldTuner?.Id,
            Type = "m3u",
            Url = playlist,
            FriendlyName = "Mediathek",
            // Die Sender liefern HLS in mehreren Qualitäten; Jellyfin soll sie nicht mehrfach gleichzeitig begrenzen.
            TunerCount = 0,
            AllowHWTranscoding = true,
            EnableStreamLooping = false,
            // Bild und Ton kommen bei den Sendern als getrennte Streams. Mit „DTS ignorieren“ (Jellyfins Voreinstellung für
            // M3U-Tuner) verwirft ffmpeg dabei die Tonpakete: das Bild läuft, der Ton fehlt.
            IgnoreDts = false,
            ImportFavoritesOnly = false,
        };
        await _tuners.SaveTunerHost(tuner, true).ConfigureAwait(false);

        var provider = new ListingsProviderInfo
        {
            Id = oldProvider?.Id,
            Type = "xmltv",
            Path = guide,
            EnableAllTuners = true,
        };
        await _listings.SaveListingProvider(provider, false, true).ConfigureAwait(false);

        return new JsonResult(new { playlist, guide });
    }
}
