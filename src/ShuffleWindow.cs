using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TrueShuffle;

public sealed class ShuffleWindow : Window
{
    private readonly Button _apply;
    private readonly TextBlock _status;
    private readonly Control[] _duringApply;
    public string Status { get => _status.Text ?? ""; set => _status.Text = value; }
    public bool Busy { set { foreach (var control in _duringApply) control.IsEnabled = !value; } }
    public ShuffleWindow(string? initialMode, string? initialScope, bool skipDisliked,
        Func<ShuffleMode, string, bool, bool, Task> apply, Action stop, Func<Task> reset)
    {
        Title = "True Shuffle"; Width = 560; Height = 720; MinWidth = 460; MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var modes = new[] { "Equal chance", "No repeats", "Discovery", "Never played only" };
        var scopes = new[] { "Current queue", "Open playlist", "Whole library" };
        int defaultMode = Array.IndexOf(modes, initialMode ?? "No repeats");
        var mode = new ComboBox { ItemsSource = modes, SelectedIndex = defaultMode < 0 ? 1 : defaultMode, HorizontalAlignment = HorizontalAlignment.Stretch };
        var scope = new ComboBox { ItemsSource = scopes, SelectedIndex = Math.Max(0, Array.IndexOf(scopes, initialScope ?? "Current queue")), HorizontalAlignment = HorizontalAlignment.Stretch };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Describe() => description.Text = mode.SelectedIndex switch
        {
            0 => "Every eligible song has an equal chance. Favorites, ratings and play counts do not affect the order. Each queue contains a song once; a later reshuffle can choose it again.",
            1 => "Equal chances among songs not started in this cycle. Progress survives restarts and reshuffles. After every eligible song in this source has started, the next apply begins a fresh cycle.",
            2 => "Songs with the lowest Noctis play counts come first, with random order inside each count. Remembers cycle progress, so reshuffling does not bring the same songs back early.",
            _ => "Only songs whose Noctis play count is zero and that have not started in a True Shuffle session. An empty result stays empty; it never falls back to familiar songs."
        };
        mode.SelectionChanged += (_, _) => Describe(); Describe();
        var keep = new CheckBox { Content = "Keep the current song playing", IsChecked = true };
        var disliked = new CheckBox { Content = "Exclude songs marked as disliked", IsChecked = skipDisliked };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Choose a source and mode, then apply." };
        _apply = new Button { Content = "Apply shuffle", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        _apply.Click += async (_, _) =>
        {
            try
            {
                if (mode.SelectedIndex < 0 || mode.SelectedIndex >= modes.Length || scope.SelectedIndex < 0 || scope.SelectedIndex >= scopes.Length)
                    throw new InvalidOperationException("Choose a source and shuffle mode first.");
                await apply((ShuffleMode)mode.SelectedIndex, scopes[scope.SelectedIndex], keep.IsChecked == true, disliked.IsChecked == true);
            }
            catch (Exception ex) { Status = ex.GetBaseException().Message; }
        };
        var stopButton = new Button { Content = "Return to Noctis controls", HorizontalAlignment = HorizontalAlignment.Stretch };
        stopButton.Click += (_, _) => { try { stop(); } catch (Exception ex) { Status = ex.GetBaseException().Message; } };
        var resetButton = new Button { Content = "Reset remembered cycle…" };
        bool confirming = false;
        resetButton.Click += async (_, _) =>
        {
            if (!confirming) { confirming = true; resetButton.Content = "Confirm reset"; Status = "Press Confirm reset to forget which songs started. The current queue stays as it is."; }
            else
            {
                confirming = false; resetButton.Content = "Reset remembered cycle…";
                try { await reset(); } catch (Exception ex) { Status = ex.GetBaseException().Message; }
            }
        };
        _duringApply = new Control[] { _apply, mode, scope, keep, disliked, resetButton };
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "True Shuffle", FontSize = 26, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = "Fresh orders. Remembered progress. Your choice.", Opacity = .7 });
        content.Children.Add(new TextBlock { Text = "Music source" }); content.Children.Add(scope);
        content.Children.Add(new TextBlock { Text = "Shuffle mode" }); content.Children.Add(mode); content.Children.Add(description);
        content.Children.Add(keep); content.Children.Add(disliked);
        content.Children.Add(new TextBlock { Text = "Apply replaces the upcoming queue. Skip-when-shuffling, snoozes and Noctis's explicit-content setting are respected. The current song stays unless you uncheck the option above.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        content.Children.Add(_apply); content.Children.Add(_status); content.Children.Add(stopButton); content.Children.Add(resetButton);
        Content = new ScrollViewer { Content = content };
    }
}
