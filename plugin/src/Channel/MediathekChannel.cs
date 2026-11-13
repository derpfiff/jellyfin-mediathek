using System.Globalization;
using Jellyfin.Plugin.Mediathek.Ard;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Channel;

/// <summary>
/// Die ARD Mediathek als Jellyfin-Kanal. Die oberste Ebene zeigt die Reihen ihrer Startseite („Empfehlungen“,
/// „Unsere Top-Serien“ …) und den Ordner „Suche“. Sendungen erscheinen als Serien mit Staffeln und Folgen, Filme und
/// Einzelbeiträge als Titel mit Bild, Beschreibung und Altersfreigabe. Abgespielt wird die Datei des Senders direkt.
/// </summary>
public class MediathekChannel : IChannel, IHasCacheKey
{
    /// <summary>Kennung des Ordners „Suche“ innerhalb des Kanals.</summary>
    public const string SearchFolder = "suche";

    /// <summary>Kennung des Ordners einer Startseiten-Reihe.</summary>
    public static string RowId(string widgetId) => "w" + Sep + widgetId;

    private const char Sep = '|';

    /// <summary>Damit beginnen die Kennungen aller Medienquellen dieses Kanals.</summary>
    public const string SourcePrefix = "mediathek-";

    /// <summary>Wird hochgesetzt, sobald sich der Aufbau der Titel ändert; Jellyfin legt bekannte Titel dann neu ab.</summary>
    private static readonly DateTime LayoutChanged = new(2026, 11, 13, 23, 45, 0, DateTimeKind.Utc);

    private readonly ArdClient _ard;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<MediathekChannel> _logger;

    public MediathekChannel(ArdClient ard, IServerApplicationHost host, ILogger<MediathekChannel> logger)
    {
        _ard = ard;
        _host = host;
        _logger = logger;
    }

    /// <summary>
    /// Adresse, unter der Jellyfin ein Bild der Mediathek abholt: beim Plugin selbst, das die passende Größe besorgt.
    /// </summary>
    private string? Picture(string template, int width) =>
        template.Length == 0 ? null : _host.GetApiUrlForLocalAccess().TrimEnd('/') + "/Mediathek/" + Live.LiveProxy.ImagePath(template, width);

    public string Name => "Mediathek";

    public string Description => "Sendungen, Serien und Filme aus der ARD Mediathek.";

    public string DataVersion => "14";

    public string HomePageUrl => "https://www.ardmediathek.de";

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Kennung des Ordners für einen Suchbegriff.</summary>
    public static string SearchId(string term) => "q" + Sep + term.Trim();

    /// <summary>
    /// Jellyfin merkt sich den Inhalt eines Ordners mehrere Stunden. Der Schlüssel wechselt stündlich, damit die Startseite
    /// der Mediathek frisch bleibt, und sofort, wenn ein Suchbegriff dazukommt.
    /// </summary>
    public string? GetCacheKey(string? userId) =>
        DateTime.UtcNow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture) + Sep + string.Join(',', Config.Searches);

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Episode, ChannelMediaContentType.Movie, ChannelMediaContentType.Clip },
        DefaultSortFields = new List<ChannelItemSortField> { ChannelItemSortField.PremiereDate, ChannelItemSortField.Name },
        SupportsSortOrderToggle = true,
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Mediathek: Ordner {Folder} ließ sich nicht laden", query.FolderId);
            return new ChannelItemResult { Items = new List<ChannelItemInfo>(), TotalRecordCount = 0 };
        }
    }

    private async Task<List<ChannelItemInfo>> ItemsFor(string? folder, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(folder))
        {
            var rows = await _ard.HomeAsync(ct).ConfigureAwait(false);
            var result = rows.Take(18).Select(r => Folder("w" + Sep + r.Id, r.Title, null)).ToList();
            result.Add(Folder(SearchFolder, "Suche", "Zuletzt gesuchte Begriffe."));
            return result;
        }

        var parts = folder.Split(Sep, 3);
        switch (parts[0])
        {
            case "w" when parts.Length >= 2:
            {
                var rows = await _ard.HomeAsync(ct).ConfigureAwait(false);
                var row = rows.FirstOrDefault(r => r.Id == parts[1]);
                return row is null ? new List<ChannelItemInfo>() : await Build(row.Teasers, ct).ConfigureAwait(false);
            }

            case SearchFolder:
                return Config.Searches.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(s => Folder(SearchId(s), s, "Treffer für „" + s + "“.")).ToList();

            case "q" when parts.Length >= 2:
            {
                var found = await _ard.SearchAsync(folder[(folder.IndexOf(Sep) + 1)..], 12, 36, ct).ConfigureAwait(false);
                return await Build(found, ct).ConfigureAwait(false);
            }

            case "show" when parts.Length >= 2:
            {
                var show = await _ard.ShowAsync(parts[1], 240, ct).ConfigureAwait(false);
                if (show is null)
                {
                    return new List<ChannelItemInfo>();
                }

                var seasons = show.Episodes.Select(e => e.Season).Where(s => s.HasValue).Select(s => s!.Value).Distinct().OrderBy(s => s).ToList();
                if (seasons.Count > 1)
                {
                    return seasons.Select(s => new ChannelItemInfo
                    {
                        Id = "season" + Sep + parts[1] + Sep + s.ToString(CultureInfo.InvariantCulture),
                        Name = "Staffel " + s.ToString(CultureInfo.InvariantCulture),
                        SeriesName = show.Title,
                        IndexNumber = s,
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Season,
                        ImageUrl = Picture(show.ImagePoster, 640),
                        DateModified = LayoutChanged,
                    }).ToList();
                }

                return await Episodes(show, show.Episodes, ct).ConfigureAwait(false);
            }

            case "season" when parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var season):
            {
                var show = await _ard.ShowAsync(parts[1], 240, ct).ConfigureAwait(false);
                return show is null
                    ? new List<ChannelItemInfo>()
                    : await Episodes(show, show.Episodes.Where(e => e.Season == season).ToList(), ct).ConfigureAwait(false);
            }
        }

        return new List<ChannelItemInfo>();
    }

    /// <summary>Macht aus einer gemischten Liste Serien-Ordner und abspielbare Titel.</summary>
    private async Task<List<ChannelItemInfo>> Build(IReadOnlyList<ArdTeaser> teasers, CancellationToken ct)
    {
        var tasks = teasers.Select(async t =>
        {
            try
            {
                if (t.IsShow)
                {
                    // Beschreibung und Plakat stehen auf der Seite der Sendung; für die Liste genügt deren erste Seite.
                    var show = await _ard.ShowAsync(t.Id, 0, ct).ConfigureAwait(false);
                    return show is null ? null : new ChannelItemInfo
                    {
                        Id = "show" + Sep + t.Id,
                        Name = show.Title,
                        Overview = Join(t.Tagline, show.Synopsis),
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Series,
                        ImageUrl = Picture(show.ImagePoster.Length > 0 ? show.ImagePoster : (t.ImagePoster.Length > 0 ? t.ImagePoster : show.ImageWide), 640),
                        Studios = show.Publisher.Length > 0 ? new List<string> { show.Publisher } : new List<string>(),
                        DateModified = LayoutChanged,
                    };
                }

                var video = await _ard.VideoAsync(t.Id, ct).ConfigureAwait(false);
                return video is null ? null : Media(t, video, asEpisodeOf: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Mediathek: {Title} ließ sich nicht laden", t.Title);
                return null;
            }
        }).ToList();

        var built = await Task.WhenAll(tasks).ConfigureAwait(false);
        return built.Where(i => i is not null).Select(i => i!).ToList();
    }

    private async Task<List<ChannelItemInfo>> Episodes(ArdShow show, IReadOnlyList<ArdTeaser> episodes, CancellationToken ct)
    {
        var tasks = episodes.Select(async t =>
        {
            try
            {
                var video = await _ard.VideoAsync(t.Id, ct).ConfigureAwait(false);
                return video is null ? null : Media(t, video, asEpisodeOf: show);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Mediathek: Folge {Title} ließ sich nicht laden", t.Title);
                return null;
            }
        }).ToList();

        var built = await Task.WhenAll(tasks).ConfigureAwait(false);
        return built.Where(i => i is not null).Select(i => i!).ToList();
    }

    private static ChannelItemInfo Folder(string id, string name, string? overview) => new()
    {
        Id = id,
        Name = name,
        Overview = overview,
        Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container,
        DateModified = LayoutChanged,
    };

    /// <summary>
    /// Die Fassungen eines Videos als Medienquellen; sie zeigen auf den Sender. Soll die Wiedergabe über den Server laufen,
    /// stellt <see cref="Web.ViaServerFilter"/> die Adressen um, sobald ein Client danach fragt.
    /// </summary>
    private static List<MediaSourceInfo> Sources(ArdVideo video, long? ticks) =>
        video.Streams.Select(s => new MediaSourceInfo
        {
            // Bewusst keine Kennung in GUID-Form: solche hält Jellyfin für andere Fassungen aus der Bibliothek und
            // verwirft sie, wenn es dort keinen Titel dazu gibt.
            Id = SourcePrefix + s.Width.ToString(CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(video.Id))).ToLowerInvariant()[..16],
            Name = s.Label.Length > 0 ? s.Label : s.Width.ToString(CultureInfo.InvariantCulture) + " px",
            Path = s.Url,
            Protocol = MediaProtocol.Http,
            Container = "mp4",
            IsRemote = true,
            RunTimeTicks = ticks,
            // Clients holen die Datei unter dieser Adresse selbst. (Als „nicht direkt abspielbar“ markiert würde Jellyfin sie
            // nicht durchreichen, sondern für jeden Client neu kodieren.)
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = false,
            // Die Mediathek liefert H.264 mit AAC in Stereo. Ohne diese Angabe hielte Jellyfin die Datei für unbekannt und
            // würde sie für jeden Client umrechnen, statt sie durchzureichen.
            MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Profile = "High", Width = s.Width > 0 ? s.Width : null, Height = s.Height > 0 ? s.Height : null, IsInterlaced = false, PixelFormat = "yuv420p", BitDepth = 8 },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Profile = "LC", Channels = 2, SampleRate = 48000, Language = "deu", IsDefault = true },
            },
        }).ToList();

    /// <summary>Baut einen abspielbaren Titel. Ohne Videoadresse (Ländersperre, abgelaufen) entsteht keiner.</summary>
    private ChannelItemInfo? Media(ArdTeaser teaser, ArdVideo video, ArdShow? asEpisodeOf)
    {
        if (video.Streams.Count == 0)
        {
            return null;
        }

        var duration = video.Duration > 0 ? video.Duration : teaser.Duration;
        var ticks = duration > 0 ? TimeSpan.FromSeconds(duration).Ticks : (long?)null;
        var date = video.Date ?? teaser.Date;
        var (title, season, episode) = ArdClient.SplitEpisodeMark(video.Title.Length > 0 ? video.Title : teaser.Title);
        season ??= teaser.Season;
        episode ??= teaser.Episode;
        var showTitle = asEpisodeOf?.Title ?? (video.ShowTitle.Length > 0 ? video.ShowTitle : teaser.ShowTitle);
        // „Folge 1 | Babylon Berlin“: der Name der Sendung hinter dem Strich steht schon am Ordner.
        var bar = title.LastIndexOf(" | ", StringComparison.Ordinal);
        if (bar > 0 && showTitle.Length > 0 && title[(bar + 3)..].Trim().Equals(showTitle, StringComparison.OrdinalIgnoreCase))
        {
            title = title[..bar].Trim();
        }

        // Ein Film ist, was in der Mediathek als Plakat steht oder für sich allein; alles andere ist eine Folge oder ein Beitrag.
        var isEpisode = asEpisodeOf is not null;
        var isMovie = !isEpisode && (teaser.ImagePoster.Length > 0 || duration >= 60 * 60);
        // In gemischten Listen steht der Name der Sendung davor, damit „Folge 3“ nicht für sich allein steht.
        if (!isEpisode && !isMovie && showTitle.Length > 0 && !title.Contains(showTitle, StringComparison.OrdinalIgnoreCase))
        {
            title = showTitle + ": " + title;
        }

        var sources = Sources(video, ticks);

        var image = isMovie && teaser.ImagePoster.Length > 0 ? teaser.ImagePoster : (teaser.ImageWide.Length > 0 ? teaser.ImageWide : video.ImageWide);
        return new ChannelItemInfo
        {
            Id = "v" + Sep + video.Id,
            Name = title,
            SeriesName = isMovie ? null : Nullable(showTitle),
            Overview = Join(isEpisode ? string.Empty : teaser.Tagline, video.Synopsis),
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = isMovie ? ChannelMediaContentType.Movie : (isEpisode ? ChannelMediaContentType.Episode : ChannelMediaContentType.Clip),
            IndexNumber = isEpisode ? episode : null,
            ParentIndexNumber = isEpisode ? season : null,
            RunTimeTicks = ticks,
            PremiereDate = date,
            DateCreated = date,
            // Die Mediathek nennt, seit wann ein Titel dort steht, nicht wann er entstand: für Filme wäre das als Jahr falsch.
            ProductionYear = isMovie ? null : date?.Year,
            OfficialRating = Rating(video.Rating.Length > 0 ? video.Rating : teaser.Rating),
            Studios = video.Publisher.Length > 0 ? new List<string> { video.Publisher } : new List<string>(),
            ImageUrl = Picture(image, isMovie && teaser.ImagePoster.Length > 0 ? 640 : 960),
            HomePageUrl = "https://www.ardmediathek.de/video/" + Uri.EscapeDataString(video.Id),
            MediaSources = sources,
            DateModified = LayoutChanged,
        };
    }

    // „FSK12“ → „FSK-12“, wie Jellyfin deutsche Freigaben schreibt.
    private static string? Rating(string raw) =>
        raw.StartsWith("FSK", StringComparison.OrdinalIgnoreCase) && raw.Length > 3 && int.TryParse(raw[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var age)
            ? "FSK-" + age.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? Nullable(string value) => value.Length > 0 ? value : null;

    private static string? Join(string first, string second)
    {
        if (first.Length > 0 && second.Length > 0 && !second.Contains(first, StringComparison.OrdinalIgnoreCase))
        {
            return first + "\n\n" + second;
        }

        return second.Length > 0 ? second : Nullable(first);
    }
}
