using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Plugin.Mojito.Streaming;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mojito.Streaming;

/// <summary>
/// Watches Jellyfin playback events to reflect the real playback state on
/// sessions (via the media source id) and apply the cleanup policy on stop.
/// </summary>
public class PlaybackEventsConsumer :
    IEventConsumer<PlaybackStartEventArgs>,
    IEventConsumer<PlaybackStopEventArgs>
{
    private readonly MojitoSessionManager _manager;
    private readonly ILogger<PlaybackEventsConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackEventsConsumer"/> class.
    /// </summary>
    /// <param name="manager">The session manager.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackEventsConsumer(MojitoSessionManager manager, ILogger<PlaybackEventsConsumer> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        var session = Match(eventArgs);
        if (session is not null)
        {
            _logger.LogInformation("Mojito session {Id} playback started", session.Id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        var session = Match(eventArgs);
        if (session is null)
        {
            return;
        }

        _logger.LogInformation("Mojito session {Id} playback stopped", session.Id);
        await _manager.EndSessionAsync(session).ConfigureAwait(false);
    }

    private StreamSession? Match(PlaybackProgressEventArgs args)
    {
        var mediaSourceId = args.MediaSourceId;
        if (string.IsNullOrEmpty(mediaSourceId))
        {
            return null;
        }

        if (!Guid.TryParse(mediaSourceId, out var id))
        {
            return null;
        }

        return _manager.Store.Get(id);
    }
}
