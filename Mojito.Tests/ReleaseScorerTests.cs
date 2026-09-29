using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Plugin.Mojito.Providers;
using Jellyfin.Plugin.Mojito.Selection;
using Xunit;

namespace Jellyfin.Plugin.Mojito.Tests;

public class ReleaseScorerTests
{
    private static QualityProfile Profile(params string[] allowed)
    {
        var profile = new QualityProfile();
        var rank = 0;
        foreach (var name in allowed)
        {
            profile.Items.Add(new QualityProfileItem
            {
                Quality = new ReleaseQualityInner { Name = name },
                Allowed = true
            });
            rank++;
        }

        // A forbidden quality at the end.
        profile.Items.Add(new QualityProfileItem
        {
            Quality = new ReleaseQualityInner { Name = "Forbidden" },
            Allowed = false
        });
        return profile;
    }

    private static ArrRelease Release(string guid, string title, string quality, int seeders, long size, string? magnet = "magnet:?xt=urn:btih:x")
    {
        return new ArrRelease
        {
            Guid = guid,
            Title = title,
            Size = size,
            Seeders = seeders,
            MagnetUrl = magnet ?? string.Empty,
            DownloadUrl = magnet is null ? "http://dl/torrent.torrent" : string.Empty,
            Quality = new ReleaseQuality { Quality = new ReleaseQualityInner { Name = quality, Weight = 10 } }
        };
    }

    [Fact]
    public void Score_PrefersAllowedQualityOverSeeders()
    {
        // Only WEBDL-1080p is allowed by the profile; HDTV-720p is not.
        var profile = Profile("WEBDL-1080p");
        var releases = new List<ArrRelease>
        {
            Release("a", "Show.HDTV-720p", "HDTV-720p", seeders: 500, size: 1),
            Release("b", "Show.WEBDL-1080p", "WEBDL-1080p", seeders: 3, size: 1)
        };

        var scored = ReleaseScorer.Score(releases, profile, 45);
        Assert.Equal("b", scored[0].Release.Guid);
        Assert.True(scored[0].QualityAllowed);
        Assert.Equal(0, scored[0].QualityRank);
        Assert.False(scored[1].QualityAllowed);
    }

    [Fact]
    public void Score_DropsRejectionsAndNonTorrent()
    {
        var releases = new List<ArrRelease>
        {
            Release("rejected", "Show.WEBDL-1080p", "WEBDL-1080p", 100, 1),
            Release("usenet", "Show.WEBDL-1080p", "WEBDL-1080p", 100, 1)
        };
        releases[0].Rejections.Add("Quality forbidden");
        releases[1].Protocol = "usenet";

        var scored = ReleaseScorer.Score(releases, Profile("WEBDL-1080p"), 45);
        Assert.Empty(scored);
    }

    [Fact]
    public void Score_KeepsTorrentFileOnlyReleases()
    {
        var releases = new List<ArrRelease>
        {
            Release("fileonly", "Show.WEBDL-1080p", "WEBDL-1080p", 100, 1, magnet: null)
        };

        var scored = ReleaseScorer.Score(releases, Profile("WEBDL-1080p"), 45);
        Assert.Single(scored);
        Assert.False(scored[0].Release.MagnetUrl.Length > 0);
    }

    [Fact]
    public void Score_PenalizesNonStreamableContainers()
    {
        var releases = new List<ArrRelease>
        {
            Release("avi", "Show.HDTV.AVI", "HDTV-720p", 300, 1),
            Release("mkv", "Show.HDTV.MKV", "HDTV-720p", 100, 1)
        };

        // Rename to fake the container in the title.
        releases[0].Title = "Show.HDTV.avi";
        releases[1].Title = "Show.HDTV.mkv";
        var scored = ReleaseScorer.Score(releases, Profile("HDTV-720p"), 45);
        Assert.Equal("mkv", scored[0].Release.Guid);
        Assert.True(ReleaseScorer.HasStreamableExtension("show.mkv"));
        Assert.False(ReleaseScorer.HasStreamableExtension("show.avi"));
    }

    [Fact]
    public void Score_EmptyProfileAllowsEverything()
    {
        var releases = new List<ArrRelease> { Release("a", "Show.CAM", "CAM", 10, 1) };
        var scored = ReleaseScorer.Score(releases, profile: null, 45);
        Assert.Single(scored);
        Assert.True(scored[0].QualityAllowed);
    }

    [Fact]
    public void IsVideoFile_FiltersExtensions()
    {
        Assert.True(ReleaseScorer.IsVideoFile("movie.mkv"));
        Assert.True(ReleaseScorer.IsVideoFile("movie.mp4"));
        Assert.False(ReleaseScorer.IsVideoFile("nfo.txt"));
        Assert.False(ReleaseScorer.IsVideoFile("sample.jpg"));
    }
}
