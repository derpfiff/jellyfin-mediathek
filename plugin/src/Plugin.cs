using Jellyfin.Plugin.Mediathek.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Mediathek;

/// <summary>Mediathek: Sendungen der öffentlich-rechtlichen Sender als Kanal und ihr laufendes Programm als Live-TV.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Mediathek";

    public override Guid Id => Guid.Parse("b3d0a2f4-71c6-4e7b-9a0e-5c2f8d14e6a9");

    public override string Description => "Sendungen der öffentlich-rechtlichen Mediatheken zum Stöbern, Suchen und Abspielen, dazu ihre Live-Programme mit Programmübersicht.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo { Name = "Mediathek", EmbeddedResourcePath = "Mediathek.configPage.html" };
    }
}
