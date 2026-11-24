using System;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamTrailerTool.Core.Models;

namespace steam_trailer_tool.ViewModels;

public partial class TrailerItemViewModel : ObservableObject, IDisposable
{
    public TrailerItemViewModel(TrailerInfo info, string fileStem, int index)
    {
        Info = info;
        FileStem = fileStem;
        Index = index;
    }

    public TrailerInfo Info { get; }

    public string FileStem { get; }

    public int Index { get; }

    public string Title => Info.Highlight ? $"★ {Info.Name}" : Info.Name;

    public string SubTitle => $"#{Index}  ·  id {Info.Id}  ·  {Sources}";

    private string Sources =>
        string.Join(" / ", new[]
        {
            Info.DashH264Url is null ? null : "H.264",
            Info.DashAv1Url is null ? null : "AV1",
            Info.LegacyMp4Url is null ? null : "mp4",
        }.Where(s => s is not null));

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusText = "대기";

    partial void OnThumbnailChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
        GC.SuppressFinalize(this);
    }
}
