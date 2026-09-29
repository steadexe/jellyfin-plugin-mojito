using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Jellyfin.Plugin.Mojito.Streaming;
using Xunit;

namespace Jellyfin.Plugin.Mojito.Tests;

public class SessionTests
{
    [Fact]
    public void ParseInfoHash_HexMagnet()
    {
        var magnet = "magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&dn=Test";
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", MojitoSessionManager.ParseInfoHash(magnet));
    }

    [Fact]
    public void ParseInfoHash_Base32Magnet()
    {
        // Base32 of the 20 zero bytes: "AAAAAAAAAAAAAAAAAAAAAAAAAAA=" without padding.
        var hash = "A".PadLeft(32, 'A');
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Test";
        Assert.Equal(new string('0', 40), MojitoSessionManager.ParseInfoHash(magnet));
    }

    [Fact]
    public void ParseInfoHash_NoHash()
    {
        Assert.Null(MojitoSessionManager.ParseInfoHash("magnet:?dn=Test"));
        Assert.Null(MojitoSessionManager.ParseInfoHash("http://example.org"));
    }

    [Fact]
    public async Task SessionStore_RoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mojito-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var store = new StreamSessionStore(dir);
            var session = new StreamSession
            {
                Title = "Test Movie (2026)",
                Kind = "movie",
                TorrentHash = "abcdef0123456789abcdef0123456789abcdef01",
                MediaFileName = "test.mkv",
                MediaFileSize = 1234,
                State = SessionState.Ready
            };
            store.Add(session);

            // Give the debounced persistence a moment.
            await Task.Delay(300);

            var store2 = new StreamSessionStore(dir);
            var loaded = store2.Get(session.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Test Movie (2026)", loaded!.Title);
            Assert.Equal("test.mkv", loaded.MediaFileName);
            Assert.Equal(1234, loaded.MediaFileSize);
            // Ready is transient: it must not survive a restart.
            Assert.Equal(SessionState.Ended, loaded.State);

            store2.Remove(session.Id);
            await Task.Delay(300);
            var store3 = new StreamSessionStore(dir);
            Assert.Null(store3.Get(session.Id));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Session_MaxReadableOffset_IsThreadSafeLong()
    {
        var session = new StreamSession();
        session.MaxReadableOffset = 0x1_0000_0000L + 42;
        Assert.Equal(0x1_0000_0000L + 42, session.MaxReadableOffset);
    }

    [Fact]
    public void Session_MediaFilePath_CombinesDirAndFile()
    {
        var session = new StreamSession { SessionDir = "/data/downloads/abc", MediaFileName = "video.mkv" };
        var expected = Path.Combine("/data/downloads/abc", "video.mkv");
        Assert.Equal(expected, session.MediaFilePath());
    }
}
