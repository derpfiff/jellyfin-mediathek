using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Live;

/// <summary>
/// Stellt beim Start einen schon eingerichteten Tuner richtig: ältere Fassungen des Plugins ließen „DTS ignorieren“ auf
/// Jellyfins Voreinstellung, und damit bleiben die Live-Sender stumm (siehe <c>SetupLive</c>).
/// </summary>
public sealed class TunerRepair : IHostedService
{
    private readonly IConfigurationManager _config;
    private readonly ILogger<TunerRepair> _logger;

    public TunerRepair(IConfigurationManager config, ILogger<TunerRepair> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = _config.GetConfiguration<LiveTvOptions>("livetv");
            var own = (options.TunerHosts ?? Array.Empty<TunerHostInfo>())
                .Where(t => (t.Url ?? string.Empty).EndsWith("/Mediathek/live.m3u", StringComparison.OrdinalIgnoreCase) && t.IgnoreDts)
                .ToList();
            if (own.Count > 0)
            {
                foreach (var tuner in own)
                {
                    tuner.IgnoreDts = false;
                }

                _config.SaveConfiguration("livetv", options);
                _logger.LogInformation("Mediathek: Tuner auf Wiedergabe mit Ton umgestellt");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mediathek: Tuner ließ sich nicht prüfen");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
