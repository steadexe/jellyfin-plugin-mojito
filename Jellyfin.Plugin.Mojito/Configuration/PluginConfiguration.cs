namespace Jellyfin.Plugin.Mojito.Configuration;

/// <summary>
/// Cleanup policy applied when a session's playback stops.
/// </summary>
public enum CleanupPolicy
{
    /// <summary>
    /// Pause the torrent; files stay on disk for later resumption.
    /// </summary>
    Pause = 0,

    /// <summary>
    /// Delete the torrent and its files.
    /// </summary>
    Remove = 1,

    /// <summary>
    /// Leave the torrent running and seeding.
    /// </summary>
    Seed = 2
}

/// <summary>
/// Plugin configuration. Every value is editable from the Jellyfin dashboard.
/// </summary>
public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        RadarrUrl = "http://localhost:7878";
        RadarrApiKey = string.Empty;
        SonarrUrl = "http://localhost:8989";
        SonarrApiKey = string.Empty;
        QbitUrl = "http://localhost:8080";
        QbitUsername = string.Empty;
        QbitPassword = string.Empty;
        SavePath = string.Empty;
        MovieQualityProfileId = 0;
        SeriesQualityProfileId = 0;
        TorrentTag = "mojito";
        TorrentCategory = "mojito";
        StartThresholdBytes = 30 * 1024 * 1024;
        StartThresholdPercent = 3;
        Cleanup = CleanupPolicy.Pause;
        KeepInLibrary = false;
    }

    /// <summary>
    /// Gets or sets the Radarr base URL.
    /// </summary>
    public string RadarrUrl { get; set; }

    /// <summary>
    /// Gets or sets the Radarr API key.
    /// </summary>
    public string RadarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets the Sonarr base URL.
    /// </summary>
    public string SonarrUrl { get; set; }

    /// <summary>
    /// Gets or sets the Sonarr API key.
    /// </summary>
    public string SonarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets the qBittorrent WebUI base URL.
    /// </summary>
    public string QbitUrl { get; set; }

    /// <summary>
    /// Gets or sets the qBittorrent username.
    /// </summary>
    public string QbitUsername { get; set; }

    /// <summary>
    /// Gets or sets the qBittorrent password.
    /// </summary>
    public string QbitPassword { get; set; }

    /// <summary>
    /// Gets or sets the download folder (must be readable by the Jellyfin process).
    /// </summary>
    public string SavePath { get; set; }

    /// <summary>
    /// Gets or sets the Radarr quality profile id used for movie selection (0 = Radarr default).
    /// </summary>
    public int MovieQualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the Sonarr quality profile id used for series selection (0 = Sonarr default).
    /// </summary>
    public int SeriesQualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the qBittorrent tag applied to every Mojito torrent.
    /// </summary>
    public string TorrentTag { get; set; }

    /// <summary>
    /// Gets or sets the qBittorrent category applied to every Mojito torrent.
    /// </summary>
    public string TorrentCategory { get; set; }

    /// <summary>
    /// Gets or sets the minimum downloaded prefix (bytes) before playback starts.
    /// </summary>
    public long StartThresholdBytes { get; set; }

    /// <summary>
    /// Gets or sets the minimum downloaded prefix (percent) before playback starts.
    /// </summary>
    public int StartThresholdPercent { get; set; }

    /// <summary>
    /// Gets or sets the cleanup policy applied when playback stops.
    /// </summary>
    public CleanupPolicy Cleanup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether media added to Radarr/Sonarr libraries stay there after the session.
    /// </summary>
    public bool KeepInLibrary { get; set; }
}
