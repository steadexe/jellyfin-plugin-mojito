using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Mojito.Providers;

/// <summary>
/// Radarr/Sonarr image.
/// </summary>
public class ArrMediaImage
{
    /// <summary>
    /// Gets or sets the media type (poster, fanart...).
    /// </summary>
    public string CoverType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote URL.
    /// </summary>
    public string RemoteUrl { get; set; } = string.Empty;
}

/// <summary>
/// Radarr movie resource (subset).
/// </summary>
public class RadarrMovie
{
    /// <summary>
    /// Gets or sets the library id (0 when not in library).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Gets or sets the TMDB id.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quality profile id.
    /// </summary>
    public int QualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the library path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the images.
    /// </summary>
    public List<ArrMediaImage> Images { get; set; } = [];
}

/// <summary>
/// Sonarr series resource (subset).
/// </summary>
public class SonarrSeries
{
    /// <summary>
    /// Gets or sets the library id (0 when not in library).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Gets or sets the TVDB id.
    /// </summary>
    public int TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quality profile id.
    /// </summary>
    public int QualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the library path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the images.
    /// </summary>
    public List<ArrMediaImage> Images { get; set; } = [];
}

/// <summary>
/// Sonarr episode resource (subset).
/// </summary>
public class SonarrEpisode
{
    /// <summary>
    /// Gets or sets the episode id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the air date.
    /// </summary>
    public string? AirDate { get; set; }
}

/// <summary>
/// Release quality (Radarr/Sonarr).
/// </summary>
public class ReleaseQuality
{
    /// <summary>
    /// Gets or sets the inner quality.
    /// </summary>
    [JsonPropertyName("quality")]
    public ReleaseQualityInner Quality { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the release is proper/repack.
    /// </summary>
    public bool Proper { get; set; }
}

/// <summary>
/// Inner quality object.
/// </summary>
public class ReleaseQualityInner
{
    /// <summary>
    /// Gets or sets the quality id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the quality name (e.g. WEBDL-1080p).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quality weight.
    /// </summary>
    public int Weight { get; set; }
}

/// <summary>
/// Radarr/Sonarr release resource (subset).
/// </summary>
public class ArrRelease
{
    /// <summary>
    /// Gets or sets the release guid.
    /// </summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the indexer name.
    /// </summary>
    public string Indexer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the seeders count.
    /// </summary>
    public int Seeders { get; set; }

    /// <summary>
    /// Gets or sets the leechers count.
    /// </summary>
    public int Leechers { get; set; }

    /// <summary>
    /// Gets or sets the download URL (.torrent file).
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the magnet URL.
    /// </summary>
    public string MagnetUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the protocol (torrent/usenet).
    /// </summary>
    public string Protocol { get; set; } = "torrent";

    /// <summary>
    /// Gets or sets the quality.
    /// </summary>
    public ReleaseQuality Quality { get; set; } = new();

    /// <summary>
    /// Gets or sets the custom format score (when exposed).
    /// </summary>
    public int CustomFormatScore { get; set; }

    /// <summary>
    /// Gets or sets the publish date.
    /// </summary>
    public string PublishDate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rejections.
    /// </summary>
    public List<string> Rejections { get; set; } = [];
}

/// <summary>
/// Radarr/Sonarr quality profile (subset).
/// </summary>
public class QualityProfile
{
    /// <summary>
    /// Gets or sets the profile id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile items (ordered best to worst).
    /// </summary>
    public List<QualityProfileItem> Items { get; set; } = [];
}

/// <summary>
/// Quality profile item.
/// </summary>
public class QualityProfileItem
{
    /// <summary>
    /// Gets or sets the quality (null for groups).
    /// </summary>
    public ReleaseQualityInner? Quality { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the quality is allowed.
    /// </summary>
    public bool Allowed { get; set; }
}

/// <summary>
/// Radarr/Sonarr root folder.
/// </summary>
public class RootFolder
{
    /// <summary>
    /// Gets or sets the root folder id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the root folder path.
    /// </summary>
    public string Path { get; set; } = string.Empty;
}
