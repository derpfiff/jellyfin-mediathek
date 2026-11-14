using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediathek.Web;

/// <summary>
/// Bindet die Seite der Mediathek (<c>web.js</c>, <c>web.css</c>) in Jellyfins Weboberfläche ein: zwei Zeilen am Ende
/// ihrer Startseite. Abschaltbar in den Einstellungen des Plugins; schlägt das Einfügen fehl, geht die Seite unverändert
/// hinaus.
/// </summary>
public class WebUiFilter : IStartupFilter
{
    private readonly ILogger<WebUiFilter> _logger;

    public WebUiFilter(ILogger<WebUiFilter> logger) => _logger = logger;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!IsIndex(context.Request) || Plugin.Instance is null || !Plugin.Instance.Configuration.WebUi)
                {
                    await nextMiddleware().ConfigureAwait(false);
                    return;
                }

                // Unkomprimiert und vollständig holen (kein 304, kein gzip), sonst lässt sich nichts einfügen.
                context.Request.Headers.Remove("Accept-Encoding");
                context.Request.Headers.Remove("If-None-Match");
                context.Request.Headers.Remove("If-Modified-Since");
                context.Request.Headers.Remove("Range");

                var original = context.Response.Body;
                await using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await nextMiddleware().ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Body = original;
                }

                buffer.Position = 0;
                var isHtml = context.Response.StatusCode == 200
                    && (context.Response.ContentType ?? string.Empty).Contains("text/html", StringComparison.OrdinalIgnoreCase)
                    && !context.Response.Headers.ContainsKey("Content-Encoding");
                if (!isHtml)
                {
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                    return;
                }

                byte[] output;
                try
                {
                    var html = Encoding.UTF8.GetString(buffer.ToArray());
                    var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0 && !html.Contains("Mediathek/web.js", StringComparison.Ordinal))
                    {
                        var v = Plugin.Instance.Version.ToString();
                        html = html[..at]
                            + "<link rel=\"stylesheet\" href=\"../Mediathek/web.css?v=" + v + "\"><script defer src=\"../Mediathek/web.js?v=" + v + "\"></script>"
                            + html[at..];
                    }

                    output = Encoding.UTF8.GetBytes(html);
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    context.Response.Headers["Cache-Control"] = "no-cache";
                    context.Response.ContentLength = output.Length;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Mediathek: Einbindung in die Weboberfläche fehlgeschlagen, Seite wird unverändert ausgeliefert");
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                    return;
                }

                await original.WriteAsync(output).ConfigureAwait(false);
            });
            next(app);
        };
    }

    private static bool IsIndex(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = (request.Path.Value ?? string.Empty).TrimEnd('/');
        return path.EndsWith("/web", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
