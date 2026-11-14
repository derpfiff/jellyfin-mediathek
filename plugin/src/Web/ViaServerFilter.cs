using Jellyfin.Plugin.Mediathek.Live;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Mediathek.Web;

/// <summary>
/// Fragt ein Client, wo er einen Titel der Mediathek abholen soll, nennt Jellyfin die Adresse beim Sender. Mit der Einstellung
/// „über den Server leiten“ setzt dieser Filter stattdessen die Adresse des Plugins ein (<see cref="LiveProxy.VodAsync"/>),
/// und zwar unter genau der Serveradresse, über die der Client gerade gefragt hat. Für Ländersperren zählt dann der Standort
/// des Servers. Adressen, die das Plugin erst beim Abspielen auflöst (<c>Mediathek/play/…</c>), stellt er in jedem Fall auf die
/// Serveradresse des Clients um. Andere Titel bleiben unberührt.
/// </summary>
public sealed class ViaServerFilter : IAsyncResultFilter
{
    private const string PlayMark = "/Mediathek/play/";

    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: PlaybackInfoResponse info } && info.MediaSources is not null)
        {
            var viaServer = Plugin.Instance?.Configuration.ViaServer ?? true;
            string? root = null;
            foreach (var source in info.MediaSources)
            {
                if (source.Id is null || !source.Id.StartsWith(Channel.MediathekChannel.SourcePrefix, StringComparison.Ordinal) || source.Path is null)
                {
                    continue;
                }

                // Adressen, die das Plugin erst beim Abspielen auflöst, kennt Jellyfin unter seiner eigenen, inneren
                // Adresse; der Client braucht sie unter der Adresse, über die er den Server erreicht.
                var play = source.Path.IndexOf(PlayMark, StringComparison.Ordinal);
                if (play >= 0)
                {
                    root ??= Root(context.HttpContext.Request);
                    source.Path = root + source.Path[play..];
                    continue;
                }

                if (!viaServer || !source.Path.StartsWith("https://", StringComparison.Ordinal)
                    || source.Path.Contains("/Mediathek/vod/", StringComparison.Ordinal))
                {
                    continue;
                }

                root ??= Root(context.HttpContext.Request);
                source.Path = root + "/Mediathek/" + LiveProxy.VodPath(source.Path);
            }
        }

        return next();
    }

    /// <summary>Die Adresse, unter der der Client den Server erreicht hat.</summary>
    private static string Root(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        // Hinter einem Reverse-Proxy, den Jellyfin nicht als solchen kennt, sähe die Anfrage nach http aus.
        var forwarded = request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
        var scheme = forwarded is "https" or "http" ? forwarded : request.Scheme;
        return scheme + "://" + request.Host + request.PathBase;
    }
}
