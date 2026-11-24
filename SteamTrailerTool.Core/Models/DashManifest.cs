namespace SteamTrailerTool.Core.Models;

public enum DashMediaKind
{
    Video,
    Audio,
}

/// <summary>
/// MPD의 Representation 하나. 세그먼트 URL은 파싱 시점에 모두 절대 URL로 펼쳐 둔다.
/// </summary>
public sealed record DashRepresentation(
    string Id,
    DashMediaKind Kind,
    string MimeType,
    string Codecs,
    long Bandwidth,
    int Width,
    int Height,
    Uri? InitializationUrl,
    IReadOnlyList<Uri> SegmentUrls)
{
    public string Label => Kind == DashMediaKind.Video
        ? $"{Width}x{Height} {Codecs} {Bandwidth / 1000}kbps"
        : $"audio {Codecs} {Bandwidth / 1000}kbps";
}

public sealed record DashManifest(TimeSpan Duration, IReadOnlyList<DashRepresentation> Representations)
{
    public IEnumerable<DashRepresentation> Videos => Representations.Where(r => r.Kind == DashMediaKind.Video);

    public IEnumerable<DashRepresentation> Audios => Representations.Where(r => r.Kind == DashMediaKind.Audio);

    /// <summary>
    /// maxHeight 이하에서 가장 높은 해상도(동률이면 비트레이트 높은 쪽)를 고른다.
    /// 조건에 맞는 것이 없으면 가장 낮은 해상도를 돌려준다.
    /// </summary>
    public DashRepresentation? SelectVideo(int? maxHeight)
    {
        var videos = Videos.ToList();
        if (videos.Count == 0)
        {
            return null;
        }

        var fitting = videos
            .Where(v => maxHeight is null || v.Height <= maxHeight)
            .OrderByDescending(v => v.Height)
            .ThenByDescending(v => v.Bandwidth)
            .FirstOrDefault();

        return fitting ?? videos.OrderBy(v => v.Height).ThenBy(v => v.Bandwidth).First();
    }

    public DashRepresentation? SelectAudio() =>
        Audios.OrderByDescending(a => a.Bandwidth).FirstOrDefault();
}
