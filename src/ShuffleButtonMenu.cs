using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace TrueShuffle;

/// <summary>Reversible menu attachment to buttons bound to the native shuffle command.</summary>
public sealed class ShuffleButtonMenu : IDisposable
{
    private readonly ICommand _native;
    private readonly Func<ShuffleMode, string, Task> _apply;
    private readonly Action _normal, _open;
    private readonly Action? _end;
    private readonly Action<string> _error;
    private const string EndTag = "end";
    private readonly Dictionary<Button, Attachment> _buttons = new();
    private ShuffleMode? _selected;
    private string? _scope;
    /// <param name="end">End True Shuffle: offered while a mode is active; the queue stays as it is.</param>
    public ShuffleButtonMenu(ICommand native, Func<ShuffleMode, string, Task> apply, Action normal,
        Action open, Action<string> error, Action? end = null)
    { _native = native; _apply = apply; _normal = normal; _open = open; _error = error; _end = end; }

    /// <summary>How many shuffle buttons carry the menu right now.</summary>
    public int Count => _buttons.Count;

    /// <summary>
    /// Looking for the buttons walks the visual tree of every window. Until they are found (the
    /// windows may still be loading) that happens every second for half a minute; after that, and
    /// once they are attached, every five seconds. A window that came or went is looked at at once.
    /// </summary>
    public static TimeSpan ScanInterval(int attached, TimeSpan sinceStart)
        => attached > 0 || sinceStart >= TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1);

    /// <summary>Changes when a window is opened or closed (the mini player, say).</summary>
    public static int Fingerprint(IEnumerable<Window> windows)
    {
        var hash = new HashCode();
        foreach (var window in windows) hash.Add(RuntimeHelpers.GetHashCode(window));
        return hash.ToHashCode();
    }

    public void Refresh(IEnumerable<Visual> roots)
    {
        var buttons = roots.SelectMany(r => r.GetVisualDescendants().Prepend(r)).OfType<Button>()
            .Where(b => ReferenceEquals(b.Command, _native)).ToHashSet();
        foreach (var old in _buttons.Keys.Where(b => !buttons.Contains(b)).ToArray()) { _buttons[old].Dispose(); _buttons.Remove(old); }
        foreach (var button in buttons)
        {
            if (!_buttons.ContainsKey(button)) _buttons.Add(button, new Attachment(button, BuildMenu(), _error));
            _buttons[button].Tip(_selected == null ? "Shuffle modes (True Shuffle)" : $"True Shuffle: {Label(_selected.Value)} - {_scope}");
        }
    }

    public void Selected(ShuffleMode? mode, string? scope)
    {
        _selected = mode; _scope = scope;
        foreach (var attachment in _buttons.Values)
        {
            foreach (var item in Items(attachment.Menu))
            {
                if (item.Tag is Selection selection) item.IsChecked = mode == selection.Mode && scope == selection.Scope;
                else if (Equals(item.Tag, EndTag)) item.IsEnabled = mode != null;
            }
            attachment.Tip(mode == null ? "Shuffle modes (True Shuffle)" : $"True Shuffle: {Label(mode.Value)} - {scope}");
        }
    }

    public bool Open(Window window)
    {
        var button=_buttons.Keys.FirstOrDefault(b=>b.GetVisualAncestors().Contains(window));
        if (button == null) return false;
        _buttons[button].Menu.Open(button);
        return true;
    }

    private ContextMenu BuildMenu()
    {
        var items = new List<Control> { new MenuItem { Header = "Current queue", IsEnabled = false } };
        items.AddRange(Modes("Current queue"));
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "Open playlist", ItemsSource = Modes("Open playlist") });
        items.Add(new MenuItem { Header = "Whole library", ItemsSource = Modes("Whole library") });
        items.Add(new Separator());
        if (_end != null)
        {
            var end = new MenuItem { Header = "End True Shuffle", Tag = EndTag, IsEnabled = _selected != null };
            end.Click += (_, _) => { try { _end(); } catch (Exception e) { _error(e.GetBaseException().Message); } };
            items.Add(end);
        }
        var normal = new MenuItem { Header = "Noctis shuffle (normal)" };
        normal.Click += (_, _) => { try { _normal(); } catch (Exception e) { _error(e.GetBaseException().Message); } };
        var controls = new MenuItem { Header = "More shuffle controls…" };
        controls.Click += (_, _) => { try { _open(); } catch (Exception e) { _error(e.GetBaseException().Message); } };
        items.Add(normal); items.Add(controls);
        return new ContextMenu { ItemsSource = items };
    }

    private MenuItem[] Modes(string scope) => Enum.GetValues<ShuffleMode>().Select(mode =>
    {
        var item = new MenuItem { Header = Label(mode), ToggleType = MenuItemToggleType.CheckBox,
            Tag = new Selection(mode, scope), IsChecked = _selected == mode && _scope == scope };
        item.Click += async (_, _) =>
        {
            try { await _apply(mode, scope); }
            catch (Exception e) { _error(e.GetBaseException().Message); }
            finally { Selected(_selected, _scope); } // refuse optimistic checkmarks after a failed apply
        };
        return item;
    }).ToArray();

    private static IEnumerable<MenuItem> Items(ItemsControl control)
    {
        foreach (var item in control.Items.OfType<MenuItem>())
        { yield return item; foreach (var child in Items(item)) yield return child; }
    }
    private static string Label(ShuffleMode mode) => mode switch
    { ShuffleMode.EqualChance => "Equal chance", ShuffleMode.NoRepeats => "No repeats", ShuffleMode.Discovery => "Discovery",
      ShuffleMode.ForYou => "For you", _ => "Never played only" };
    private sealed record Selection(ShuffleMode Mode, string Scope);

    public void Dispose() { foreach (var a in _buttons.Values) a.Dispose(); _buttons.Clear(); }

    private sealed class Attachment : IDisposable
    {
        private readonly Button _button;
        private readonly ContextMenu? _previous;
        private readonly object? _previousTip;
        private readonly Action<string> _error;
        private string? _tip;
        public ContextMenu Menu { get; }
        public Attachment(Button button, ContextMenu menu, Action<string> error)
        {
            _button = button; Menu = menu; _error = error;
            _previous = button.ContextMenu; _previousTip = ToolTip.GetTip(button);
            button.SetCurrentValue(Control.ContextMenuProperty, menu);
            button.AddHandler(Button.ClickEvent, Click, RoutingStrategies.Bubble);
        }
        private void Click(object? sender, RoutedEventArgs e)
        {
            // Avalonia raises Click before executing Command; handled prevents the native
            // weighted toggle from also firing when the menu opens.
            e.Handled = true;
            try { Menu.Open(_button); } catch (Exception ex) { _error(ex.GetBaseException().Message); }
        }
        public void Tip(string text)
        {
            _tip = text;
            if (!Equals(ToolTip.GetTip(_button), text)) _button.SetCurrentValue(ToolTip.TipProperty, text); // unchanged text is not set again
        }
        public void Dispose()
        {
            _button.RemoveHandler(Button.ClickEvent, Click); Menu.Close();
            if (ReferenceEquals(_button.ContextMenu, Menu)) _button.SetCurrentValue(Control.ContextMenuProperty, _previous);
            if (Equals(ToolTip.GetTip(_button), _tip)) _button.SetCurrentValue(ToolTip.TipProperty, _previousTip);
        }
    }
}
