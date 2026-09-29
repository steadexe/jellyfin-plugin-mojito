using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Plugin.Mojito.Streaming;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mojito.Channel;

/// <summary>
/// The Mojito channel: lists every session as a playable channel item. The
/// media source points to the streaming bridge served by the plugin.
/// </summary>
public class MojitoChannel : IChannel
{
    private readonly MojitoSessionManager _manager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<MojitoChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MojitoChannel"/> class.
    /// </summary>
    /// <param name="manager">The session manager.</param>
    /// <param name="appHost">The server application host.</param>
    /// <param name="logger">The logger.</param>
    public MojitoChannel(MojitoSessionManager manager, IServerApplicationHost appHost, ILogger<MojitoChannel> logger)
    {
        _manager = manager;
        _appHost = appHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Mojito";

    /// <inheritdoc />
    public string Description => "Médias streamés pendant leur téléchargement via Sonarr/Radarr et qBittorrent.";

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/local/jellyfin-plugin-mojito";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = [ChannelMediaType.Video],
            ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
            MaxPageSize = 100
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var baseUrl = _appHost.GetLocalApiUrl("127.0.0.1", "http", _appHost.HttpPort).TrimEnd('/');
        var sessions = _manager.Store.Active().Concat(_manager.Store.All().Where(s => s.State == SessionState.Ended)).DistinctBy(s => s.Id).Take(50);
        var items = new List<ChannelItemInfo>();
        foreach (var session in sessions)
        {
            var mediaSource = new MediaSourceInfo
            {
                Id = session.Id.ToString(),
                Path = $"{baseUrl}/mojito/stream/{session.Id}",
                Protocol = MediaProtocol.Http,

                // Always transcode server-side: clients are not assumed to be
                // compatible with the source codec/container.
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
                UseMostCompatibleTranscodingProfile = true,

                // The file is still downloading: tolerate the growing index,
                // non-monotonic timestamps and let ffmpeg generate PTS.
                IgnoreDts = true,
                IgnoreIndex = true,
                GenPtsInput = true,
                IsInfiniteStream = false,
                SupportsProbing = true,

                IsRemote = true,
                Container = System.IO.Path.GetExtension(session.MediaFileName).TrimStart('.'),
                Name = "Mojito Stream"
            };

            var name = session.Title;
            if (!string.IsNullOrEmpty(session.EpisodeInfo))
            {
                name += $" — {session.EpisodeInfo}";
            }

            var stateSuffix = session.State switch
            {
                SessionState.Pending => " [métadonnées...]",
                SessionState.Buffering => " [mise en tampon...]",
                _ => string.Empty
            };

            items.Add(new ChannelItemInfo
            {
                Id = session.Id.ToString(),
                Name = name + stateSuffix,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = session.Kind == "movie" ? ChannelMediaContentType.Movie : ChannelMediaContentType.Episode,
                Overview = $"État : {session.State} — {session.Progress:P0} ({session.DlSpeed / 1024} Ko/s)",
                ImageUrl = session.PosterUrl,
                MediaSources = [mediaSource]
            });
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    /// <inheritdoc />
    public Task<MediaBrowser.Controller.Providers.DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return [];
    }
}
