using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mojito.Streaming;

/// <summary>
/// Session state.
/// </summary>
public enum SessionState
{
    /// <summary>
    /// Torrent added, waiting for qBittorrent metadata.
    /// </summary>
    Pending,

    /// <summary>
    /// Metadata known, buffering the playable prefix.
    /// </summary>
    Buffering,

    /// <summary>
    /// Playable prefix reached.
    /// </summary>
    Ready,

    /// <summary>
    /// A stream is currently being read by a client.
    /// </summary>
    Playing,

    /// <summary>
    /// Playback stopped; cleanup policy applied.
    /// </summary>
    Ended,

    /// <summary>
    /// The session failed (no metadata, dead swarm, deleted torrent...).
    /// </summary>
    Failed
}

/// <summary>
/// A streaming session: one media, one torrent, one channel item.
/// </summary>
public class StreamSession
{
    /// <summary>
    /// Gets or sets the session id (also channel item id and media source id).
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the media kind ("movie" or "episode").
    /// </summary>
    public string Kind { get; set; } = "movie";

    /// <summary>
    /// Gets or sets the *arr library media id.
    /// </summary>
    public int MediaId { get; set; }

    /// <summary>
    /// Gets or sets the Sonarr episode id (0 for movies).
    /// </summary>
    public int EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the media was created in the *arr library by this session.
    /// </summary>
    public bool MediaCreatedBySession { get; set; }

    /// <summary>
    /// Gets or sets the display title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episode display info (null for movies).
    /// </summary>
    public string? EpisodeInfo { get; set; }

    /// <summary>
    /// Gets or sets the poster URL.
    /// </summary>
    public string? PosterUrl { get; set; }

    /// <summary>
    /// Gets or sets the torrent hash (qBittorrent).
    /// </summary>
    public string TorrentHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the magnet URI.
    /// </summary>
    public string MagnetUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the session directory (torrent save path).
    /// </summary>
    public string SessionDir { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target video file name (relative to the session dir).
    /// </summary>
    public string MediaFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target video file size in bytes.
    /// </summary>
    public long MediaFileSize { get; set; }

    /// <summary>
    /// Gets or sets the max readable offset in the media file (downloaded prefix minus a safety margin).
    /// </summary>
    public long MaxReadableOffset
    {
        get => Interlocked.Read(ref _maxReadableOffset);
        set => Interlocked.Exchange(ref _maxReadableOffset, value);
    }

    private long _maxReadableOffset;

    /// <summary>
    /// Gets or sets the current download speed in bytes/s.
    /// </summary>
    public long DlSpeed { get; set; }

    /// <summary>
    /// Gets or sets the target file progress (0..1).
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets the state.
    /// </summary>
    public SessionState State { get; set; }

    /// <summary>
    /// Gets or sets a transient error message for the Failed state.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the creation time.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the full path of the target media file.
    /// </summary>
    /// <returns>The absolute file path.</returns>
    public string MediaFilePath()
    {
        return string.IsNullOrEmpty(MediaFileName)
            ? string.Empty
            : Path.Combine(SessionDir, MediaFileName);
    }

    /// <summary>
    /// Gets the qBittorrent session tag.
    /// </summary>
    /// <returns>The per-session tag.</returns>
    public string SessionTag()
    {
        var tag = Plugin.Instance?.Configuration.TorrentTag;
        return $"{(string.IsNullOrEmpty(tag) ? "mojito" : tag)}-{Id:N}";
    }
}

/// <summary>
/// In-memory session store with JSON persistence in the plugin data folder.
/// </summary>
public class StreamSessionStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<Guid, StreamSession> _sessions = new();
    private readonly string _persistPath;
    private readonly object _persistLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamSessionStore"/> class.
    /// </summary>
    /// <param name="dataFolderPath">The plugin data folder.</param>
    public StreamSessionStore(string dataFolderPath)
    {
        _persistPath = Path.Combine(dataFolderPath, "mojito-sessions.json");
        Load();
    }

    /// <summary>
    /// Adds or replaces a session.
    /// </summary>
    /// <param name="session">The session.</param>
    public void Add(StreamSession session)
    {
        _sessions[session.Id] = session;
        Persist();
    }

    /// <summary>
    /// Removes a session.
    /// </summary>
    /// <param name="id">The session id.</param>
    public void Remove(Guid id)
    {
        _sessions.TryRemove(id, out _);
        Persist();
    }

    /// <summary>
    /// Gets a session by id.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <returns>The session, or null.</returns>
    public StreamSession? Get(Guid id)
    {
        return _sessions.TryGetValue(id, out var session) ? session : null;
    }

    /// <summary>
    /// Lists all sessions, newest first.
    /// </summary>
    /// <returns>All sessions.</returns>
    public IEnumerable<StreamSession> All()
    {
        return _sessions.Values.OrderByDescending(s => s.CreatedAt);
    }

    /// <summary>
    /// Lists sessions that are downloading or playable.
    /// </summary>
    /// <returns>The active sessions.</returns>
    public IEnumerable<StreamSession> Active()
    {
        return All().Where(s => s.State is SessionState.Pending or SessionState.Buffering or SessionState.Ready or SessionState.Playing);
    }

    /// <summary>
    /// Persists the sessions to disk (fire-and-forget, serialized).
    /// </summary>
    public void Persist()
    {
        _ = Task.Run(() =>
        {
            lock (_persistLock)
            {
                try
                {
                    var sessions = _sessions.Values.ToList();
                    File.WriteAllText(_persistPath, JsonSerializer.Serialize(sessions, s_jsonOptions));
                }
                catch
                {
                    // Persistence is best-effort; losing it only means orphaned *arr entries.
                }
            }
        });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath))
            {
                return;
            }

            var sessions = JsonSerializer.Deserialize<List<StreamSession>>(File.ReadAllText(_persistPath), s_jsonOptions);
            if (sessions is null)
            {
                return;
            }

            foreach (var session in sessions)
            {
                // A Playing/Ready state at startup is stale: the HTTP stream died with the server.
                if (session.State is SessionState.Playing or SessionState.Ready or SessionState.Buffering)
                {
                    session.State = SessionState.Ended;
                }

                _sessions[session.Id] = session;
            }
        }
        catch
        {
            // Corrupt persistence file: start fresh.
        }
    }
}
