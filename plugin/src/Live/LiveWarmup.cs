using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Live;

/// <summary>
/// Verkürzt das Einschalten. Bevor Jellyfin einen Live-Sender zum ersten Mal abspielt, wartet es drei Sekunden und
/// untersucht dann den Stream; das Ergebnis legt es im Zwischenspeicher ab, und ab dem zweiten Mal startet der Sender
/// sofort. Dieser Dienst erledigt das erste Mal im Hintergrund für alle Sender des Plugins: kurz nach dem Start des
/// Servers und danach regelmäßig, damit die Angaben frisch bleiben und Jellyfins Aufräumen sie nicht entfernt.
/// </summary>
public sealed class LiveWarmup : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly ILiveTvManager _liveTv;
    private readonly IMediaSourceManager _sources;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<LiveWarmup> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stop;

    public LiveWarmup(ILiveTvManager liveTv, IMediaSourceManager sources, IApplicationPaths paths, ILogger<LiveWarmup> logger)
    {
        _liveTv = liveTv;
        _sources = sources;
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(FirstRun, ct).ConfigureAwait(false);
                    while (!ct.IsCancellationRequested)
                    {
                        await RunAsync(ct).ConfigureAwait(false);
                        await Task.Delay(Interval, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Der Server fährt herunter.
                }
            },
            ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stop?.Dispose();
        _gate.Dispose();
    }

    /// <summary>Holt die Stream-Angaben aller Sender des Plugins, denen sie fehlen oder bei denen sie alt sind.</summary>
    /// <returns>Wie viele Sender untersucht wurden.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return 0;
        }

        var done = 0;
        try
        {
            var channels = _liveTv.GetInternalChannels(new LiveTvChannelQuery(), new DtoOptions(false), ct);
            foreach (var channel in channels.Items)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var sources = await _sources.GetPlaybackMediaSources(channel, null!, false, false, ct).ConfigureAwait(false);
                    var source = sources.FirstOrDefault(s => !string.IsNullOrEmpty(s.OpenToken)
                        && (s.Path ?? string.Empty).Contains("/Mediathek/live/m/", StringComparison.OrdinalIgnoreCase));
                    if (source is null)
                    {
                        continue;
                    }

                    // Dieselbe Datei, in der Jellyfin die Angaben zu diesem Sender sucht.
                    var name = new Guid(MD5.HashData(Encoding.Unicode.GetBytes(source.OpenToken))).ToString("N") + ".json";
                    var file = Path.Combine(_paths.CachePath, "mediainfo", name);
                    if (File.Exists(file))
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge)
                        {
                            continue;
                        }

                        File.Delete(file);
                    }

                    var opened = await _sources.OpenLiveStream(new LiveStreamRequest { OpenToken = source.OpenToken, ItemId = channel.Id }, ct).ConfigureAwait(false);
                    var id = opened.MediaSource?.LiveStreamId;
                    if (!string.IsNullOrEmpty(id))
                    {
                        await _sources.CloseLiveStream(id).ConfigureAwait(false);
                    }

                    done++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Ein Sender, der gerade nicht antwortet, wird beim nächsten Lauf oder beim Einschalten untersucht.
                    _logger.LogDebug(ex, "Mediathek: {Channel} ließ sich nicht vorab untersuchen", channel.Name);
                }
            }

            if (done > 0)
            {
                _logger.LogInformation("Mediathek: {Count} Sender für schnelles Einschalten vorbereitet", done);
            }
        }
        finally
        {
            _gate.Release();
        }

        return done;
    }
}
