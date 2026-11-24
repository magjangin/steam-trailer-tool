using System.Diagnostics;

namespace SteamTrailerTool.Core.Services;

/// <summary>
/// 다운로드한 영상/음성 트랙(fMP4)을 재인코딩 없이(-c copy) 하나의 mp4로 합친다.
/// ffmpeg 위치: FFMPEG_PATH 환경 변수 → 실행 파일 옆 ffmpeg.exe → PATH 순.
/// </summary>
public static class FfmpegMuxer
{
    private static string? _cached;
    private static bool _searched;

    public static string? Locate()
    {
        if (_searched)
        {
            return _cached;
        }

        _cached = Search();
        _searched = true;
        return _cached;
    }

    private static string? Search()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        var env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (File.Exists(env))
            {
                return env;
            }

            var inDir = Path.Combine(env, exe);
            if (File.Exists(inDir))
            {
                return inDir;
            }
        }

        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local))
        {
            return local;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH에 잘못된 문자가 섞인 항목은 건너뛴다
            }
        }

        return null;
    }

    /// <summary>video(+audio)를 output으로 mux. 실패 시 ffmpeg stderr를 담은 예외를 던진다.</summary>
    public static async Task MuxAsync(string ffmpeg, string videoPath, string? audioPath, string outputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath })
        {
            psi.ArgumentList.Add(a);
        }

        if (audioPath is not null)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(audioPath);
            foreach (var a in new[] { "-map", "0:v:0", "-map", "1:a:0" })
            {
                psi.ArgumentList.Add(a);
            }
        }

        // 출력 확장자가 .part 이므로 컨테이너를 명시한다.
        foreach (var a in new[] { "-c", "copy", "-movflags", "+faststart", "-f", "mp4", outputPath })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg를 실행하지 못했습니다.");
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 이미 종료됨
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg 병합 실패 (exit {proc.ExitCode}): {stderr.Trim()}");
        }
    }
}
