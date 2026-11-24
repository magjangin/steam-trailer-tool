using SteamTrailerTool.Core.Models;
using SteamTrailerTool.Core.Services;

namespace SteamTrailerTool.Tests;

public class SteamStoreClientTests
{
    [Theory]
    [InlineData("4839390", 4839390L)]
    [InlineData(" 774171 ", 774171L)]
    [InlineData("https://store.steampowered.com/app/4839390/Muse_Dash_2/", 4839390L)]
    [InlineData("store.steampowered.com/app/774171", 774171L)]
    [InlineData("https://steamcommunity.com/app/774171/videos/", 774171L)]
    [InlineData("https://steamdb.info/app/4839390/", 4839390L)]
    [InlineData("https://store.steampowered.com/agecheck/app/1091500/", 1091500L)]
    public void ParseAppId_accepts_ids_and_urls(string input, long expected)
    {
        Assert.Equal(expected, SteamStoreClient.ParseAppId(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("muse dash")]
    [InlineData("https://store.steampowered.com/")]
    [InlineData("-5")]
    public void ParseAppId_rejects_garbage(string input)
    {
        Assert.Null(SteamStoreClient.ParseAppId(input));
    }

    [Fact]
    public void ParseAppDetails_reads_dash_and_legacy_fields()
    {
        const string json = """
            {"4839390":{"success":true,"data":{"name":"Muse Dash 2","movies":[
              {"id":257392019,"name":"md2_pv_0.1_kr","thumbnail":"https://t/1.jpg",
               "dash_av1":"https://v/dash_av1.mpd","dash_h264":"https://v/dash_h264.mpd",
               "hls_h264":"https://v/hls_264_master.m3u8","highlight":true},
              {"id":1,"name":"old","mp4":{"480":"https://o/480.mp4","max":"https://o/max.mp4"},"highlight":false}
            ]}}}
            """;

        var app = SteamStoreClient.ParseAppDetails(4839390, json);

        Assert.Equal("Muse Dash 2", app.AppName);
        Assert.Equal(2, app.Trailers.Count);
        Assert.Equal("https://v/dash_h264.mpd", app.Trailers[0].DashH264Url);
        Assert.Equal("https://v/dash_av1.mpd", app.Trailers[0].DashAv1Url);
        Assert.True(app.Trailers[0].Highlight);
        Assert.Equal("https://o/max.mp4", app.Trailers[1].LegacyMp4Url);
        Assert.True(app.Trailers[1].HasAnySource);
    }

    [Fact]
    public void ParseAppDetails_without_movies_returns_empty_list()
    {
        var app = SteamStoreClient.ParseAppDetails(10, """{"10":{"success":true,"data":{"name":"Counter-Strike"}}}""");
        Assert.Empty(app.Trailers);
    }

    [Fact]
    public void ParseAppDetails_failure_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SteamStoreClient.ParseAppDetails(1, """{"1":{"success":false}}"""));
    }

    [Fact]
    public void PickManifest_falls_back_to_other_codec()
    {
        var onlyH264 = new TrailerInfo(1, "a", null, "h", null, null, null, false);
        Assert.Equal(("h", "H264"), TrailerDownloader.PickManifest(onlyH264, VideoCodec.Av1));

        var both = onlyH264 with { DashAv1Url = "a" };
        Assert.Equal(("a", "AV1"), TrailerDownloader.PickManifest(both, VideoCodec.Av1));
    }
}
