using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mojito.Providers;

/// <summary>
/// qBittorrent Web API v2 client (subset used by Mojito).
/// </summary>
public class QbitClient
{
    private readonly HttpClient _http;
    private bool _loggedIn;
    private DateTimeOffset _lastLogin;

    /// <summary>
    /// Initializes a new instance of the <see cref="QbitClient"/> class.
    /// </summary>
    public QbitClient()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static (string Url, string User, string Pass) Config()
    {
        var cfg = Plugin.Instance!.Configuration;
        return (cfg.QbitUrl.TrimEnd('/'), cfg.QbitUsername, cfg.QbitPassword);
    }

    private async Task EnsureLoginAsync()
    {
        var (url, user, pass) = Config();
        if (_loggedIn && DateTimeOffset.UtcNow - _lastLogin < TimeSpan.FromMinutes(5))
        {
            return;
        }

        // Empty credentials = localhost bypass assumed to be enabled.
        if (!string.IsNullOrEmpty(user))
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = user,
                ["password"] = pass
            });
            using var response = await _http.PostAsync(url + "/api/v2/auth/login", content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || body.Contains("Fails", StringComparison.Ordinal) || body.Contains("banned", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"qBittorrent login failed: {(int)response.StatusCode} {body}");
            }
        }

        _loggedIn = true;
        _lastLogin = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Sends a POST with form fields to the qBittorrent API.
    /// </summary>
    /// <param name="path">The API path, e.g. "/api/v2/torrents/add".</param>
    /// <param name="fields">The form fields.</param>
    /// <returns>The response body.</returns>
    public async Task<string> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields)
    {
        var (url, _, _) = Config();
        await EnsureLoginAsync().ConfigureAwait(false);
        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(url + path, content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"qBittorrent {path} failed: {(int)response.StatusCode} {body}");
        }

        return body;
    }

    /// <summary>
    /// Adds a magnet torrent configured for sequential streaming.
    /// </summary>
    /// <param name="magnet">The magnet URI.</param>
    /// <param name="savePath">The session save path.</param>
    /// <param name="category">The category (configurable).</param>
    /// <param name="tags">The tags to apply (configurable global tag + session tag).</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task AddMagnetAsync(string magnet, string savePath, string category, IEnumerable<string> tags)
    {
        var fields = new Dictionary<string, string>
        {
            ["urls"] = magnet,
            ["savepath"] = savePath,
            ["category"] = category,
            ["tags"] = string.Join(",", tags),
            ["sequentialDownload"] = "true",
            ["firstLastPiecePrio"] = "true",
            ["root_folder"] = "false",
            ["paused"] = "false"
        };
        return PostFormAsync("/api/v2/torrents/add", fields);
    }

    /// <summary>
    /// Lists torrent files with their download progress.
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <returns>The torrent files.</returns>
    public async Task<List<QbitFile>> GetFilesAsync(string hash)
    {
        var (url, _, _) = Config();
        await EnsureLoginAsync().ConfigureAwait(false);
        using var response = await _http.GetAsync($"{url}/api/v2/torrents/files?hash={hash}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        var result = new List<QbitFile>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            result.Add(new QbitFile
            {
                Index = item.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0,
                Name = item.GetProperty("name").GetString() ?? string.Empty,
                Size = item.GetProperty("size").GetInt64(),
                Progress = item.GetProperty("progress").GetDouble(),
                Priority = item.TryGetProperty("priority", out var prio) ? prio.GetInt32() : 1
            });
        }

        return result;
    }

    /// <summary>
    /// Gets torrent properties (progress, download speed, piece count...).
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <returns>The torrent properties, or null when the torrent vanished.</returns>
    public async Task<QbitProperties?> GetPropertiesAsync(string hash)
    {
        var (url, _, _) = Config();
        await EnsureLoginAsync().ConfigureAwait(false);
        using var response = await _http.GetAsync($"{url}/api/v2/torrents/properties?hash={hash}").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        var root = doc.RootElement;
        long GetLong(params string[] names)
        {
            foreach (var n in names)
            {
                if (root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.Number)
                {
                    return el.GetInt64();
                }
            }

            return 0;
        }

        return new QbitProperties
        {
            TotalSize = GetLong("total_size"),
            PiecesHave = GetLong("pieces_have"),
            PiecesNum = GetLong("pieces_num"),
            DlSpeed = GetLong("dl_speed", "dlspeed"),
            Progress = root.TryGetProperty("progress", out var p) ? p.GetDouble() : 0,
            SavePath = root.TryGetProperty("save_path", out var sp) ? sp.GetString() ?? string.Empty : string.Empty
        };
    }

    /// <summary>
    /// Sets the priority of selected files (0 = do not download).
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <param name="indexes">The file indexes.</param>
    /// <param name="priority">The new priority.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task SetFilePriorityAsync(string hash, IEnumerable<int> indexes, int priority)
    {
        var fields = new Dictionary<string, string>
        {
            ["hash"] = hash,
            ["id"] = string.Join("|", indexes),
            ["priority"] = priority.ToString()
        };
        return PostFormAsync("/api/v2/torrents/filePrio", fields);
    }

    /// <summary>
    /// Lists torrents carrying a given tag.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The matching torrent hashes and names.</returns>
    public async Task<List<(string Hash, string Name)>> GetTorrentsByTagAsync(string tag)
    {
        var (url, _, _) = Config();
        await EnsureLoginAsync().ConfigureAwait(false);
        using var response = await _http.GetAsync($"{url}/api/v2/torrents/info?tag={Uri.EscapeDataString(tag)}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        var result = new List<(string, string)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            result.Add((item.GetProperty("hash").GetString() ?? string.Empty, item.GetProperty("name").GetString() ?? string.Empty));
        }

        return result;
    }

    /// <summary>
    /// Adds tags to a torrent.
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <param name="tags">The tags.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task AddTagsAsync(string hash, string tags)
    {
        return PostFormAsync("/api/v2/torrents/addTags", new Dictionary<string, string>
        {
            ["hashes"] = hash,
            ["tags"] = tags
        });
    }

    /// <summary>
    /// Removes tags from a torrent.
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <param name="tags">The tags.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task RemoveTagsAsync(string hash, string tags)
    {
        return PostFormAsync("/api/v2/torrents/removeTags", new Dictionary<string, string>
        {
            ["hashes"] = hash,
            ["tags"] = tags
        });
    }

    /// <summary>
    /// Pauses a torrent.
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task PauseAsync(string hash)
    {
        return PostFormAsync("/api/v2/torrents/pause", new Dictionary<string, string> { ["hashes"] = hash });
    }

    /// <summary>
    /// Resumes a torrent.
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task ResumeAsync(string hash)
    {
        return PostFormAsync("/api/v2/torrents/resume", new Dictionary<string, string> { ["hashes"] = hash });
    }

    /// <summary>
    /// Deletes a torrent, optionally with its files.
    /// </summary>
    /// <param name="hash">The torrent hash.</param>
    /// <param name="deleteFiles">Whether to remove the downloaded files.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task DeleteAsync(string hash, bool deleteFiles)
    {
        return PostFormAsync("/api/v2/torrents/delete", new Dictionary<string, string>
        {
            ["hashes"] = hash,
            ["deleteFiles"] = deleteFiles ? "true" : "false"
        });
    }
}

/// <summary>
/// A file inside a qBittorrent torrent.
/// </summary>
public class QbitFile
{
    /// <summary>
    /// Gets or sets the file index.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the relative file name inside the torrent.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the download progress (0..1).
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets the file priority (0 = skipped).
    /// </summary>
    public int Priority { get; set; }
}

/// <summary>
/// qBittorrent torrent properties (subset).
/// </summary>
public class QbitProperties
{
    /// <summary>
    /// Gets or sets the total size in bytes.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Gets or sets the number of downloaded pieces.
    /// </summary>
    public long PiecesHave { get; set; }

    /// <summary>
    /// Gets or sets the total number of pieces.
    /// </summary>
    public long PiecesNum { get; set; }

    /// <summary>
    /// Gets or sets the download speed in bytes/s.
    /// </summary>
    public long DlSpeed { get; set; }

    /// <summary>
    /// Gets or sets the global progress (0..1).
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets the torrent save path.
    /// </summary>
    public string SavePath { get; set; } = string.Empty;
}
