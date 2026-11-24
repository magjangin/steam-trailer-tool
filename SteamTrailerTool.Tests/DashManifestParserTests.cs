using SteamTrailerTool.Core.Models;
using SteamTrailerTool.Core.Services;

namespace SteamTrailerTool.Tests;

public class DashManifestParserTests
{
    private static readonly Uri ManifestUri =
        new("https://video.akamai.steamstatic.com/store_trailers/4839390/1626706064/abc/1785901632/dash_h264.mpd?t=1786442291");

    // 2026-11 실측한 Muse Dash 2 dash_h264.mpd 구조를 축약한 것
    private const string SteamMpd = """
        <?xml version='1.0' encoding='utf-8'?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" profiles="urn:mpeg:dash:profile:isoff-live:2011" type="static" mediaPresentationDuration="PT1M15.3S" maxSegmentDuration="PT3.0S" minBufferTime="PT6.0S">
          <Period id="0" start="PT0.0S">
            <AdaptationSet id="0" contentType="video" frameRate="60/1" maxWidth="1920" maxHeight="1080">
              <Representation id="0" mimeType="video/mp4" codecs="avc1.640029" bandwidth="5800000" width="1920" height="1080">
                <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1" />
              </Representation>
              <Representation id="1" mimeType="video/mp4" codecs="avc1.640029" bandwidth="2600000" width="1280" height="720">
                <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1" />
              </Representation>
              <Representation id="3" mimeType="video/mp4" codecs="avc1.640029" bandwidth="1000000" width="640" height="360">
                <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1" />
              </Representation>
            </AdaptationSet>
            <AdaptationSet id="1" contentType="audio" lang="und">
              <Representation id="4" mimeType="audio/mp4" codecs="mp4a.40.2" bandwidth="192000" audioSamplingRate="48000">
                <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1" />
              </Representation>
            </AdaptationSet>
            <AdaptationSet id="2" mimeType="image/avif" contentType="image">
              <SegmentTemplate media="$RepresentationID$/thumbnails-$Number%05d$.avif" duration="90" startNumber="1" />
              <Representation id="dash_thumbnails" bandwidth="0" width="1280" height="864" />
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void Parses_steam_manifest_and_skips_thumbnail_tiles()
    {
        var m = DashManifestParser.Parse(SteamMpd, ManifestUri);

        Assert.Equal(TimeSpan.FromSeconds(75.3), m.Duration);
        Assert.Equal(3, m.Videos.Count());
        Assert.Single(m.Audios);
        Assert.Equal(4, m.Representations.Count);
    }

    [Fact]
    public void Expands_numbered_segments_relative_to_manifest_without_query()
    {
        var m = DashManifestParser.Parse(SteamMpd, ManifestUri);
        var best = m.SelectVideo(null)!;

        // 75.3s / 3s = 25.1 → 26 세그먼트
        Assert.Equal(26, best.SegmentUrls.Count);
        Assert.Equal(
            "https://video.akamai.steamstatic.com/store_trailers/4839390/1626706064/abc/1785901632/dash_h264/init-stream0.m4s",
            best.InitializationUrl!.ToString());
        Assert.Equal(
            "https://video.akamai.steamstatic.com/store_trailers/4839390/1626706064/abc/1785901632/dash_h264/chunk-stream0-00001.m4s",
            best.SegmentUrls[0].ToString());
        Assert.EndsWith("chunk-stream0-00026.m4s", best.SegmentUrls[^1].ToString());
    }

    [Theory]
    [InlineData(null, 1080)]
    [InlineData(1080, 1080)]
    [InlineData(900, 720)]
    [InlineData(720, 720)]
    [InlineData(480, 360)]
    [InlineData(100, 360)] // 맞는 게 없으면 가장 낮은 화질
    public void Selects_best_video_under_max_height(int? maxHeight, int expected)
    {
        var m = DashManifestParser.Parse(SteamMpd, ManifestUri);
        Assert.Equal(expected, m.SelectVideo(maxHeight)!.Height);
    }

    [Fact]
    public void Exact_division_does_not_add_extra_segment()
    {
        var xml = SteamMpd.Replace("PT1M15.3S", "PT1M15.0S");
        var m = DashManifestParser.Parse(xml, ManifestUri);
        Assert.Equal(25, m.SelectAudio()!.SegmentUrls.Count);
    }

    [Fact]
    public void Supports_segment_timeline_with_time_token()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate timescale="1000" initialization="v/init.mp4" media="v/seg-$Time$.m4s">
                    <SegmentTimeline>
                      <S t="0" d="4000" r="1" />
                      <S d="2000" />
                    </SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="v" bandwidth="100" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var m = DashManifestParser.Parse(xml, new Uri("https://x.test/a/b.mpd"));
        var v = Assert.Single(m.Videos);
        Assert.Equal(
            ["https://x.test/a/v/seg-0.m4s", "https://x.test/a/v/seg-4000.m4s", "https://x.test/a/v/seg-8000.m4s"],
            v.SegmentUrls.Select(u => u.ToString()));
        Assert.Equal("https://x.test/a/v/init.mp4", v.InitializationUrl!.ToString());
    }

    [Fact]
    public void Rejects_non_mpd_document()
    {
        Assert.Throws<FormatException>(() => DashManifestParser.Parse("<html/>", ManifestUri));
    }
}
