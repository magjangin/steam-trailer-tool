using System.Globalization;
using System.Text;
using SteamTrailerTool.Core.Models;
using SteamTrailerTool.Core.Services;

Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
    사용법: steam-trailer <상점 URL | AppID> [옵션]

    옵션:
      -o, --out <폴더>        저장 폴더 (기본: %USERPROFILE%\Downloads\SteamTrailer_Downloads)
      -c, --codec <h264|av1>  영상 코덱 (기본: h264)
      -q, --max-height <N>    최대 세로 해상도, 예: 1080, 720 (기본: 최고 화질)
      -i, --index <목록>      받을 트레일러 번호 (1부터, 쉼표 구분). 기본: 전부
      -l, --list              목록과 화질만 출력하고 다운로드하지 않음
          --lang <언어>       Steam 언어 코드 (기본: koreana)
      -h, --help              도움말

    예시:
      steam-trailer https://store.steampowered.com/app/4839390/Muse_Dash_2/
      steam-trailer 4839390 --codec av1 -q 720 -o D:\Trailers
    """;

string? input = null;
var outDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Downloads",
    "SteamTrailer_Downloads");
var codec = VideoCodec.H264;
int? maxHeight = null;
HashSet<int>? indices = null;
var listOnly = false;
var lang = "koreana";

try
{
    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 뒤에 값이 필요합니다.");

        switch (a)
        {
            case "-h" or "--help" or "/?":
                Console.WriteLine(Usage);
                return 0;
            case "-o" or "--out":
                outDir = Path.GetFullPath(Next());
                break;
            case "-c" or "--codec":
                codec = Next().ToLowerInvariant() switch
                {
                    "h264" or "avc" => VideoCodec.H264,
                    "av1" => VideoCodec.Av1,
                    var other => throw new ArgumentException($"알 수 없는 코덱: {other}"),
                };
                break;
            case "-q" or "--max-height":
                maxHeight = int.Parse(Next().TrimEnd('p', 'P'), CultureInfo.InvariantCulture);
                break;
            case "-i" or "--index":
                indices = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToHashSet();
                break;
            case "-l" or "--list":
                listOnly = true;
                break;
            case "--lang":
                lang = Next();
                break;
            default:
                if (a.StartsWith('-'))
                {
                    throw new ArgumentException($"알 수 없는 옵션: {a}");
                }

                input = a;
                break;
        }
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"오류: {ex.Message}\n");
    Console.Error.WriteLine(Usage);
    return 2;
}

var appId = SteamStoreClient.ParseAppId(input);
if (appId is null)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var store = new SteamStoreClient();
    var app = await store.GetTrailersAsync(appId.Value, lang, cts.Token);
    Console.WriteLine($"[{app.AppId}] {app.AppName} — 트레일러 {app.Trailers.Count}개");

    if (app.Trailers.Count == 0)
    {
        return 1;
    }

    var downloader = new TrailerDownloader();
    var stems = FileNameHelper.BuildStems(app);

    for (var n = 0; n < app.Trailers.Count; n++)
    {
        var t = app.Trailers[n];
        Console.WriteLine($"  {n + 1}. {t.Name}{(t.Highlight ? " ★" : "")} (id {t.Id})");

        if (listOnly)
        {
            var (url, label) = TrailerDownloader.PickManifest(t, codec);
            if (url is not null)
            {
                var manifest = await downloader.LoadManifestAsync(url, cts.Token);
                Console.WriteLine($"     길이 {manifest.Duration:mm\\:ss}, {label}: " +
                                  string.Join(", ", manifest.Videos.Select(v => $"{v.Height}p")) +
                                  (manifest.SelectAudio() is { } au ? $" / 음성 {au.Bandwidth / 1000}kbps" : ""));
            }
        }
    }

    if (listOnly)
    {
        return 0;
    }

    if (FfmpegMuxer.Locate() is null)
    {
        Console.WriteLine("⚠ ffmpeg를 찾지 못해 영상(.video.mp4)과 음성(.audio.m4a)을 따로 저장합니다.");
    }

    var options = new DownloadOptions(outDir, codec, maxHeight);
    var failed = 0;

    for (var n = 0; n < app.Trailers.Count; n++)
    {
        if (indices is not null && !indices.Contains(n + 1))
        {
            continue;
        }

        var t = app.Trailers[n];
        var lastPct = -1;
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = (int)(p.Fraction * 100);
            if (pct != lastPct)
            {
                lastPct = pct;
                Console.Write($"\r  [{n + 1}] {pct,3}% {p.Message}".PadRight(70));
            }
        });

        try
        {
            var result = await downloader.DownloadAsync(t, stems[t.Id], options, progress, cts.Token);
            Console.WriteLine();
            foreach (var f in result.Files)
            {
                Console.WriteLine($"      → {f} ({new FileInfo(f).Length / 1024.0 / 1024.0:0.0} MB)");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n취소되었습니다.");
            return 130;
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"\n  [{n + 1}] 실패: {ex.Message}");
        }
    }

    return failed == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("취소되었습니다.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"오류: {ex.Message}");
    return 1;
}
