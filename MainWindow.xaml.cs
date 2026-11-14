using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace classify;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ImageFile> _images = [];
    private readonly string _settingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Classify", "settings.json");
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private bool _loadingSettings;
    private bool _isDragging;
    private bool _isBusy;
    private int _displayRequestId;
    private Point _dragStart;
    private double _fitScale = 1;
    private double _zoom = 1;

    public MainWindow()
    {
        InitializeComponent();
        ImageList.ItemsSource = _images;
        DisplayedImage.RenderTransform = new TransformGroup { Children = new TransformCollection { _scale, _translate } };
        LoadSettings();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile));
            if (settings is null) return;
            _loadingSettings = true;
            WorkPathText.Text = settings.WorkPath;
            OkPathText.Text = settings.OkPath;
            NgPathText.Text = settings.NgPath;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取路径配置失败：{exception.Message}";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            var settings = new AppSettings(WorkPathText.Text.Trim(), OkPathText.Text.Trim(), NgPathText.Text.Trim());
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存路径配置失败：{exception.Message}";
        }
    }

    private void PathText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSettings) SaveSettings();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => SaveSettings();

    private void ChooseWorkFolder_Click(object sender, RoutedEventArgs e) => ChooseFolder(WorkPathText);
    private void ChooseOkFolder_Click(object sender, RoutedEventArgs e) => ChooseFolder(OkPathText);
    private void ChooseNgFolder_Click(object sender, RoutedEventArgs e) => ChooseFolder(NgPathText);

    private void ChooseFolder(TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "选择文件夹", Multiselect = false };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) await RefreshImagesAsync();
    }

    private async Task<bool> RefreshImagesAsync(string? preferredPath = null, int? preferredIndex = null, bool manageBusy = true)
    {
        var workPath = WorkPathText.Text.Trim();
        if (!Directory.Exists(workPath))
        {
            _images.Clear();
            ImageList.SelectedItem = null;
            await ShowCurrentImageAsync();
            StatusText.Text = "请先设置有效的工作路径。";
            return false;
        }

        var selectedPath = preferredPath ?? (ImageList.SelectedItem as ImageFile)?.FullPath;
        if (manageBusy) SetBusy(true);
        StatusText.Text = "正在后台扫描工作路径…";
        try
        {
            var result = await Task.Run(() => ScanImages(workPath));
            _images.Clear();
            foreach (var image in result.Images) _images.Add(image);
            var selected = selectedPath is null ? null : _images.FirstOrDefault(image =>
                string.Equals(image.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                ImageList.SelectedItem = selected;
            }
            else if (_images.Count > 0)
            {
                ImageList.SelectedIndex = Math.Clamp(preferredIndex ?? 0, 0, _images.Count - 1);
            }
            else
            {
                ImageList.SelectedItem = null;
            }

            ImageCountText.Text = $"{_images.Count} 张图片";
            StatusText.Text = result.Unreadable == 0
                ? "列表已与工作路径同步。"
                : $"已加载 {_images.Count} 张图片，跳过 {result.Unreadable} 个无法解码的文件。";
            await ShowCurrentImageAsync();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _images.Clear();
            ImageList.SelectedItem = null;
            ImageCountText.Text = "0 张图片";
            await ShowCurrentImageAsync();
            StatusText.Text = $"扫描失败，已清空可能过期的列表：{exception.Message}";
            return false;
        }
        finally
        {
            if (manageBusy) SetBusy(false);
        }

    }

    private static ScanResult ScanImages(string workPath)
    {
        var found = new List<ImageFile>();
        var unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(workPath, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count > 0) found.Add(new ImageFile(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException or FormatException)
            {
                unreadable++;
            }
        }
        return new ScanResult(found, unreadable);
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RefreshButton.IsEnabled = !busy;
        ImageList.IsEnabled = !busy;
        PreviousButton.IsEnabled = !busy;
        NextButton.IsEnabled = !busy;
        FitButton.IsEnabled = !busy;
        RightControls.IsEnabled = !busy;
    }

    private async void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isBusy) await ShowCurrentImageAsync();
    }

    private async Task ShowCurrentImageAsync()
    {
        var requestId = ++_displayRequestId;
        if (ImageList.SelectedItem is not ImageFile selected)
        {
            DisplayedImage.Source = null;
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "请选择工作路径并刷新图片";
            ResetView();
            ImageCountText.Text = $"{_images.Count} 张图片";
            return;
        }

        try
        {
            var bitmap = await Task.Run(() => LoadBitmap(selected.FullPath));
            if (requestId != _displayRequestId) return;
            DisplayedImage.Source = bitmap;
            DisplayedImage.Width = bitmap.PixelWidth;
            DisplayedImage.Height = bitmap.PixelHeight;
            EmptyHint.Visibility = Visibility.Collapsed;
            ImageCountText.Text = $"{ImageList.SelectedIndex + 1} / {_images.Count}";
            StatusText.Text = selected.DisplayName;
            FitImage();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException or FormatException)
        {
            if (requestId != _displayRequestId) return;
            DisplayedImage.Source = null;
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "图片无法读取，请刷新列表";
            StatusText.Text = $"读取图片失败：{exception.Message}";
        }
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames[0];
        bitmap.Freeze();
        return bitmap;
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (ImageList.SelectedIndex > 0) ImageList.SelectedIndex--;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (ImageList.SelectedIndex >= 0 && ImageList.SelectedIndex < _images.Count - 1) ImageList.SelectedIndex++;
    }

    private async void MoveToOk_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) await MoveCurrentImageAsync(OkPathText.Text.Trim());
    }

    private async void MoveToNg_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) await MoveCurrentImageAsync(NgPathText.Text.Trim());
    }

    private async Task MoveCurrentImageAsync(string destinationFolder)
    {
        if (ImageList.SelectedItem is not ImageFile selected) return;
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            StatusText.Text = "请先设置目标路径。";
            return;
        }

        string sourceFolder;
        string targetFolder;
        try
        {
            sourceFolder = Path.GetFullPath(WorkPathText.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            targetFolder = Path.GetFullPath(destinationFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusText.Text = $"目标路径无效：{exception.Message}";
            return;
        }

        if (string.Equals(sourceFolder, targetFolder, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "目标路径不能与工作路径相同。";
            return;
        }

        var selectedIndex = ImageList.SelectedIndex;
        SetBusy(true);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(targetFolder);
                var destination = Path.Combine(targetFolder, Path.GetFileName(selected.FullPath));
                if (File.Exists(destination)) destination = GetUniqueDestination(targetFolder, Path.GetFileName(selected.FullPath));
                File.Move(selected.FullPath, destination);
            });
            _images.Remove(selected);
            var synchronized = await RefreshImagesAsync(preferredIndex: selectedIndex, manageBusy: false);
            StatusText.Text = synchronized
                ? $"已移动到：{targetFolder}，列表已同步。"
                : "图片已移动，但列表同步失败；请检查工作路径后重试刷新。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = $"移动失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string GetUniqueDestination(string folder, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(folder, $"{name} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || ImageList.SelectedItem is not ImageFile selected) return;
        if (MessageBox.Show(this, $"确定永久删除图片“{selected.DisplayName}”吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var selectedIndex = ImageList.SelectedIndex;
        SetBusy(true);
        try
        {
            await Task.Run(() => File.Delete(selected.FullPath));
            _images.Remove(selected);
            var synchronized = await RefreshImagesAsync(preferredIndex: selectedIndex, manageBusy: false);
            StatusText.Text = synchronized
                ? "图片已删除，列表已与工作路径同步。"
                : "图片已删除，但列表同步失败；请检查工作路径后重试刷新。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = $"删除失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ImageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DisplayedImage.Source is not null) FitImage();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitImage();

    private void FitImage()
    {
        if (DisplayedImage.Source is null || ImageCanvas.ActualWidth <= 0 || ImageCanvas.ActualHeight <= 0) return;
        _fitScale = Math.Min(ImageCanvas.ActualWidth / DisplayedImage.Width, ImageCanvas.ActualHeight / DisplayedImage.Height);
        _zoom = 1;
        _scale.ScaleX = _scale.ScaleY = _fitScale;
        _translate.X = (ImageCanvas.ActualWidth - DisplayedImage.Width * _fitScale) / 2;
        _translate.Y = (ImageCanvas.ActualHeight - DisplayedImage.Height * _fitScale) / 2;
    }

    private void ResetView()
    {
        _fitScale = _zoom = 1;
        _scale.ScaleX = _scale.ScaleY = 1;
        _translate.X = _translate.Y = 0;
    }

    private void ImageCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DisplayedImage.Source is null) return;
        var position = e.GetPosition(ImageCanvas);
        var oldScale = _fitScale * _zoom;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.1, 20);
        var newScale = _fitScale * _zoom;
        _translate.X = position.X - (position.X - _translate.X) * newScale / oldScale;
        _translate.Y = position.Y - (position.Y - _translate.Y) * newScale / oldScale;
        _scale.ScaleX = _scale.ScaleY = newScale;
        e.Handled = true;
    }

    private void ImageCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DisplayedImage.Source is null) return;
        _isDragging = true;
        _dragStart = e.GetPosition(ImageCanvas);
        ImageCanvas.CaptureMouse();
        ImageCanvas.Cursor = Cursors.SizeAll;
    }

    private void ImageCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ImageCanvas.ReleaseMouseCapture();
        ImageCanvas.Cursor = Cursors.Arrow;
    }

    private void ImageCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var current = e.GetPosition(ImageCanvas);
        _translate.X += current.X - _dragStart.X;
        _translate.Y += current.Y - _dragStart.Y;
        _dragStart = current;
    }

    private sealed record ImageFile(string FullPath)
    {
        public string DisplayName => Path.GetFileName(FullPath);
    }

    private sealed record ScanResult(List<ImageFile> Images, int Unreadable);
    private sealed record AppSettings(string WorkPath, string OkPath, string NgPath);
}












