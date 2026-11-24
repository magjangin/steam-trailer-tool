using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamTrailerTool.Core.Models;
using SteamTrailerTool.Core.Services;

namespace steam_trailer_tool.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly int?[] QualityHeights = [null, 1080, 720, 480, 360];

    private readonly SteamStoreClient _store = new();
    private readonly TrailerDownloader _downloader = new();
    private CancellationTokenSource? _operationCts;
    private bool _disposed;

    public MainWindowViewModel()
    {
        SaveFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Steam Trailers");
        var ffmpeg = FfmpegMuxer.Locate();
        FfmpegStatus = ffmpeg is null
            ? "⚠ ffmpeg 없음 — 영상(.video.mp4)과 음성(.audio.m4a)이 따로 저장됩니다"
            : $"✔ ffmpeg: {ffmpeg}";
    }

    public ObservableCollection<TrailerItemViewModel> Trailers { get; } = [];

    public string[] CodecOptions { get; } = ["H.264 (호환성 좋음)", "AV1 (용량 작음)"];

    public string[] QualityOptions { get; } = ["최고 화질", "1080p 이하", "720p 이하", "480p 이하", "360p 이하"];

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Steam 상점 페이지 주소(또는 AppID)를 넣고 [분석하기]를 누르세요.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInfoLoaded;

    [ObservableProperty]
    private string _appTitle = string.Empty;

    [ObservableProperty]
    private string _saveFolderPath;

    [ObservableProperty]
    private int _selectedCodecIndex;

    [ObservableProperty]
    private int _selectedQualityIndex;

    [ObservableProperty]
    private string _ffmpegStatus;

    public bool IsIdle => !IsBusy;

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var appId = SteamStoreClient.ParseAppId(InputText);
        if (appId is null)
        {
            StatusMessage = "❌ Steam 상점 주소(…/app/숫자/…) 또는 AppID 숫자를 입력하세요.";
            return;
        }

        using var cts = BeginOperation();
        try
        {
            StatusMessage = $"🔍 AppID {appId} 트레일러 정보를 가져오는 중…";
            ProgressValue = 0;
            ClearTrailers();

            var app = await _store.GetTrailersAsync(appId.Value, ct: cts.Token);
            var stems = FileNameHelper.BuildStems(app);
            AppTitle = $"{app.AppName}  (AppID {app.AppId})";

            for (var i = 0; i < app.Trailers.Count; i++)
            {
                var t = app.Trailers[i];
                Trailers.Add(new TrailerItemViewModel(t, stems[t.Id], i + 1) { IsSelected = t.HasAnySource });
            }

            IsInfoLoaded = true;
            StatusMessage = app.Trailers.Count == 0
                ? "ℹ 이 게임에는 등록된 트레일러가 없습니다."
                : $"✅ 트레일러 {app.Trailers.Count}개를 찾았습니다. 받을 항목을 고르고 [다운로드]를 누르세요.";

            await LoadThumbnailsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "⏹ 취소되었습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 분석 실패: {ex.Message}";
        }
        finally
        {
            EndOperation(cts);
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        foreach (var item in Trailers.ToList())
        {
            if (item.Info.ThumbnailUrl is null)
            {
                continue;
            }

            try
            {
                var bytes = await _store.GetBytesAsync(item.Info.ThumbnailUrl, ct);
                using var ms = new MemoryStream(bytes);
                item.Thumbnail = new Bitmap(ms);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 썸네일은 부가 정보이므로 실패해도 무시
            }
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var targets = Trailers.Where(t => t.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "❌ 받을 트레일러를 하나 이상 선택하세요.";
            return;
        }

        var options = new DownloadOptions(
            SaveFolderPath,
            SelectedCodecIndex == 1 ? VideoCodec.Av1 : VideoCodec.H264,
            QualityHeights[Math.Clamp(SelectedQualityIndex, 0, QualityHeights.Length - 1)]);

        using var cts = BeginOperation();
        var ok = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                var baseFraction = (double)i / targets.Count;
                item.StatusText = "준비 중…";
                item.ProgressValue = 0;

                // Progress<T>는 UI 스레드의 SynchronizationContext로 마샬링된다. 1% 단위로만 갱신해 UI 부하를 줄인다.
                var lastPct = -1;
                var progress = new Progress<DownloadProgress>(p =>
                {
                    var pct = (int)(p.Fraction * 100);
                    if (pct == lastPct && p.Fraction < 1)
                    {
                        return;
                    }

                    lastPct = pct;
                    item.ProgressValue = p.Fraction * 100;
                    item.StatusText = p.Message;
                    ProgressValue = (baseFraction + p.Fraction / targets.Count) * 100;
                    StatusMessage = $"⬇ [{i + 1}/{targets.Count}] {item.Info.Name} — {p.Message}";
                });

                try
                {
                    var result = await _downloader.DownloadAsync(item.Info, item.FileStem, options, progress, cts.Token);
                    item.ProgressValue = 100;
                    item.StatusText = result.Muxed
                        ? $"✔ 완료 ({result.Description})"
                        : "✔ 완료 (영상/음성 분리 저장)";
                    ok++;
                }
                catch (OperationCanceledException)
                {
                    item.StatusText = "⏹ 취소됨";
                    throw;
                }
                catch (Exception ex)
                {
                    item.StatusText = $"❌ {ex.Message}";
                    failed++;
                }
            }

            ProgressValue = 100;
            StatusMessage = failed == 0
                ? $"🎉 {ok}개 저장 완료 → {SaveFolderPath}"
                : $"⚠ {ok}개 성공, {failed}개 실패. 항목별 상태를 확인하세요.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"⏹ 취소되었습니다. ({ok}개 저장됨)";
        }
        finally
        {
            EndOperation(cts);
        }
    }

    [RelayCommand]
    private void Cancel() => _operationCts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        var target = Trailers.Any(t => !t.IsSelected);
        foreach (var t in Trailers)
        {
            t.IsSelected = target;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(SaveFolderPath);
            Process.Start(new ProcessStartInfo(SaveFolderPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 폴더 열기 실패: {ex.Message}";
        }
    }

    private CancellationTokenSource BeginOperation()
    {
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        IsBusy = true;
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_operationCts, cts))
        {
            _operationCts = null;
        }

        IsBusy = false;
    }

    private void ClearTrailers()
    {
        foreach (var t in Trailers)
        {
            t.Dispose();
        }

        Trailers.Clear();
        IsInfoLoaded = false;
        AppTitle = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationCts?.Cancel();
        ClearTrailers();
        GC.SuppressFinalize(this);
    }
}
