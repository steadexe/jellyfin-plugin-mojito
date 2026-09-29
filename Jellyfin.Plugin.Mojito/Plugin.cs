using System;
using System.Collections.Generic;
using System.Globalization;

using Jellyfin.Plugin.Mojito.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Mojito;

/// <summary>
/// The Jellyfin Mojito plugin: search media via Sonarr/Radarr, stream torrents
/// managed by qBittorrent while they download.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Mojito";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b4f7a2d1-9c83-4e16-8a5d-2f6b0e9c4a17");

    /// <inheritdoc />
    public override string Description => "Recherche de médias via Sonarr/Radarr, streaming torrent via qBittorrent pendant le téléchargement.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        // Single page on purpose: the Jellyfin 12 web client picks the plugin's
        // configuration page from this list, and prefers the EnableInMainMenu
        // entry when several pages exist. One page serves both the main menu
        // and the dashboard settings entry; the settings panel inside the page
        // is shown only to users who can read the plugin configuration.
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Mojito",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.WebUI.mojito.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "plugins",
                MenuIcon = "play_circle"
            }
        ];
    }
}
