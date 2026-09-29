using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Jellyfin.Plugin.Mojito.Configuration;
using Jellyfin.Plugin.Mojito.Providers;
using Jellyfin.Plugin.Mojito.Selection;
using Jellyfin.Plugin.Mojito.Streaming;

namespace Jellyfin.Plugin.Mojito.Streaming;

/// <summary>
/// Core service: resolves media via Radarr/Sonarr, scores releases, creates
/// sessions, pushes magnets to qBittorrent and applies cleanup policies.
/// </summary>
public class MojitoSessionManager
{
    private readonly RadarrClient _radarr;
    private readonly SonarrClient _sonarr;
    private readonly QbitClient _qbit;

    /// <summary>
    /// Initializes a new instance of the <see cref="MojitoSessionManager"/> class.
    /// </summary>
    /// <param name="store">The session store.</param>
    /// <param name="radarr">The Radarr client.</param>
    /// <param name="sonarr">The Sonarr client.</param>
    /// <param name="qbit">The qBittorrent client.</param>
    public MojitoSessionManager(StreamSessionStore store, RadarrClient radarr, SonarrClient sonarr, QbitClient qbit)
    {
        Store = store;
        _radarr = radarr;
        _sonarr = sonarr;
        _qbit = qbit;
    }

    /// <summary>
    /// Gets the session store.
    /// </summary>
    public StreamSessionStore Store { get; }

    /// <summary>
    /// Lists the Radarr quality profiles.
    /// </summary>
    /// <returns>The profiles.</returns>
    public Task<List<QualityProfile>> GetMovieProfilesAsync()
    {
        return _radarr.GetQualityProfilesAsync();
    }

    /// <summary>
    /// Lists the Sonarr quality profiles.
    /// </summary>
    /// <returns>The profiles.</returns>
    public Task<List<QualityProfile>> GetSeriesProfilesAsync()
    {
        return _sonarr.GetQualityProfilesAsync();
    }

    /// <summary>
    /// Searches movies by title.
    /// </summary>
    /// <param name="term">The search term.</param>
    /// <returns>The matches.</returns>
    public Task<List<RadarrMovie>> LookupMovieAsync(string term)
    {
        return _radarr.LookupMovieAsync(term);
    }

    /// <summary>
    /// Searches series by title.
    /// </summary>
    /// <param name="term">The search term.</param>
    /// <returns>The matches.</returns>
    public Task<List<SonarrSeries>> LookupSeriesAsync(string term)
    {
        return _sonarr.LookupSeriesAsync(term);
    }

    /// <summary>
    /// Lists the episodes of a series, ensuring the series exists in Sonarr.
    /// </summary>
    /// <param name="lookupSeries">The series chosen from the lookup.</param>
    /// <returns>The library series, whether it was created, and the episodes.</returns>
    public async Task<(SonarrSeries Series, bool Created, List<SonarrEpisode> Episodes)> GetEpisodesAsync(SonarrSeries lookupSeries)
    {
        if (lookupSeries.Id > 0)
        {
            var existing = await _sonarr.GetEpisodesAsync(lookupSeries.Id).ConfigureAwait(false);
            return (lookupSeries, false, existing);
        }

        var series = await _sonarr.AddSeriesAsync(lookupSeries, Plugin.Instance!.Configuration.SeriesQualityProfileId).ConfigureAwait(false);
        var episodes = await _sonarr.GetEpisodesAsync(series.Id).ConfigureAwait(false);
        return (series, true, episodes);
    }

    /// <summary>
    /// Gets the scored releases for a movie, ensuring the movie exists in Radarr.
    /// </summary>
    /// <param name="lookupMovie">The movie chosen from the lookup.</param>
    /// <returns>The movie library resource and the scored releases.</returns>
    public async Task<(RadarrMovie Movie, List<ScoredRelease> Releases)> GetMovieReleasesAsync(RadarrMovie lookupMovie)
    {
        var movie = lookupMovie.Id > 0
            ? lookupMovie
            : await _radarr.AddMovieAsync(lookupMovie, Plugin.Instance!.Configuration.MovieQualityProfileId).ConfigureAwait(false);

        var releases = await _radarr.GetReleasesAsync(movie.Id).ConfigureAwait(false);
        var profiles = await _radarr.GetQualityProfilesAsync().ConfigureAwait(false);
        var profile = profiles.FirstOrDefault(p => p.Id == (Plugin.Instance!.Configuration.MovieQualityProfileId > 0
            ? Plugin.Instance!.Configuration.MovieQualityProfileId
            : movie.QualityProfileId));

        return (movie, ReleaseScorer.Score(releases, profile, 110));
    }

    /// <summary>
    /// Gets the scored releases for an episode.
    /// </summary>
    /// <param name="episodeId">The Sonarr episode id.</param>
    /// <returns>The scored releases.</returns>
    public async Task<List<ScoredRelease>> GetEpisodeReleasesAsync(int episodeId)
    {
        var releases = await _sonarr.GetReleasesAsync(episodeId).ConfigureAwait(false);
        var profiles = await _sonarr.GetQualityProfilesAsync().ConfigureAwait(false);
        var profile = profiles.FirstOrDefault(p => p.Id == Plugin.Instance!.Configuration.SeriesQualityProfileId);
        return ReleaseScorer.Score(releases, profile, 45);
    }

    /// <summary>
    /// Starts a streaming session for the best (or explicitly chosen) release.
    /// </summary>
    /// <param name="session">The prepared session (media info filled in).</param>
    /// <param name="release">The chosen release.</param>
    /// <returns>The created session, updated with torrent info.</returns>
    public async Task<StreamSession> StartSessionAsync(StreamSession session, ArrRelease release)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.SavePath))
        {
            throw new InvalidOperationException("Le dossier de téléchargement (SavePath) n'est pas configuré.");
        }

        if (string.IsNullOrWhiteSpace(cfg.SavePath) || !Directory.Exists(cfg.SavePath))
        {
            throw new InvalidOperationException($"Le dossier de téléchargement est introuvable pour le serveur Jellyfin : {cfg.SavePath}");
        }

        var magnet = release.MagnetUrl;
        if (string.IsNullOrWhiteSpace(magnet))
        {
            throw new InvalidOperationException("Cette release n'expose pas de magnet URI; seules les releases magnet sont supportées pour l'instant.");
        }

        session.MagnetUri = magnet;
        session.TorrentHash = ParseInfoHash(magnet)
            ?? throw new InvalidOperationException("Impossible de déterminer le hash du torrent depuis le magnet URI.");
        session.SessionDir = Path.Combine(cfg.SavePath, session.Id.ToString("N"));
        Directory.CreateDirectory(session.SessionDir);

        var tag = string.IsNullOrEmpty(cfg.TorrentTag) ? "mojito" : cfg.TorrentTag;
        var category = string.IsNullOrEmpty(cfg.TorrentCategory) ? "mojito" : cfg.TorrentCategory;
        await _qbit.AddMagnetAsync(magnet, session.SessionDir, category, [tag, session.SessionTag(), "mojito-state:pending"])
            .ConfigureAwait(false);

        session.State = SessionState.Pending;
        Store.Add(session);
        return session;
    }

    /// <summary>
    /// Pauses a session torrent.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task PauseAsync(StreamSession session)
    {
        return _qbit.PauseAsync(session.TorrentHash);
    }

    /// <summary>
    /// Resumes a session torrent.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ResumeAsync(StreamSession session)
    {
        await _qbit.ResumeAsync(session.TorrentHash).ConfigureAwait(false);
        if (session.State == SessionState.Ended)
        {
            session.State = SessionState.Pending;
            Store.Persist();
        }
    }

    /// <summary>
    /// Marks a session ended and applies the cleanup policy.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EndSessionAsync(StreamSession session)
    {
        if (session.State is SessionState.Ended or SessionState.Failed)
        {
            return;
        }

        session.State = SessionState.Ended;
        Store.Persist();
        await ApplyCleanupAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops a session and deletes the torrent (and files when configured).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task StopAndRemoveAsync(StreamSession session)
    {
        try
        {
            await _qbit.DeleteAsync(session.TorrentHash, Plugin.Instance!.Configuration.Cleanup == CleanupPolicy.Remove).ConfigureAwait(false);
        }
        catch
        {
            // The torrent may already be gone.
        }

        await CleanupArrMediaAsync(session).ConfigureAwait(false);
        Store.Remove(session.Id);
    }

    /// <summary>
    /// Applies the configured cleanup policy to a session's torrent and media.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ApplyCleanupAsync(StreamSession session)
    {
        var cfg = Plugin.Instance!.Configuration;
        try
        {
            await _qbit.RemoveTagsAsync(session.TorrentHash, "mojito-state:pending,mojito-state:buffering,mojito-state:playing").ConfigureAwait(false);
            await _qbit.AddTagsAsync(session.TorrentHash, "mojito-state:ended").ConfigureAwait(false);
            if (cfg.Cleanup == CleanupPolicy.Pause)
            {
                await _qbit.PauseAsync(session.TorrentHash).ConfigureAwait(false);
            }
            else if (cfg.Cleanup == CleanupPolicy.Remove)
            {
                await _qbit.DeleteAsync(session.TorrentHash, true).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The torrent may already be gone; not fatal.
        }

        await CleanupArrMediaAsync(session).ConfigureAwait(false);
    }

    private async Task CleanupArrMediaAsync(StreamSession session)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (cfg.KeepInLibrary || !session.MediaCreatedBySession)
        {
            return;
        }

        try
        {
            if (session.Kind == "movie")
            {
                await _radarr.DeleteMovieAsync(session.MediaId).ConfigureAwait(false);
            }
            else
            {
                await _sonarr.DeleteSeriesAsync(session.MediaId).ConfigureAwait(false);
            }
        }
        catch
        {
            // Media cleanup is best-effort.
        }
    }

    /// <summary>
    /// Extracts the infohash (v1) from a magnet URI, as a lowercase hex string.
    /// </summary>
    /// <param name="magnet">The magnet URI.</param>
    /// <returns>The hash, or null.</returns>
    internal static string? ParseInfoHash(string magnet)
    {
        var idx = magnet.IndexOf("urn:btih:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + "urn:btih:".Length;
        var remainder = magnet[start..];
        var end = remainder.IndexOf('&');
        var hash = end < 0 ? remainder : remainder[..end];
        if (hash.Length == 40)
        {
            return hash.ToLowerInvariant();
        }

        if (hash.Length == 32)
        {
            // Base32 encoded v1 infohash.
            return Convert.ToHexString(Base32Decode(hash)).ToLowerInvariant();
        }

        return null;
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var bits = 0;
        var value = 0;
        using var output = new MemoryStream();
        foreach (var c in input.ToLowerInvariant())
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0)
            {
                continue;
            }

            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.WriteByte((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }

        return output.ToArray();
    }
}
