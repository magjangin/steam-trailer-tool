using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using steam_trailer_tool.ViewModels;

namespace steam_trailer_tool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    private async void SelectFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "트레일러 저장 폴더 선택",
                AllowMultiple = false
            });

            if (folders.Count > 0 && DataContext is MainWindowViewModel vm)
            {
                vm.SaveFolderPath = folders[0].Path.LocalPath;
            }
        }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.StatusMessage = $"❌ 폴더 선택 실패: {ex.Message}";
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
