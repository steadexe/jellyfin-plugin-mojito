using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Plugin.Mojito.Providers;

namespace Jellyfin.Plugin.Mojito.Selection;

/// <summary>
/// A release scored against the configured quality profile.
/// </summary>
public class ScoredRelease
{
    /// <summary>
    /// Gets or sets the release.
    /// </summary>
    public ArrRelease Release { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the release quality is allowed by the profile.
    /// </summary>
    public bool QualityAllowed { get; set; }

    /// <summary>
    /// Gets or sets the rank in the quality profile (0 = best quality, lower is better).
    /// </summary>
    public int QualityRank { get; set; } = -1;

    /// <summary>
    /// Gets or sets the computed score.
    /// </summary>
    public long Score { get; set; }

    /// <summary>
    /// Gets or sets the computed bitrate (bits/s), estimated from an assumed runtime.
    /// </summary>
    public long EstimatedBitrate { get; set; }
}

/// <summary>
/// Filters and scores *arr releases against a quality profile, best streaming candidate first.
/// </summary>
public static class ReleaseScorer
{
    private static readonly string[] s_videoExtensions = [".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".webm", ".mov"];
    private static readonly string[] s_streamableExtensions = [".mkv", ".webm", ".mp4"];

    /// <summary>
    /// Scores the releases: best candidate first.
    /// </summary>
    /// <param name="releases">The raw releases.</param>
    /// <param name="profile">The quality profile (allowed qualities ordered best to worst).</param>
    /// <param name="assumedRuntimeMinutes">Runtime used to estimate the bitrate (for the UI verdict).</param>
    /// <returns>The scored releases, sorted best first.</returns>
    public static List<ScoredRelease> Score(IEnumerable<ArrRelease> releases, QualityProfile? profile, int assumedRuntimeMinutes)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile is not null)
        {
            var rankIndex = 0;
            foreach (var item in profile.Items)
            {
                if (item.Quality is null)
                {
                    continue;
                }

                if (item.Allowed)
                {
                    allowed.Add(item.Quality.Name);
                    rank[item.Quality.Name] = rankIndex++;
                }
            }
        }

        var assumedRuntimeSeconds = Math.Max(assumedRuntimeMinutes, 1) * 60L;
        var scored = new List<ScoredRelease>();
        foreach (var release in releases)
        {
            if (!"torrent".Equals(release.Protocol, StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(release.MagnetUrl) && string.IsNullOrEmpty(release.DownloadUrl))
                || release.Rejections.Count > 0)
            {
                continue;
            }

            var qualityName = release.Quality.Quality.Name;
            var isAllowed = profile is null || allowed.Count == 0 || allowed.Contains(qualityName);
            var qualityRank = rank.TryGetValue(qualityName, out var r) ? r : -1;

            long score = 0;
            if (isAllowed)
            {
                // Lower rank = better quality.
                score += 1_000_000 - (qualityRank < 0 ? 500 : qualityRank * 10);
            }

            score += Math.Min(release.Seeders, 2000);
            score += release.CustomFormatScore;
            score += release.Quality.Proper ? 50 : 0;

            var titleLower = release.Title.ToLowerInvariant();
            if (!HasStreamableExtension(release.Title))
            {
                // Non streamable containers still work but probe poorly while incomplete.
                score -= 400;
            }

            if (titleLower.Contains("cam", StringComparison.Ordinal) || titleLower.Contains("screener"))
            {
                score -= 1000;
            }

            scored.Add(new ScoredRelease
            {
                Release = release,
                QualityAllowed = isAllowed,
                QualityRank = qualityRank,
                Score = score,
                EstimatedBitrate = release.Size * 8 / assumedRuntimeSeconds
            });
        }

        return scored.OrderByDescending(s => s.Score).ToList();
    }

    /// <summary>
    /// Checks whether a release title ends with a streamable container extension.
    /// </summary>
    /// <param name="title">The release title.</param>
    /// <returns>True when streamable.</returns>
    public static bool HasStreamableExtension(string title)
    {
        return s_streamableExtensions.Any(ext => title.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks whether a file is a video file.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>True when the file looks like a video.</returns>
    public static bool IsVideoFile(string name)
    {
        return s_videoExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
