using Sakana.Application;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Sakana.App.ViewModels;

public sealed class GameOutputLineViewModel
{
    private const int MaxLineLength = 4000;

    public GameOutputLineViewModel(MinecraftGameProcessOutput entry, string instanceName)
    {
        var streamLabel = entry.Output.Stream == MinecraftOutputStream.StandardError ? "ERR" : "OUT";
        var safeText = entry.Output.Text.Length > MaxLineLength
            ? $"{entry.Output.Text[..MaxLineLength]} …[line truncated]"
            : entry.Output.Text;
        Text = $"{instanceName} · {entry.Output.Timestamp.ToLocalTime():HH:mm:ss} [{streamLabel}] {safeText}";
        ForegroundBrush = entry.Output.Stream == MinecraftOutputStream.StandardError
            ? new SolidColorBrush(Color.FromArgb(255, 224, 91, 98))
            : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"];
    }

    public string Text { get; }

    public Brush ForegroundBrush { get; }
}
