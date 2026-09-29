using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Plugin.Mojito.Providers;
using Jellyfin.Plugin.Mojito.Selection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mojito.Streaming;

/// <summary>
/// Polls qBittorrent for every active session: metadata discovery, target file
/// selection, file priorities, playable prefix computation and state tags.
/// Reconciles orphaned Mojito torrents at startup.
/// </summary>
public class SessionOrchestrator : IHostedService, IDisposable
{
    private readonly QbitClient _qbit;
    private readonly StreamSessionStore _store;
    private readonly ILogger<SessionOrchestrator> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionOrchestrator"/> class.
    /// </summary>
    /// <param name="qbit">The qBittorrent client.</param>
    /// <param name="store">The session store.</param>
    /// <param name="logger">The logger.</param>
    public SessionOrchestrator(QbitClient qbit, StreamSessionStore store, ILogger<SessionOrchestrator> logger)
    {
        _qbit = qbit;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mojito poll loop ended with an error");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _pollLock.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await ReconcileOrphansAsync().ConfigureAwait(false);
        using var timer = new PeriodicTimer(_pollInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!await _pollLock.WaitAsync(0, ct).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                await UpdateSessionsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mojito session update failed");
            }
            finally
            {
                _pollLock.Release();
            }
        }
    }

    private async Task ReconcileOrphansAsync()
    {
        try
        {
            var cfg = Plugin.Instance!.Configuration;
            var tag = string.IsNullOrEmpty(cfg.TorrentTag) ? "mojito" : cfg.TorrentTag;
            var orphans = await _qbit.GetTorrentsByTagAsync(tag).ConfigureAwait(false);
            foreach (var (hash, name) in orphans)
            {
                var sessionTag = $"mojito-state:ended";
                await _qbit.AddTagsAsync(hash, sessionTag).ConfigureAwait(false);
                if (cfg.Cleanup == Configuration.CleanupPolicy.Pause)
                {
                    await _qbit.PauseAsync(hash).ConfigureAwait(false);
                }
                else if (cfg.Cleanup == Configuration.CleanupPolicy.Remove)
                {
                    await _qbit.DeleteAsync(hash, true).ConfigureAwait(false);
                }

                _logger.LogInformation("Mojito reconciled orphaned torrent {Name} ({Hash})", name, hash);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mojito orphan reconciliation failed (is qBittorrent reachable?)");
        }
    }

    private async Task UpdateSessionsAsync(CancellationToken ct)
    {
        foreach (var session in _store.Active().ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await UpdateSessionAsync(session).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mojito failed to update session {Id}", session.Id);
            }
        }
    }

    private async Task UpdateSessionAsync(StreamSession session)
    {
        var properties = await _qbit.GetPropertiesAsync(session.TorrentHash).ConfigureAwait(false);
        if (properties is null)
        {
            session.State = SessionState.Failed;
            session.Error = "Le torrent a disparu de qBittorrent.";
            session.MaxReadableOffset = 0;
            _store.Persist();
            return;
        }

        session.DlSpeed = properties.DlSpeed;

        if (session.State == SessionState.Pending)
        {
            await TrySelectTargetFileAsync(session).ConfigureAwait(false);
        }

        if (session.State is SessionState.Buffering or SessionState.Ready or SessionState.Playing)
        {
            await UpdateReadablePrefixAsync(session).ConfigureAwait(false);
        }
    }

    private async Task TrySelectTargetFileAsync(StreamSession session)
    {
        var files = await _qbit.GetFilesAsync(session.TorrentHash).ConfigureAwait(false);
        if (files.Count == 0)
        {
            // qBittorrent still fetching metadata.
            if (DateTime.UtcNow - session.CreatedAt > TimeSpan.FromMinutes(3))
            {
                session.State = SessionState.Failed;
                session.Error = "Les métadonnées du torrent ne sont jamais arrivées.";
                _store.Persist();
            }

            return;
        }

        var videos = files.Where(f => ReleaseScorer.IsVideoFile(f.Name)).ToList();
        if (videos.Count == 0)
        {
            session.State = SessionState.Failed;
            session.Error = "Aucun fichier vidéo dans ce torrent.";
            _store.Persist();
            return;
        }

        var target = videos.OrderByDescending(f => f.Size).First();
        session.MediaFileName = target.Name;
        session.MediaFileSize = target.Size;
        session.State = SessionState.Buffering;

        // Skip every other file so the whole bandwidth goes to the target.
        var others = files.Where(f => f.Index != target.Index).Select(f => f.Index).ToList();
        if (others.Count > 0)
        {
            await _qbit.SetFilePriorityAsync(session.TorrentHash, others, 0).ConfigureAwait(false);
        }

        _store.Persist();
        _logger.LogInformation("Mojito session {Id} target file: {File} ({Size} bytes)", session.Id, target.Name, target.Size);
    }

    private async Task UpdateReadablePrefixAsync(StreamSession session)
    {
        var files = await _qbit.GetFilesAsync(session.TorrentHash).ConfigureAwait(false);
        var target = files.FirstOrDefault(f => f.Name == session.MediaFileName);
        if (target is null)
        {
            return;
        }

        session.Progress = target.Progress;

        // Sequential download => the downloaded prefix is contiguous. Keep a
        // safety margin of one piece-block so we never serve sparse zeros.
        var margin = 4 * 1024 * 1024;
        var max = Math.Max(0, (long)(target.Progress * session.MediaFileSize) - margin);
        if (target.Progress >= 1.0)
        {
            max = session.MediaFileSize;
        }

        session.MaxReadableOffset = max;

        if (session.State == SessionState.Buffering)
        {
            var cfg = Plugin.Instance!.Configuration;
            var thresholdBytes = Math.Min(
                Math.Max(cfg.StartThresholdBytes, session.MediaFileSize * cfg.StartThresholdPercent / 100),
                session.MediaFileSize);
            if (max >= thresholdBytes)
            {
                session.State = SessionState.Ready;
                _store.Persist();
                _logger.LogInformation("Mojito session {Id} ready ({Prefix}/{Size} bytes)", session.Id, max, session.MediaFileSize);
            }
        }
    }
}
