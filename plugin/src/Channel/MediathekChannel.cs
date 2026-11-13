using System.Globalization;
using Jellyfin.Plugin.Mediathek.Mvw;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Channel;

/// <summary>
/// Die Mediatheken als Jellyfin-Kanal: „Neu“, „Sender“ (je Sender die Sendungen, darin die Beiträge) und „Suche“
/// (je gemerktem Suchbegriff ein Ordner). Abgespielt wird die Datei des Senders direkt.
/// </summary>
public class MediathekChannel : IChannel, IHasCacheKey
{
    private const char Sep = '|';
    private static readonly DateTime SourcesChanged = new(2026, 11, 13, 19, 0, 0, DateTimeKind.Utc);

    // Name in der Filmliste, Anzeigename, Ausland.
    private static readonly (string Key, string Name, bool Foreign)[] Senders =
    {
        ("ARD", "Das Erste", false), ("ZDF", "ZDF", false), ("3Sat", "3sat", false), ("ARTE.DE", "arte", false),
        ("KiKA", "KiKA", false), ("PHOENIX", "phoenix", false), ("ZDFneo", "ZDFneo", false), ("ZDFinfo", "ZDFinfo", false),
        ("ONE", "ONE", false), ("tagesschau24", "tagesschau24", false), ("ARD-alpha", "ARD alpha", false),
        ("Funk.net", "funk", false), ("ZDF-tivi", "ZDFtivi", false),
        ("BR", "BR", false), ("HR", "hr", false), ("MDR", "MDR", false), ("NDR", "NDR", false), ("RBB", "rbb", false),
        ("rbtv", "Radio Bremen", false), ("SR", "SR", false), ("SWR", "SWR", false), ("WDR", "WDR", false), ("DW", "Deutsche Welle", false),
        ("ORF", "ORF", true), ("SRF", "SRF", true),
    };

    private static readonly string[] AccessibilityMarks = { "Audiodeskription", "Gebärdensprache", "Hörfassung", "Hörfilm", "(AD)", "Klare Sprache" };
    private static readonly string[] TrailerMarks = { "Trailer", "Vorschau:", "Teaser" };

    private readonly MvwClient _mvw;
    private readonly ILogger<MediathekChannel> _logger;

    public MediathekChannel(MvwClient mvw, ILogger<MediathekChannel> logger)
    {
        _mvw = mvw;
        _logger = logger;
    }

    public string Name => "Mediathek";

    public string Description => "Sendungen der öffentlich-rechtlichen Sender.";

    public string DataVersion => "6";

    public string HomePageUrl => "https://mediathekviewweb.de";

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Ändert sich mit den Einstellungen, damit Jellyfin seine gemerkten Ordnerinhalte verwirft.</summary>
    public string? GetCacheKey(string? userId)
    {
        var c = Config;
        return string.Join(Sep, c.HideAccessibilityVersions, c.HideTrailers, c.MinimumMinutesInNew, c.IncludeForeign, string.Join(',', c.Searches));
    }

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
        DefaultSortFields = new List<ChannelItemSortField> { ChannelItemSortField.DateCreated, ChannelItemSortField.Name },
        SupportsSortOrderToggle = false,
        SupportsContentDownloading = true,
    };

    public bool IsEnabledFor(string userId) => true;

    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var items = await ItemsFor(query.FolderId, cancellationToken).ConfigureAwait(false);
            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Mediathek: Ordner {Folder} ließ sich nicht laden", query.FolderId);
            return new ChannelItemResult { Items = new List<ChannelItemInfo>(), TotalRecordCount = 0 };
        }
    }

    private async Task<List<ChannelItemInfo>> ItemsFor(string? folder, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return new List<ChannelItemInfo>
            {
                Folder("neu", "Neu in den Mediatheken", "Die neuesten Sendungen aller Sender."),
                Folder("sender", "Sender", "Nach Sender und Sendung stöbern."),
                Folder("suche", "Suche", "Gemerkte Suchbegriffe. Neue Begriffe lassen sich in den Einstellungen des Plugins anlegen."),
            };
        }

        var parts = folder.Split(Sep, 3);
        switch (parts[0])
        {
            case "neu":
            {
                var (found, _) = await _mvw.QueryAsync(null, null, null, 0, MvwClient.MaxPageSize, false, ct).ConfigureAwait(false);
                var minimum = Math.Max(0, Config.MinimumMinutesInNew) * 60;
                return found.Where(e => Wanted(e) && e.Duration >= minimum).Take(300).Select(e => Media(e, withTopic: true)).ToList();
            }

            case "sender":
                return Senders.Where(s => Config.IncludeForeign || !s.Foreign)
                    .Select(s => Folder("s" + Sep + s.Key, s.Name, null)).ToList();

            case "s" when parts.Length >= 2:
            {
                // Die Sendungen eines Senders ergeben sich aus seinen neuesten Beiträgen.
                var all = new List<MvwEntry>();
                for (var page = 0; page < 3; page++)
                {
                    var (found, total) = await _mvw.QueryAsync(parts[1], null, null, page * MvwClient.MaxPageSize, MvwClient.MaxPageSize, false, ct).ConfigureAwait(false);
                    all.AddRange(found);
                    if ((page + 1) * MvwClient.MaxPageSize >= total || found.Count == 0)
                    {
                        break;
                    }
                }

                var result = new List<ChannelItemInfo> { Folder("sn" + Sep + parts[1], "Neueste Beiträge", null) };
                result.AddRange(all.Where(Wanted).Where(e => e.Topic.Length > 0)
                    .GroupBy(e => e.Topic, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true))
                    .Select(g => Folder("t" + Sep + parts[1] + Sep + g.Key, g.Key, null)));
                return result;
            }

            case "sn" when parts.Length >= 2:
            {
                var (found, _) = await _mvw.QueryAsync(parts[1], null, null, 0, MvwClient.MaxPageSize, false, ct).ConfigureAwait(false);
                return found.Where(Wanted).Take(300).Select(e => Media(e, withTopic: true)).ToList();
            }

            case "t" when parts.Length >= 3:
            {
                var (found, _) = await _mvw.QueryAsync(parts[1], parts[2], null, 0, MvwClient.MaxPageSize, false, ct).ConfigureAwait(false);
                return found.Where(Wanted).Take(500).Select(e => Media(e, withTopic: false)).ToList();
            }

            case "suche":
                return Config.Searches.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(s => Folder("q" + Sep + s, s, "Treffer für „" + s + "“, neueste zuerst.")).ToList();

            case "q" when parts.Length >= 2:
            {
                var (found, _) = await _mvw.QueryAsync(null, null, folder[(folder.IndexOf(Sep) + 1)..], 0, MvwClient.MaxPageSize, false, ct).ConfigureAwait(false);
                return found.Where(Wanted).Take(400).Select(e => Media(e, withTopic: true)).ToList();
            }
        }

        return new List<ChannelItemInfo>();
    }

    private static bool Wanted(MvwEntry e)
    {
        var c = Config;
        if (!c.IncludeForeign && Senders.Any(s => s.Foreign && string.Equals(s.Key, e.Channel, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (c.HideAccessibilityVersions && AccessibilityMarks.Any(m => e.Title.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (c.HideTrailers && TrailerMarks.Any(m => e.Title.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return e.Duration <= 0 || e.Duration > 45;   // Schnipsel unter einer Dreiviertelminute sind fast immer Hinweise
    }

    private static ChannelItemInfo Folder(string id, string name, string? overview) => new()
    {
        Id = id,
        Name = name,
        Overview = overview,
        Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container,
    };

    private static List<MediaSourceInfo> Sources(string entryId, int duration, string hd, string standard, string low)
    {
        var ticks = duration > 0 ? TimeSpan.FromSeconds(duration).Ticks : (long?)null;
        var sources = new List<MediaSourceInfo>();
        void Add(string url, string label, int width, int height)
        {
            if (!MvwClient.IsWebUrl(url) || sources.Any(s => s.Path == url))
            {
                return;
            }

            var hls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            sources.Add(new MediaSourceInfo
            {
                // Bewusst keine Kennung in GUID-Form: solche hält Jellyfin für andere Fassungen aus der Bibliothek
                // und verwirft sie, wenn es dort keinen Titel dazu gibt.
                Id = "mediathek-" + label.ToLowerInvariant() + "-" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(entryId))).ToLowerInvariant()[..16],
                Name = label,
                Path = url,
                Protocol = MediaProtocol.Http,
                Container = hls ? "hls" : "mp4",
                IsRemote = true,
                RunTimeTicks = ticks,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = false,
                // Die Mediatheken liefern durchweg H.264 mit AAC in Stereo. Ohne diese Angabe hielte Jellyfin die Datei für
                // unbekannt und würde sie für jeden Client umrechnen, statt sie durchzureichen.
                MediaStreams = new List<MediaStream>
                {
                    new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Profile = "High", Width = width, Height = height, IsInterlaced = false, PixelFormat = "yuv420p", BitDepth = 8 },
                    new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Profile = "LC", Channels = 2, SampleRate = 48000, Language = "deu", IsDefault = true },
                },
            });
        }

        Add(hd, "HD", 1920, 1080);
        Add(standard, "Standard", 1280, 720);
        Add(low, "Sparsam", 640, 360);
        return sources;
    }

    /// <summary>Baut aus einem Beitrag einen abspielbaren Titel mit bis zu drei Qualitäten.</summary>
    public static ChannelItemInfo Media(MvwEntry e, bool withTopic)
    {
        var sender = Senders.FirstOrDefault(s => string.Equals(s.Key, e.Channel, StringComparison.OrdinalIgnoreCase)).Name ?? e.Channel;
        var when = e.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.Timestamp).UtcDateTime : (DateTime?)null;
        var name = withTopic && e.Topic.Length > 0 && !e.Title.Contains(e.Topic, StringComparison.OrdinalIgnoreCase)
            ? e.Topic + ": " + e.Title
            : e.Title;
        var ticks = e.Duration > 0 ? TimeSpan.FromSeconds(e.Duration).Ticks : (long?)null;

        var sources = Sources(e.Id, e.Duration, e.UrlVideoHd, e.UrlVideo, e.UrlVideoLow);

        var overview = e.Description;
        var line = sender + (when.HasValue ? " · " + when.Value.ToLocalTime().ToString("d. MMMM yyyy", CultureInfo.GetCultureInfo("de-DE")) : string.Empty);
        overview = overview.Length > 0 ? overview + "\n\n" + line : line;

        return new ChannelItemInfo
        {
            Id = "v" + Sep + e.Id,
            Name = name,
            SeriesName = e.Topic.Length > 0 ? e.Topic : null,
            Overview = overview,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            RunTimeTicks = ticks,
            PremiereDate = when,
            DateCreated = when,
            ProductionYear = when?.Year,
            Studios = new List<string> { sender },
            HomePageUrl = MvwClient.IsWebUrl(e.UrlWebsite) ? e.UrlWebsite : null,
            MediaSources = sources,
            // Jellyfin legt einen schon bekannten Titel nur neu ab, wenn dieses Datum jünger ist als das gemerkte.
            // Es wird hochgesetzt, sobald sich der Aufbau der Quellen ändert.
            DateModified = SourcesChanged,
        };
    }
}
