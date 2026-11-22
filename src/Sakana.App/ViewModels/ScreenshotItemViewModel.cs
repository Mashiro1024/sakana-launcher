using Microsoft.UI.Xaml.Media.Imaging;

namespace Sakana.App.ViewModels;

public sealed class ScreenshotItemViewModel
{
    public ScreenshotItemViewModel(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        CapturedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToLocalTime();

        var bitmap = new BitmapImage(new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute))
        {
            DecodePixelWidth = 420
        };
        ImageSource = bitmap;
    }

    public string Path { get; }

    public string FileName { get; }

    public DateTimeOffset CapturedAt { get; }

    public BitmapImage ImageSource { get; }

    public string CapturedAtLabel => CapturedAt.ToString("yyyy/MM/dd HH:mm");
}
