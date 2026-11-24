using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamTrailerTool.Core.Models;

namespace SteamTrailerTool.Core.Services;

/// <summary>
/// Steam 상점 공개 API(appdetails)로 트레일러 목록을 가져온다.
/// 상점 페이지 HTML은 React로 렌더링되어 영상 URL이 마크업에 없으므로 API를 쓰는 것이 안정적이다.
/// </summary>
public sealed partial class SteamStoreClient
{
    private readonly HttpClient _http;

    public SteamStoreClient(HttpClient? http = null)
    {
        _http = http ?? SharedHttp.Client;
    }

    [GeneratedRegex(@"/app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AppPathRegex();

    /// <summary>
    /// "4839390", "https://store.steampowered.com/app/4839390/Muse_Dash_2/",
    /// steamcommunity.com/app/…, steamdb.info/app/… 같은 입력에서 AppID를 뽑는다.
    /// </summary>
    public static long? ParseAppId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var direct) && direct > 0)
        {
            return direct;
        }

        var m = AppPathRegex().Match(text);
        if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        return null;
    }

    public async Task<AppTrailers> GetTrailersAsync(long appId, string language = "koreana", CancellationToken ct = default)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic,movies&l={Uri.EscapeDataString(language)}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Steam API 요청 제한(429)에 걸렸습니다. 잠시 후 다시 시도하세요.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseAppDetails(appId, json);
    }

    internal static AppTrailers ParseAppDetails(long appId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var key = appId.ToString(CultureInfo.InvariantCulture);

        if (!doc.RootElement.TryGetProperty(key, out var entry) ||
            !entry.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !entry.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException(
                $"AppID {appId}의 상점 정보를 가져오지 못했습니다. (존재하지 않거나 지역 제한/비공개 앱일 수 있습니다)");
        }

        var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {appId}" : $"App {appId}";
        var trailers = new List<TrailerInfo>();

        if (data.TryGetProperty("movies", out var movies) && movies.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in movies.EnumerateArray())
            {
                trailers.Add(new TrailerInfo(
                    Id: m.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                    Name: Str(m, "name") ?? "trailer",
                    ThumbnailUrl: Str(m, "thumbnail"),
                    DashH264Url: Str(m, "dash_h264"),
                    DashAv1Url: Str(m, "dash_av1"),
                    HlsH264Url: Str(m, "hls_h264"),
                    // 2025년 이전 형식: { "mp4": { "480": "...", "max": "..." } }
                    LegacyMp4Url: m.TryGetProperty("mp4", out var mp4) && mp4.ValueKind == JsonValueKind.Object
                        ? Str(mp4, "max") ?? Str(mp4, "480")
                        : null,
                    Highlight: m.TryGetProperty("highlight", out var h) && h.ValueKind == JsonValueKind.True));
            }
        }

        return new AppTrailers(appId, name, trailers);
    }

    public Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default) =>
        _http.GetByteArrayAsync(url, ct);

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>앱 전체에서 공유하는 HttpClient (소켓 고갈 방지, 연결 재사용).</summary>
public static class SharedHttp
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 16,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamTrailerTool/1.0");
        return client;
    }
}
