using System.Net;
using SteamTrailerTool.Core.Models;

namespace SteamTrailerTool.Core.Services;

public sealed record DownloadOptions(
    string OutputDirectory,
    VideoCodec Codec = VideoCodec.H264,
    int? MaxHeight = null,
    int SegmentParallelism = 6);

public sealed record DownloadProgress(string Message, double Fraction);

/// <param name="Files">최종 저장된 파일 경로 (병합 성공 시 1개, ffmpeg 없으면 영상/음성 2개).</param>
public sealed record DownloadResult(IReadOnlyList<string> Files, bool Muxed, string Description);

/// <summary>
/// Steam 트레일러 DASH 스트림을 받아 mp4로 저장한다.
/// 1) MPD에서 영상(최대 해상도 이하 최고 화질)·음성(최고 비트레이트) Representation 선택
/// 2) init + 세그먼트를 순서대로 이어 붙여 트랙별 fMP4 파일 생성 (슬라이딩 윈도우 병렬 다운로드)
/// 3) ffmpeg가 있으면 -c copy로 한 파일로 병합, 없으면 .video.mp4 / .audio.m4a 그대로 남김
/// </summary>
public sealed class TrailerDownloader
{
    private const int MaxAttempts = 4;
    private readonly HttpClient _http;

    public TrailerDownloader(HttpClient? http = null)
    {
        _http = http ?? SharedHttp.Client;
    }

    public async Task<DashManifest> LoadManifestAsync(string manifestUrl, CancellationToken ct = default)
    {
        var xml = await _http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);
        return DashManifestParser.Parse(xml, new Uri(manifestUrl));
    }

    public static (string? Url, string Label) PickManifest(TrailerInfo trailer, VideoCodec codec) =>
        codec == VideoCodec.Av1
            ? trailer.DashAv1Url is not null ? (trailer.DashAv1Url, "AV1") : (trailer.DashH264Url, "H264")
            : trailer.DashH264Url is not null ? (trailer.DashH264Url, "H264") : (trailer.DashAv1Url, "AV1");

    public async Task<DownloadResult> DownloadAsync(
        TrailerInfo trailer,
        string fileStem,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        var (manifestUrl, codecLabel) = PickManifest(trailer, options.Codec);
        if (manifestUrl is null)
        {
            if (trailer.LegacyMp4Url is not null)
            {
                return await DownloadLegacyMp4Async(trailer.LegacyMp4Url, fileStem, options, progress, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"'{trailer.Name}'에는 다운로드 가능한 영상 주소가 없습니다.");
        }

        progress?.Report(new DownloadProgress("매니페스트 분석 중…", 0));
        var manifest = await LoadManifestAsync(manifestUrl, ct).ConfigureAwait(false);

        var video = manifest.SelectVideo(options.MaxHeight)
                    ?? throw new InvalidOperationException("매니페스트에 영상 트랙이 없습니다.");
        var audio = manifest.SelectAudio();

        var finalPath = Path.Combine(options.OutputDirectory, $"{fileStem} [{video.Height}p {codecLabel}].mp4");
        var videoPart = finalPath + ".video.part";
        var audioPart = finalPath + ".audio.part";
        var muxPart = finalPath + ".part";

        var tracker = new ProgressTracker(progress, video, audio, $"{video.Height}p {codecLabel}");
        var success = false;

        try
        {
            var videoTask = DownloadTrackAsync(video, videoPart, options.SegmentParallelism, tracker, ct);
            var audioTask = audio is null
                ? Task.CompletedTask
                : DownloadTrackAsync(audio, audioPart, Math.Max(2, options.SegmentParallelism / 2), tracker, ct);
            await Task.WhenAll(videoTask, audioTask).ConfigureAwait(false);

            var ffmpeg = FfmpegMuxer.Locate();
            if (ffmpeg is not null)
            {
                progress?.Report(new DownloadProgress("ffmpeg로 영상+음성 병합 중…", 0.99));
                await FfmpegMuxer.MuxAsync(ffmpeg, videoPart, audio is null ? null : audioPart, muxPart, ct).ConfigureAwait(false);
                File.Move(muxPart, finalPath, overwrite: true);
                success = true;
                progress?.Report(new DownloadProgress("완료", 1));
                return new DownloadResult([finalPath], true, video.Label);
            }

            // ffmpeg 없음: 각 트랙을 그대로 재생 가능한 fMP4로 남긴다.
            var stem = finalPath[..^".mp4".Length];
            var videoOut = stem + ".video.mp4";
            File.Move(videoPart, videoOut, overwrite: true);
            var files = new List<string> { videoOut };
            if (audio is not null)
            {
                var audioOut = stem + ".audio.m4a";
                File.Move(audioPart, audioOut, overwrite: true);
                files.Add(audioOut);
            }

            success = true;
            progress?.Report(new DownloadProgress("완료 (ffmpeg 없음: 영상/음성 분리 저장)", 1));
            return new DownloadResult(files, false, video.Label);
        }
        finally
        {
            TryDelete(videoPart);
            TryDelete(audioPart);
            if (!success)
            {
                TryDelete(muxPart);
            }
        }
    }

    private async Task DownloadTrackAsync(
        DashRepresentation rep,
        string path,
        int parallelism,
        ProgressTracker tracker,
        CancellationToken ct)
    {
        parallelism = Math.Max(1, parallelism);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var window = new Queue<Task<byte[]?>>();

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        if (rep.InitializationUrl is not null)
        {
            var init = await FetchAsync(rep.InitializationUrl, allowMissing: false, linked.Token).ConfigureAwait(false);
            await fs.WriteAsync(init!, linked.Token).ConfigureAwait(false);
        }

        var urls = rep.SegmentUrls;
        var next = 0;

        void Enqueue()
        {
            // 마지막 세그먼트는 duration 반올림 오차로 실제로 없을 수 있으므로 404를 허용한다.
            var isLast = next == urls.Count - 1;
            window.Enqueue(FetchAsync(urls[next], isLast, linked.Token));
            next++;
        }

        try
        {
            while (next < urls.Count && window.Count < parallelism)
            {
                Enqueue();
            }

            while (window.Count > 0)
            {
                var data = await window.Dequeue().ConfigureAwait(false);
                if (data is not null)
                {
                    await fs.WriteAsync(data, linked.Token).ConfigureAwait(false);
                }

                tracker.SegmentDone(rep);

                if (next < urls.Count)
                {
                    Enqueue();
                }
            }
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(window).ConfigureAwait(false);
            }
            catch
            {
                // 남은 요청의 취소/실패는 원래 예외로 대체한다
            }

            throw;
        }
    }

    private async Task<byte[]?> FetchAsync(Uri url, bool allowMissing, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (allowMissing && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    var retryable = code >= 500 || code is 408 or 429;
                    if (!retryable || attempt >= MaxAttempts)
                    {
                        throw new HttpRequestException($"HTTP {code}: {url}", null, response.StatusCode);
                    }
                }
                else
                {
                    return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < MaxAttempts &&
                                       ex is HttpRequestException { StatusCode: null } or TaskCanceledException or IOException)
            {
                // 네트워크 오류·HttpClient 타임아웃 → 재시도
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);
        }
    }

    private async Task<DownloadResult> DownloadLegacyMp4Async(
        string url,
        string fileStem,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var finalPath = Path.Combine(options.OutputDirectory, $"{fileStem} [mp4].mp4");
        var part = finalPath + ".part";
        var success = false;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total is > 0)
                    {
                        progress?.Report(new DownloadProgress("mp4 다운로드 중…", (double)done / total.Value));
                    }
                }
            }

            File.Move(part, finalPath, overwrite: true);
            success = true;
            progress?.Report(new DownloadProgress("완료", 1));
            return new DownloadResult([finalPath], true, "legacy mp4");
        }
        finally
        {
            if (!success)
            {
                TryDelete(part);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>영상·음성 세그먼트 진행을 비트레이트 가중치로 합쳐 0~0.98 범위로 보고한다 (나머지는 병합 단계).</summary>
    private sealed class ProgressTracker
    {
        private readonly IProgress<DownloadProgress>? _progress;
        private readonly string _label;
        private readonly double _totalWeight;
        private readonly Lock _lock = new();
        private double _doneWeight;

        public ProgressTracker(IProgress<DownloadProgress>? progress, DashRepresentation video, DashRepresentation? audio, string label)
        {
            _progress = progress;
            _label = label;
            _totalWeight = Weight(video) * video.SegmentUrls.Count + (audio is null ? 0 : Weight(audio) * audio.SegmentUrls.Count);
        }

        private static double Weight(DashRepresentation r) => Math.Max(1, r.Bandwidth);

        public void SegmentDone(DashRepresentation rep)
        {
            if (_progress is null)
            {
                return;
            }

            double fraction;
            lock (_lock)
            {
                _doneWeight += Weight(rep);
                fraction = _totalWeight <= 0 ? 0 : _doneWeight / _totalWeight;
            }

            _progress.Report(new DownloadProgress($"{_label} 다운로드 중…", Math.Min(0.98, fraction * 0.98)));
        }
    }
}
