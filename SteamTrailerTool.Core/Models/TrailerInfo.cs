namespace SteamTrailerTool.Core.Models;

/// <summary>Steam 상점 페이지의 트레일러 1개 (appdetails API의 movies[] 항목).</summary>
public sealed record TrailerInfo(
    long Id,
    string Name,
    string? ThumbnailUrl,
    string? DashH264Url,
    string? DashAv1Url,
    string? HlsH264Url,
    string? LegacyMp4Url,
    bool Highlight)
{
    public bool HasAnySource =>
        DashH264Url is not null || DashAv1Url is not null || LegacyMp4Url is not null;
}

/// <summary>한 게임(AppID)의 이름과 트레일러 목록.</summary>
public sealed record AppTrailers(long AppId, string AppName, IReadOnlyList<TrailerInfo> Trailers);

public enum VideoCodec
{
    H264,
    Av1,
}
