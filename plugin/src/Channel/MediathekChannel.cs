using System.Globalization;
using Jellyfin.Plugin.Mediathek.Ard;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
    private static readonly DateTime LayoutChanged = new(2026, 11, 14, 18, 0, 0, DateTimeKind.Utc);

    private readonly ArdClient _ard;
    private readonly IServerApplicationHost _host;
    private readonly ILibraryManager _library;
    private readonly ILogger<MediathekChannel> _logger;

    /// <summary>Sendungen, deren Folgen gerade ihre Beschreibungen bekommen.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _describing = new();

    public MediathekChannel(ArdClient ard, IServerApplicationHost host, ILibraryManager library, ILogger<MediathekChannel> logger)
    {
        _ard = ard;
        _host = host;
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// Adresse, unter der Jellyfin ein Bild der Mediathek abholt: beim Plugin selbst, das die passende Größe besorgt.
    /// </summary>
    private string? Picture(string template, int width) =>
        template.Length == 0 ? null : _host.GetApiUrlForLocalAccess().TrimEnd('/') + "/Mediathek/" + Live.LiveProxy.ImagePath(template, width);

    public string Name => "Mediathek";

    public string Description => "Sendungen, Serien und Filme aus der ARD Mediathek.";

    public string DataVersion => "20";

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
        DateTime.UtcNow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture) + Sep + string.Join(',', Config.Searches) + Fresh;

    /// <summary>
    /// Solange gesetzt, gilt für Jellyfin kein Ordner als schon gelesen: es fragt den Kanal neu und legt fehlende Titel
    /// wieder an (siehe <see cref="Web.MediathekController"/>).
    /// </summary>
    internal static volatile string? Fresh;

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

    /// <summary>Der Inhalt eines Ordners, wie ihn der Kanal an Jellyfin meldet; <c>null</c> ist die oberste Ebene.</summary>
    public async Task<List<ChannelItemInfo>> ItemsFor(string? folder, CancellationToken ct)
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

            // Die Kennungen „sh“, „se“ und „m“ lösten „show“, „season“ und „v“ ab, als die Bilder auf Querformat wechselten:
            // Jellyfin behält das Bild eines bekannten Titels, unter neuer Kennung holt es das neue.
            case "sh" or "show" when parts.Length >= 2:
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
                        Id = "se" + Sep + parts[1] + Sep + s.ToString(CultureInfo.InvariantCulture),
                        Name = "Staffel " + s.ToString(CultureInfo.InvariantCulture),
                        SeriesName = show.Title,
                        IndexNumber = s,
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Season,
                        ImageUrl = Picture(show.ImageWide.Length > 0 ? show.ImageWide : show.ImagePoster, 960),
                        DateModified = LayoutChanged,
                    }).ToList();
                }

                return Episodes(show, show.Episodes);
            }

            case "se" or "season" when parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var season):
            {
                var show = await _ard.ShowAsync(parts[1], 240, ct).ConfigureAwait(false);
                return show is null
                    ? new List<ChannelItemInfo>()
                    : Episodes(show, show.Episodes.Where(e => e.Season == season).ToList());
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
                        Id = "sh" + Sep + t.Id,
                        Name = show.Title,
                        Overview = Join(t.Tagline, show.Synopsis),
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Series,
                        // Querformat: so zeigt die Mediathek ihre Sendungen selbst, und die Hochkant-Fassungen sind oft nur Ausschnitte.
                        ImageUrl = Picture(show.ImageWide.Length > 0 ? show.ImageWide : (t.ImageWide.Length > 0 ? t.ImageWide : show.ImagePoster), 960),
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

    /// <summary>
    /// Die Folgen einer Sendung, allein aus ihrer Übersichtsseite: Titel, Bild, Länge und Datum stehen dort. Die Seite
    /// jedes einzelnen Videos (mit den Videoadressen) wird erst beim Abspielen gelesen, siehe <see cref="Sources"/>;
    /// sonst kostete eine Sendung mit 240 Ausgaben 240 Abrufe, bevor die Liste erscheint.
    /// </summary>
    private List<ChannelItemInfo> Episodes(ArdShow show, IReadOnlyList<ArdTeaser> episodes)
    {
        Describe(show.Id, episodes);
        return episodes.Select(t => Media(t, null, asEpisodeOf: show)).Where(i => i is not null).Select(i => i!).ToList();
    }

    /// <summary>
    /// Die Beschreibung einer Folge steht nur auf der Seite ihres Videos. Sie wird nachgereicht, während die Liste längst
    /// angezeigt ist: im Hintergrund, mit zwei Abrufen zugleich, und nur für Folgen, die noch keine haben. Jellyfin
    /// übernimmt eine Beschreibung nur beim ersten Anlegen eines Titels, deshalb trägt das Plugin sie selbst ein.
    /// </summary>
    private void Describe(string showId, IReadOnlyList<ArdTeaser> episodes)
    {
        if (episodes.Count == 0 || !_describing.TryAdd(showId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Jellyfin legt die Titel an, sobald die Liste zurückgegeben ist; vorher gäbe es nichts zu ergänzen.
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                using var gate = new SemaphoreSlim(2, 2);
                await Task.WhenAll(episodes.Select(async t =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var item = _library.GetItemById(ItemGuid("c" + Sep + t.Id, false));
                        if (item is null || !string.IsNullOrWhiteSpace(item.Overview))
                        {
                            return;
                        }

                        var video = await _ard.VideoAsync(t.Id, CancellationToken.None).ConfigureAwait(false);
                        if (video is { Synopsis.Length: > 0 })
                        {
                            item.Overview = video.Synopsis;
                            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Mediathek: Beschreibung zu {Title} ließ sich nicht nachtragen", t.Title);
                    }
                    finally
                    {
                        gate.Release();
                    }
                })).ConfigureAwait(false);
            }
            finally
            {
                _describing.TryRemove(showId, out _);
            }
        });
    }

    /// <summary>
    /// Die Kennung, unter der Jellyfin einen Titel dieses Kanals führt; Jellyfin bildet sie aus der Kennung des Plugins,
    /// dem Namen des Kanals und der Art des Titels (Film oder Video).
    /// </summary>
    private Guid ItemGuid(string externalId, bool isMovie) =>
        _library.GetNewItemId(externalId + Name + "16", isMovie ? typeof(Movie) : typeof(Video));

    /// <summary>Die Kennung, unter der Jellyfin einen Eintrag dieses Kanals führt (Ordner, Sendung, Staffel oder Titel).</summary>
    public Guid ItemGuid(ChannelItemInfo info)
    {
        if (info.Type != ChannelItemType.Folder)
        {
            return ItemGuid(info.Id, info.ContentType == ChannelMediaContentType.Movie);
        }

        var type = info.FolderType switch
        {
            ChannelFolderType.Series => typeof(MediaBrowser.Controller.Entities.TV.Series),
            ChannelFolderType.Season => typeof(MediaBrowser.Controller.Entities.TV.Season),
            _ => typeof(MediaBrowser.Controller.Entities.Folder),
        };
        return _library.GetNewItemId(info.Id + Name + "16", type);
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
    /// Die Fassungen eines Videos als Medienquellen. Ihre Adressen zeigen auf das Plugin (<c>Mediathek/play/…</c>), das die
    /// Adresse beim Sender erst heraussucht, wenn jemand abspielt; fehlt eine Fassung, nimmt es die nächstkleinere. Ist die
    /// Seite des Videos noch nicht gelesen (<paramref name="video"/> fehlt), stehen hier die vier Fassungen, in denen die
    /// Mediathek ihre Videos durchweg anbietet.
    /// (Jellyfins Rückruf für Medienquellen taugt dafür nicht: ohne gespeicherte Quellen stellt Jellyfin eine eigene,
    /// leere Quelle voran, und Clients spielen dann diese.)
    /// </summary>
    private List<MediaSourceInfo> Sources(string videoId, ArdVideo? video, Guid itemId, long? ticks)
    {
        var root = _host.GetApiUrlForLocalAccess().TrimEnd('/') + "/Mediathek/";
        var versions = video is null
            ? ArdClient.Ladder.ToList()
            : video.Streams.OrderByDescending(s => s.Width).Select(s => (s.Label, s.Width, s.Height)).ToList();
        var sources = versions.Select(v => Source(videoId, v.Label, v.Width, v.Height, root + Live.LiveProxy.PlayPath(videoId, v.Width), ticks)).ToList();
        // Die beste Fassung trägt die Kennung des Titels selbst, wie es Jellyfin bei Titeln mit einer Datei hält. Darauf
        // verlassen sich das Umrechnen (HLS liest die Kennung der Quelle als GUID) und Jellyfins eigene Detailseite (sie
        // ruft die gewählte Fassung als Titel ab). Die übrigen Fassungen behalten bewusst eine Kennung, die keine GUID
        // ist: solche hält Jellyfin für andere Fassungen aus der Bibliothek und verwirft sie, wenn es dort keinen Titel
        // dazu gibt.
        if (sources.Count > 0)
        {
            sources[0].Id = itemId.ToString("N", CultureInfo.InvariantCulture);
        }

        return sources;
    }

    private static MediaSourceInfo Source(string videoId, string label, int width, int height, string url, long? ticks)
    {
        var s = new ArdStream(label, width, height, url);
        return new MediaSourceInfo
        {
            Id = SourcePrefix + s.Width.ToString(CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(videoId))).ToLowerInvariant()[..16],
            Name = s.Label.Length > 0 ? s.Label : s.Width.ToString(CultureInfo.InvariantCulture) + " px",
            Path = s.Url,
            Protocol = MediaProtocol.Http,
            Container = "mp4",
            // Ohne Angabe rechnet Jellyfin mit 40 Mbit/s und lässt Konten oder Geräte mit einer Bitraten-Grenze die Datei
            // nicht direkt abspielen („keine gültige Medienquelle“).
            Bitrate = BitrateFor(s.Width),
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
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Profile = "High", BitRate = BitrateFor(s.Width) - 128000, Width = s.Width > 0 ? s.Width : null, Height = s.Height > 0 ? s.Height : null, IsInterlaced = false, PixelFormat = "yuv420p", BitDepth = 8 },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Profile = "LC", BitRate = 128000, Channels = 2, SampleRate = 48000, Language = "deu", IsDefault = true },
            },
        };
    }

    /// <summary>Übliche Datenrate der Mediathek-Fassungen je Bildbreite (Bild und Ton zusammen), großzügig gerundet.</summary>
    public static int BitrateFor(int width) => width switch
    {
        >= 1920 => 5_400_000,
        >= 1280 => 3_500_000,
        >= 960 => 2_000_000,
        >= 640 => 1_400_000,
        > 0 => 900_000,
        _ => 3_500_000,
    };

    /// <summary>
    /// Baut einen abspielbaren Titel. Mit <paramref name="video"/> (der Seite des Videos) stehen Beschreibung und
    /// Videoadressen gleich am Titel; ohne Videoadresse (Ländersperre, abgelaufen) entsteht dann keiner. Ohne die Seite
    /// genügt der Eintrag der Liste, und die Adressen folgen beim Abspielen.
    /// </summary>
    private ChannelItemInfo? Media(ArdTeaser teaser, ArdVideo? video, ArdShow? asEpisodeOf)
    {
        if (video is not null && video.Streams.Count == 0)
        {
            return null;
        }

        var videoId = video?.Id ?? teaser.Id;
        var duration = video is { Duration: > 0 } ? video.Duration : teaser.Duration;
        var ticks = duration > 0 ? TimeSpan.FromSeconds(duration).Ticks : (long?)null;
        var date = video?.Date ?? teaser.Date;
        var (title, season, episode) = ArdClient.SplitEpisodeMark(video is { Title.Length: > 0 } ? video.Title : (teaser.Tagline.Length > 0 ? teaser.Title + " | " + teaser.Tagline : teaser.Title));
        season ??= teaser.Season;
        episode ??= teaser.Episode;
        var showTitle = asEpisodeOf?.Title ?? (video is { ShowTitle.Length: > 0 } ? video.ShowTitle : teaser.ShowTitle);
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

        var externalId = (isMovie ? "m" : "c") + Sep + videoId;
        var sources = Sources(videoId, video, ItemGuid(externalId, isMovie), ticks);

        var image = teaser.ImageWide.Length > 0 ? teaser.ImageWide : (video is { ImageWide.Length: > 0 } ? video.ImageWide : teaser.ImagePoster);
        var rating = video is { Rating.Length: > 0 } ? video.Rating : teaser.Rating;
        var publisher = video is { Publisher.Length: > 0 } ? video.Publisher : (asEpisodeOf?.Publisher ?? string.Empty);
        return new ChannelItemInfo
        {
            Id = externalId,
            Name = title,
            SeriesName = isMovie ? null : Nullable(showTitle),
            Overview = Join(isEpisode ? string.Empty : teaser.Tagline, (video ?? _ard.KnownVideo(videoId))?.Synopsis ?? string.Empty),
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            // Folgen gelten für Jellyfin als einzelne Videos, nicht als „Episode“: bei einer Episode sucht die Weboberfläche
            // vor dem Abspielen die übrigen Folgen der Serie, findet bei Sendungen aus einem Kanal keine und spielt dann gar
            // nichts („keine gültige Medienquelle“). Staffel und Folge stehen trotzdem am Titel.
            ContentType = isMovie ? ChannelMediaContentType.Movie : ChannelMediaContentType.Clip,
            IndexNumber = isEpisode ? episode : null,
            ParentIndexNumber = isEpisode ? season : null,
            RunTimeTicks = ticks,
            PremiereDate = date,
            DateCreated = date,
            // Die Mediathek nennt, seit wann ein Titel dort steht, nicht wann er entstand: für Filme wäre das als Jahr falsch.
            ProductionYear = isMovie ? null : date?.Year,
            OfficialRating = Rating(rating),
            Studios = publisher.Length > 0 ? new List<string> { publisher } : new List<string>(),
            ImageUrl = Picture(image, 960),
            HomePageUrl = "https://www.ardmediathek.de/video/" + Uri.EscapeDataString(videoId),
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
