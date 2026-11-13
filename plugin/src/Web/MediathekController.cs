using System.Text;
using Jellyfin.Plugin.Mediathek.Live;
using Jellyfin.Plugin.Mediathek.Mvw;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Mediathek.Web;

/// <summary>Senderliste und Programm für das Live-TV von Jellyfin, dazu Suche und Einrichtung.</summary>
[ApiController]
[Route("Mediathek")]
public class MediathekController : ControllerBase
{
    private readonly LiveService _live;
    private readonly MvwClient _mvw;
    private readonly IServerApplicationHost _host;
    private readonly ITunerHostManager _tuners;
    private readonly IListingsManager _listings;

    public MediathekController(LiveService live, MvwClient mvw, IServerApplicationHost host, ITunerHostManager tuners, IListingsManager listings)
    {
        _live = live;
        _mvw = mvw;
        _host = host;
        _tuners = tuners;
        _listings = listings;
    }

    // Beide Adressen ruft Jellyfin selbst ab, ohne Anmeldung. Sie enthalten nur öffentliche Angaben der Sender.
    [HttpGet("live.m3u")]
    [AllowAnonymous]
    public async Task<ActionResult> GetPlaylist(CancellationToken ct) =>
        Content(await _live.GetPlaylistAsync(ct).ConfigureAwait(false), "audio/x-mpegurl", Encoding.UTF8);

    [HttpGet("guide.xml")]
    [AllowAnonymous]
    public async Task<ActionResult> GetGuide(CancellationToken ct) =>
        Content(await _live.GetGuideAsync(ct).ConfigureAwait(false), "application/xml", Encoding.UTF8);

    /// <summary>Sucht in den Mediatheken; für Oberflächen, die die Treffer selbst darstellen.</summary>
    [HttpGet("search")]
    [Authorize]
    public async Task<ActionResult> Search([FromQuery] string q, [FromQuery] int offset = 0, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        q = (q ?? string.Empty).Trim();
        if (q.Length is 0 or > 200)
        {
            return BadRequest();
        }

        var (items, total) = await _mvw.QueryAsync(null, null, q, offset, Math.Clamp(limit, 1, 200), false, ct).ConfigureAwait(false);
        return new JsonResult(new { total, items });
    }

    /// <summary>Merkt einen Suchbegriff: er erscheint danach als Ordner unter „Suche“.</summary>
    [HttpPost("searches")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult AddSearch([FromQuery] string q)
    {
        q = (q ?? string.Empty).Trim();
        var plugin = Plugin.Instance;
        if (plugin is null || q.Length is 0 or > 200 || q.Contains('|', StringComparison.Ordinal))
        {
            return BadRequest();
        }

        var config = plugin.Configuration;
        if (!config.Searches.Contains(q, StringComparer.OrdinalIgnoreCase))
        {
            config.Searches = config.Searches.Append(q).ToArray();
            plugin.SaveConfiguration();
        }

        return NoContent();
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

        var tuner = new TunerHostInfo
        {
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
            Type = "xmltv",
            Path = guide,
            EnableAllTuners = true,
        };
        await _listings.SaveListingProvider(provider, false, true).ConfigureAwait(false);

        return new JsonResult(new { playlist, guide });
    }
}
