using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mojito.Providers;

/// <summary>
/// Thin Radarr v3 API client. Configuration is read live from the plugin configuration.
/// </summary>
public class RadarrClient
{
    private readonly MojitoHttp _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadarrClient"/> class.
    /// </summary>
    /// <param name="http">The shared HTTP helper.</param>
    public RadarrClient(MojitoHttp http)
    {
        _http = http;
    }

    private static (string Url, string Key) Config()
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.RadarrApiKey))
        {
            throw new InvalidOperationException("Radarr API key is not configured in the Mojito plugin settings.");
        }

        return (cfg.RadarrUrl.TrimEnd('/'), cfg.RadarrApiKey);
    }

    /// <summary>
    /// Searches a movie by title via the Radarr TMDB lookup.
    /// </summary>
    /// <param name="term">The search term.</param>
    /// <returns>The matching movies, best match first.</returns>
    public Task<List<RadarrMovie>> LookupMovieAsync(string term)
    {
        var (url, key) = Config();
        return _http.GetArrAsync<List<RadarrMovie>>(url, key, "/api/v3/movie/lookup?term=" + Uri.EscapeDataString(term));
    }

    /// <summary>
    /// Adds a movie to the Radarr library without monitoring it.
    /// </summary>
    /// <param name="movie">The movie from the lookup result.</param>
    /// <param name="qualityProfileId">The quality profile id (0 = first profile).</param>
    /// <returns>The created library movie.</returns>
    public async Task<RadarrMovie> AddMovieAsync(RadarrMovie movie, int qualityProfileId)
    {
        var (url, key) = Config();
        var root = await _http.GetArrAsync<List<RootFolder>>(url, key, "/api/v3/rootfolder").ConfigureAwait(false);
        var rootPath = root.FirstOrDefault()?.Path
            ?? throw new InvalidOperationException("Radarr has no root folder configured.");

        qualityProfileId = qualityProfileId > 0
            ? qualityProfileId
            : (await _http.GetArrAsync<List<QualityProfile>>(url, key, "/api/v3/qualityprofile").ConfigureAwait(false)).First().Id;

        var payload = new
        {
            title = movie.Title,
            tmdbId = movie.TmdbId,
            year = movie.Year,
            qualityProfileId,
            rootFolderPath = rootPath,
            monitored = false,
            minimumAvailability = "released",
            addOptions = new { searchForMovie = false }
        };
        return await _http.PostArrAsync<RadarrMovie>(url, key, "/api/v3/movie", payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a movie from the Radarr library (files are untouched).
    /// </summary>
    /// <param name="movieId">The library movie id.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task DeleteMovieAsync(int movieId)
    {
        var (url, key) = Config();
        return _http.DeleteArrAsync(url, key, $"/api/v3/movie/{movieId}");
    }

    /// <summary>
    /// Performs an interactive indexer search for a movie.
    /// </summary>
    /// <param name="movieId">The library movie id.</param>
    /// <returns>The available releases.</returns>
    public Task<List<ArrRelease>> GetReleasesAsync(int movieId)
    {
        var (url, key) = Config();
        return _http.GetArrAsync<List<ArrRelease>>(url, key, $"/api/v3/release?movieId={movieId}");
    }

    /// <summary>
    /// Lists the Radarr quality profiles.
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
