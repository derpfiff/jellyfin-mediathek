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

    public MediathekController(LiveService live, LiveProxy proxy, ArdClient ard, IDtoService dto, IChannelManager channels, IUserManager users, IServerApplicationHost host, ITunerHostManager tuners, IListingsManager listings, IConfigurationManager config)
    {
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
        return await Listing(new[] { MediathekChannel.RowId(row.Id) }, row.Teasers.Select(t => t.Id).ToList(), ct).ConfigureAwait(false);
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
        var remembered = config.Searches.Where(s => !string.Equals(s, q, StringComparison.OrdinalIgnoreCase)).Prepend(q).Take(RememberedSearches).ToArray();
        if (!remembered.SequenceEqual(config.Searches))
        {
            config.Searches = remembered;
            plugin.SaveConfiguration();
        }

        var found = await _ard.SearchAsync(q, 12, 36, ct).ConfigureAwait(false);
        await Prefetch(found, ct).ConfigureAwait(false);
        var order = found.Select(t => t.Id).ToList();
        return await Listing(new[] { MediathekChannel.SearchFolder, MediathekChannel.SearchId(q) }, order, ct).ConfigureAwait(false);
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
    /// Öffnet im Kanal den Ordner am Ende des Pfads (Kennungen des Plugins, von der obersten Ebene abwärts) und liefert seinen
    /// Inhalt. <paramref name="order"/> nennt die Kennungen der Mediathek in der gewünschten Reihenfolge.
    /// </summary>
    private async Task<ActionResult<QueryResult<BaseItemDto>>> Listing(IReadOnlyList<string> path, IReadOnlyList<string> order, CancellationToken ct)
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
            foreach (var step in path)
            {
                var folder = (await Children(parent).ConfigureAwait(false)).FirstOrDefault(i => string.Equals(i.ExternalId, step, StringComparison.OrdinalIgnoreCase));
                if (folder is null)
                {
                    return new QueryResult<BaseItemDto>();
                }

                parent = folder.Id;
            }

            content = await Children(parent).ConfigureAwait(false);
        }
        finally
        {
            ListingGate.Release();
        }

        // Jellyfin sortiert nach Namen; maßgeblich ist die Reihenfolge der Mediathek.
        var rank = order.Select((id, index) => (id, index)).GroupBy(p => p.id).ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        int Rank(BaseItem item)
        {
            var external = item.ExternalId ?? string.Empty;
            var cut = external.IndexOf('|', StringComparison.Ordinal);
            return rank.TryGetValue(cut >= 0 ? external[(cut + 1)..] : external, out var index) ? index : int.MaxValue;
        }

        var items = content.OrderBy(Rank).ToList();
        var options = new DtoOptions { Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio } };
        var dtos = _dto.GetBaseItemDtos(items, options, user);
        return new QueryResult<BaseItemDto>(dtos);
    }

    /// <summary>Trägt Senderliste und Programm im Live-TV von Jellyfin ein (einmalig; erneutes Aufrufen ändert nichts).</summary>
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
