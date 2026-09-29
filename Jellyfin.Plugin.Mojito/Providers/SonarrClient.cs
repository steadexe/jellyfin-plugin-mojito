using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mojito.Providers;

/// <summary>
/// Thin Sonarr v3 API client. Configuration is read live from the plugin configuration.
/// </summary>
public class SonarrClient
{
    private readonly MojitoHttp _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="SonarrClient"/> class.
    /// </summary>
    /// <param name="http">The shared HTTP helper.</param>
    public SonarrClient(MojitoHttp http)
    {
        _http = http;
    }

    private static (string Url, string Key) Config()
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.SonarrApiKey))
        {
            throw new InvalidOperationException("Sonarr API key is not configured in the Mojito plugin settings.");
        }

        return (cfg.SonarrUrl.TrimEnd('/'), cfg.SonarrApiKey);
    }

    /// <summary>
    /// Searches a series by title via the Sonarr TVDB lookup.
    /// </summary>
    /// <param name="term">The search term.</param>
    /// <returns>The matching series, best match first.</returns>
    public Task<List<SonarrSeries>> LookupSeriesAsync(string term)
    {
        var (url, key) = Config();
        return _http.GetArrAsync<List<SonarrSeries>>(url, key, "/api/v3/series/lookup?term=" + Uri.EscapeDataString(term));
    }

    /// <summary>
    /// Adds a series to the Sonarr library without monitoring it.
    /// </summary>
    /// <param name="series">The series from the lookup result.</param>
    /// <param name="qualityProfileId">The quality profile id (0 = first profile).</param>
    /// <returns>The created library series.</returns>
    public async Task<SonarrSeries> AddSeriesAsync(SonarrSeries series, int qualityProfileId)
    {
        var (url, key) = Config();
        var root = await _http.GetArrAsync<List<RootFolder>>(url, key, "/api/v3/rootfolder").ConfigureAwait(false);
        var rootPath = root.FirstOrDefault()?.Path
            ?? throw new InvalidOperationException("Sonarr has no root folder configured.");

        qualityProfileId = qualityProfileId > 0
            ? qualityProfileId
            : (await _http.GetArrAsync<List<QualityProfile>>(url, key, "/api/v3/qualityprofile").ConfigureAwait(false)).First().Id;

        var payload = new
        {
            title = series.Title,
            tvdbId = series.TvdbId,
            qualityProfileId,
            rootFolderPath = rootPath,
            monitored = false,
            seasons = Array.Empty<object>(),
            addOptions = new { searchForSeries = false }
        };
        return await _http.PostArrAsync<SonarrSeries>(url, key, "/api/v3/series", payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a series from the Sonarr library (files are untouched).
    /// </summary>
    /// <param name="seriesId">The library series id.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task DeleteSeriesAsync(int seriesId)
    {
        var (url, key) = Config();
        return _http.DeleteArrAsync(url, key, $"/api/v3/series/{seriesId}");
    }

    /// <summary>
    /// Lists the episodes of a series.
    /// </summary>
    /// <param name="seriesId">The library series id.</param>
    /// <returns>The episodes.</returns>
    public Task<List<SonarrEpisode>> GetEpisodesAsync(int seriesId)
    {
        var (url, key) = Config();
        return _http.GetArrAsync<List<SonarrEpisode>>(url, key, $"/api/v3/episode?seriesId={seriesId}");
    }

    /// <summary>
    /// Performs an interactive indexer search for an episode.
    /// </summary>
    /// <param name="episodeId">The episode id.</param>
    /// <returns>The available releases.</returns>
    public Task<List<ArrRelease>> GetReleasesAsync(int episodeId)
    {
        var (url, key) = Config();
        return _http.GetArrAsync<List<ArrRelease>>(url, key, $"/api/v3/release?episodeId={episodeId}");
    }

    /// <summary>
    /// Lists the Sonarr quality profiles.
    /// </summary>
    /// <returns>The quality profiles.</returns>
    public Task<List<QualityProfile>> GetQualityProfilesAsync()
    {
        var (url, key) = Config();
        return _http.GetArrAsync<List<QualityProfile>>(url, key, "/api/v3/qualityprofile");
    }

    /// <summary>
    /// Downloads the .torrent file of a release.
    /// </summary>
    /// <param name="downloadUrl">The release download URL.</param>
    /// <returns>The torrent file bytes.</returns>
    public async Task<byte[]> DownloadTorrentFileAsync(string downloadUrl)
    {
        var (_, key) = Config();
        return await _http.DownloadTorrentFileAsync(key, downloadUrl).ConfigureAwait(false);
    }
}
