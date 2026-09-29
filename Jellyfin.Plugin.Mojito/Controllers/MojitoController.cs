using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Plugin.Mojito.Selection;
using Jellyfin.Plugin.Mojito.Streaming;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mojito.Controllers;

/// <summary>
/// REST surface for Mojito: search, release selection, session management and
/// the streaming bridge consumed by the Jellyfin transcoder.
/// </summary>
[ApiController]
[Route("mojito")]
[Authorize]
public class MojitoController : ControllerBase
{
    private readonly MojitoSessionManager _manager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<MojitoController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MojitoController"/> class.
    /// </summary>
    /// <param name="manager">The session manager.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="logger">The logger.</param>
    public MojitoController(MojitoSessionManager manager, IServerApplicationHost appHost, ILogger<MojitoController> logger)
    {
        _manager = manager;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Builds the loopback URL of the streaming bridge for a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The absolute URL.</returns>
    public string BuildStreamUrl(Guid sessionId)
    {
        var baseUrl = _appHost.GetLocalApiUrl("127.0.0.1", "http", _appHost.HttpPort);
        return $"{baseUrl.TrimEnd('/')}/mojito/stream/{sessionId}";
    }

    // ── Search ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches a movie or series by title.
    /// </summary>
    /// <param name="type">The media type: "movie" or "series".</param>
    /// <param name="query">The search term.</param>
    /// <returns>The matches.</returns>
    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string type, [FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("query is required");
        }

        try
        {
            if (type == "movie")
            {
                var movies = await _manager.LookupMovieAsync(query).ConfigureAwait(false);
                return Ok(movies.Take(20).Select(m => new
                {
                    m.Id,
                    m.Title,
                    m.Year,
                    m.TmdbId,
                    m.Overview,
                    Poster = m.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl,
                    InLibrary = m.Id > 0
                }));
            }

            if (type == "series")
            {
                var series = await _manager.LookupSeriesAsync(query).ConfigureAwait(false);
                return Ok(series.Take(20).Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.Year,
                    s.TvdbId,
                    s.Overview,
                    Poster = s.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl,
                    InLibrary = s.Id > 0
                }));
            }

            return BadRequest("type must be movie or series");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mojito lookup failed");
            return StatusCode(502, ex.Message);
        }
    }

    /// <summary>
    /// Lists the episodes of a series.
    /// </summary>
    /// <param name="seriesTvdbId">The TVDB id of the series.</param>
    /// <returns>The episodes.</returns>
    [HttpGet("episodes")]
    public async Task<IActionResult> Episodes([FromQuery] int seriesTvdbId)
    {
        try
        {
            var lookup = await _manager.LookupSeriesAsync($"tvdb:{seriesTvdbId}").ConfigureAwait(false);
            var series = lookup.FirstOrDefault()
                ?? throw new InvalidOperationException("Série introuvable dans Sonarr.");
            var (librarySeries, created, episodes) = await _manager.GetEpisodesAsync(series).ConfigureAwait(false);
            return Ok(new
            {
                SeriesId = librarySeries.Id,
                SeriesCreatedBySession = created,
                SeriesTitle = librarySeries.Title,
                Episodes = episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).Select(e => new
                {
                    e.Id,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.Title,
                    e.AirDate
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mojito episodes failed");
            return StatusCode(502, ex.Message);
        }
    }

    /// <summary>
    /// Lists the quality profiles.
    /// </summary>
    /// <param name="type">The media type: "movie" or "series".</param>
    /// <returns>The profiles.</returns>
    [HttpGet("profiles")]
    public async Task<IActionResult> Profiles([FromQuery] string type)
    {
        try
        {
            var profiles = type == "series"
                ? await _manager.GetSeriesProfilesAsync().ConfigureAwait(false)
                : await _manager.GetMovieProfilesAsync().ConfigureAwait(false);
            return Ok(profiles.Select(p => new { p.Id, p.Name }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mojito profiles failed");
            return StatusCode(502, ex.Message);
        }
    }

    /// <summary>
    /// Gets the scored releases of a movie.
    /// </summary>
    /// <param name="movieTmdbId">The TMDB id of the movie.</param>
    /// <returns>The scored releases.</returns>
    [HttpGet("releases")]
    public async Task<IActionResult> Releases([FromQuery] int movieTmdbId, [FromQuery] int episodeId)
    {
        try
        {
            if (episodeId > 0)
            {
                var releases = await _manager.GetEpisodeReleasesAsync(episodeId).ConfigureAwait(false);
                return Ok(releases.Select(ToDto));
            }

            if (movieTmdbId > 0)
            {
                var lookup = await _manager.LookupMovieAsync($"tmdb:{movieTmdbId}").ConfigureAwait(false);
                var movie = lookup.FirstOrDefault(m => m.TmdbId == movieTmdbId)
                    ?? throw new InvalidOperationException("Film introuvable dans Radarr.");
                var (libraryMovie, releases) = await _manager.GetMovieReleasesAsync(movie).ConfigureAwait(false);
                return Ok(releases.Select(ToDto));
            }

            return BadRequest("movieTmdbId or episodeId is required");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mojito releases failed");
            return StatusCode(502, ex.Message);
        }
    }

    /// <summary>
    /// Starts a streaming session for the best release, or a specific one.
    /// </summary>
    /// <param name="request">The play request.</param>
    /// <returns>The created session.</returns>
    [HttpPost("play")]
    public async Task<IActionResult> Play([FromBody] PlayRequest request)
    {
        try
        {
            var session = new StreamSession();
            ScoredRelease chosen;
            if (request.EpisodeId > 0)
            {
                session.Kind = "episode";
                session.EpisodeId = request.EpisodeId;
                session.MediaId = request.SeriesId;
                session.MediaCreatedBySession = request.SeriesCreatedBySession;
                var releases = await _manager.GetEpisodeReleasesAsync(request.EpisodeId).ConfigureAwait(false);
                chosen = releases.FirstOrDefault(r => r.Release.Guid == request.ReleaseGuid)
                    ?? releases.FirstOrDefault(r => r.QualityAllowed)
                    ?? releases.FirstOrDefault()
                    ?? throw new InvalidOperationException("Aucune release disponible pour cet épisode.");
                session.Title = request.Title ?? "Episode";
                session.EpisodeInfo = request.EpisodeInfo;
            }
            else
            {
                session.Kind = "movie";
                var lookup = await _manager.LookupMovieAsync($"tmdb:{request.MovieTmdbId}").ConfigureAwait(false);
                var movie = lookup.FirstOrDefault(m => m.TmdbId == request.MovieTmdbId)
                    ?? throw new InvalidOperationException("Film introuvable dans Radarr.");
                session.MediaCreatedBySession = movie.Id <= 0;
                session.MediaId = movie.Id;
                if (movie.Id <= 0)
                {
                    var (libraryMovie, releases) = await _manager.GetMovieReleasesAsync(movie).ConfigureAwait(false);
                    session.MediaId = libraryMovie.Id;
                    chosen = releases.FirstOrDefault(r => r.Release.Guid == request.ReleaseGuid)
                        ?? releases.FirstOrDefault(r => r.QualityAllowed)
                        ?? releases.FirstOrDefault()
                        ?? throw new InvalidOperationException("Aucune release disponible pour ce film.");
                }
                else
                {
                    var (_, releases) = await _manager.GetMovieReleasesAsync(movie).ConfigureAwait(false);
                    chosen = releases.FirstOrDefault(r => r.Release.Guid == request.ReleaseGuid)
                        ?? releases.FirstOrDefault(r => r.QualityAllowed)
                        ?? releases.FirstOrDefault()
                        ?? throw new InvalidOperationException("Aucune release disponible pour ce film.");
                }

                session.Title = $"{movie.Title} ({movie.Year})";
                session.PosterUrl = movie.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl;
            }

            var created = await _manager.StartSessionAsync(session, chosen.Release).ConfigureAwait(false);
            return Ok(ToSessionDto(created));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mojito play failed");
            return StatusCode(502, ex.Message);
        }
    }

    // ── Sessions ────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the sessions.
    /// </summary>
    /// <returns>The sessions.</returns>
    [HttpGet("sessions")]
    public IActionResult Sessions()
    {
        return Ok(_manager.Store.All().Select(ToSessionDto));
    }

    /// <summary>
    /// Pauses a session torrent.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <returns>OK on success.</returns>
    [HttpPost("sessions/{id}/pause")]
    public async Task<IActionResult> Pause(Guid id)
    {
        var session = _manager.Store.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        await _manager.PauseAsync(session).ConfigureAwait(false);
        return Ok();
    }

    /// <summary>
    /// Resumes a session torrent.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <returns>OK on success.</returns>
    [HttpPost("sessions/{id}/resume")]
    public async Task<IActionResult> Resume(Guid id)
    {
        var session = _manager.Store.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        await _manager.ResumeAsync(session).ConfigureAwait(false);
        return Ok();
    }

    /// <summary>
    /// Ends a session (marks it ended, applies the cleanup policy).
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <returns>OK on success.</returns>
    [HttpPost("sessions/{id}/stop")]
    public async Task<IActionResult> Stop(Guid id)
    {
        var session = _manager.Store.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        await _manager.EndSessionAsync(session).ConfigureAwait(false);
        return Ok();
    }

    /// <summary>
    /// Deletes a session and its torrent.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <returns>OK on success.</returns>
    [HttpDelete("sessions/{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var session = _manager.Store.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        await _manager.StopAndRemoveAsync(session).ConfigureAwait(false);
        return Ok();
    }

    // ── Streaming bridge ─────────────────────────────────────────────────────

    /// <summary>
    /// Streams the downloaded prefix of the session media file. Called by the
    /// Jellyfin transcoder (no user token), hence the anonymous policy: the
    /// session GUID acts as the unguessable capability token.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The media stream.</returns>
    [HttpGet("stream/{id}")]
    [AllowAnonymous]
    public async Task Stream(Guid id, CancellationToken cancellationToken)
    {
        var session = _manager.Store.Get(id);
        if (session is null)
        {
            Response.StatusCode = 404;
            return;
        }

        if (session.State is SessionState.Failed)
        {
            Response.StatusCode = 500;
            await Response.WriteAsync(session.Error ?? "Session failed", cancellationToken).ConfigureAwait(false);
            return;
        }

        Response.StatusCode = 200;
        Response.ContentType = ContentTypeFor(session.MediaFileName);
        Response.Headers.CacheControl = "no-store";

        var threshold = ThresholdFor(session);
        try
        {
            await WaitForPrefixAsync(session, Math.Min(threshold, session.MediaFileSize > 0 ? session.MediaFileSize : long.MaxValue), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (session.State == SessionState.Failed)
        {
            Response.StatusCode = 500;
            return;
        }

        if (session.State is SessionState.Ready or SessionState.Buffering or SessionState.Pending)
        {
            session.State = SessionState.Playing;
        }

        string filePath;
        try
        {
            filePath = session.MediaFilePath();
        }
        catch (ArgumentException)
        {
            Response.StatusCode = 500;
            await Response.WriteAsync("Media file not selected yet", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Wait for the file to appear on disk (metadata phase can be slow).
        FileStream? stream = null;
        while (stream is null)
        {
            try
            {
                stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
            }
            catch (IOException)
            {
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (session.State == SessionState.Failed)
                {
                    Response.StatusCode = 500;
                    return;
                }
            }
        }

        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[512 * 1024];
            long position = 0;
            while (true)
            {
                long max = session.MaxReadableOffset;
                if (position >= max)
                {
                    var done = session.Progress >= 1.0 && position >= session.MediaFileSize;
                    if (done || session.State is SessionState.Failed or SessionState.Ended)
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                var toRead = (int)Math.Min(buffer.LongLength, max - position);
                stream.Position = position;
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                position += read;
            }
        }
    }

    private static long ThresholdFor(StreamSession session)
    {
        var cfg = Plugin.Instance!.Configuration;
        var threshold = Math.Max(cfg.StartThresholdBytes, session.MediaFileSize * cfg.StartThresholdPercent / 100);
        return Math.Min(threshold, session.MediaFileSize > 0 ? session.MediaFileSize : threshold);
    }

    private static async Task WaitForPrefixAsync(StreamSession session, long threshold, CancellationToken ct)
    {
        while (session.MaxReadableOffset < threshold)
        {
            if (session.State == SessionState.Failed)
            {
                throw new InvalidOperationException(session.Error ?? "Session failed");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static string ContentTypeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mkv" or ".webm" => "video/x-matroska",
            ".mp4" or ".m4v" => "video/mp4",
            ".ts" or ".m2ts" => "video/mp2t",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };
    }

    private static object ToDto(ScoredRelease r)
    {
        return new
        {
            r.Release.Guid,
            r.Release.Title,
            r.Release.Indexer,
            r.Release.Size,
            r.Release.Seeders,
            r.Release.Leechers,
            Quality = r.Release.Quality.Quality.Name,
            r.QualityAllowed,
            r.QualityRank,
            r.Score,
            r.EstimatedBitrate,
            Streamable = ReleaseScorer.HasStreamableExtension(r.Release.Title),
            HasMagnet = !string.IsNullOrEmpty(r.Release.MagnetUrl)
        };
    }

    private object ToSessionDto(StreamSession s)
    {
        return new
        {
            s.Id,
            s.Kind,
            s.Title,
            s.EpisodeInfo,
            s.PosterUrl,
            s.State,
            s.Progress,
            s.DlSpeed,
            s.MediaFileSize,
            s.MaxReadableOffset,
            s.MediaFileName,
            s.TorrentHash,
            s.CreatedAt,
            StreamUrl = BuildStreamUrl(s.Id)
        };
    }

    /// <summary>
    /// Play request payload.
    /// </summary>
    public class PlayRequest
    {
        /// <summary>
        /// Gets or sets the TMDB id of the movie (movies only).
        /// </summary>
        public int MovieTmdbId { get; set; }

        /// <summary>
        /// Gets or sets the Sonarr episode id (episodes only).
        /// </summary>
        public int EpisodeId { get; set; }

        /// <summary>
        /// Gets or sets the Sonarr series id (episodes only, for media cleanup).
        /// </summary>
        public int SeriesId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the series was added to Sonarr for this session.
        /// </summary>
        public bool SeriesCreatedBySession { get; set; }

        /// <summary>
        /// Gets or sets the chosen release guid (optional: best release by default).
        /// </summary>
        public string? ReleaseGuid { get; set; }

        /// <summary>
        /// Gets or sets the display title of the episode.
        /// </summary>
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the display info of the episode (SxxExx).
        /// </summary>
        public string? EpisodeInfo { get; set; }
    }
}
