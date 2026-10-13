using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TrueShuffle;

/// <summary>Consume the shuffle key before Noctis runs its native weighted-toggle command.</summary>
public sealed class ShuffleShortcut : IDisposable
{
    private readonly IDisposable _registration;
    private readonly Func<Window, object?> _settings;
    private readonly Func<Window, bool> _playerWindow;
    private readonly Func<string?> _choice;
    private readonly Action<Window> _open;
    private readonly Action<string> _error;
    private string? _lastError;
    public ShuffleShortcut(Func<Window, object?> settings, Func<Window, bool> playerWindow,
        Func<string?> choice, Action<Window> open, Action<string> error)
    {
        _settings=settings; _playerWindow=playerWindow; _choice=choice; _open=open; _error=error;
        _registration=InputElement.KeyDownEvent.AddClassHandler<Window>(KeyDown, RoutingStrategies.Tunnel);
    }
    public static KeyGesture? Gesture(string? choice, object? settings)
    {
        choice=choice?.Trim();
        if (string.Equals(choice?.Trim(), "off", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(choice) || string.Equals(choice, "Follow Noctis", StringComparison.OrdinalIgnoreCase))
        {
            var service=settings == null ? null : QueueBridge.Get(settings,"ShortcutService");
            var method=service?.GetType().GetMethods().SingleOrDefault(m=>m.Name=="Get" && m.GetParameters().Length==1 && m.ReturnType==typeof(KeyGesture));
            if (method != null && method.GetParameters()[0].ParameterType is var action && action.IsEnum && Enum.IsDefined(action,"ToggleShuffle"))
                return (KeyGesture?)method.Invoke(service,new[] { Enum.Parse(action,"ToggleShuffle") });
            return new(Key.S, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        }
        var gesture=KeyGesture.Parse(choice);
        if (gesture.Key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin ||
            (gesture.Key is not (>= Key.F1 and <= Key.F24) && (gesture.KeyModifiers & (KeyModifiers.Control|KeyModifiers.Alt|KeyModifiers.Meta))==0))
            throw new FormatException("Shuffle shortcut needs Ctrl, Alt or Cmd and a key. Use Follow Noctis or off to restore native behavior.");
        return gesture;
    }
    private void KeyDown(Window window, KeyEventArgs e)
    {
        if (e.Handled || !_playerWindow(window)) return;
        try
        {
            var settings=_settings(window);
            if (settings != null && QueueBridge.Get(settings,"Shortcuts") is object editing && Equals(QueueBridge.Get(editing,"IsRecording"),true)) return;
            var gesture=Gesture(_choice(),settings);
            _lastError=null;
            if (gesture == null || !gesture.Matches(e)) return;
            // Bare keys stay available to text boxes even if a native binding uses one.
            if (e.Source is TextBox && (e.KeyModifiers==KeyModifiers.None || e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Back or Key.Delete)) return;
            e.Handled=true;
            _open(window);
        }
        catch (Exception ex)
        {
            var message=ex.GetBaseException().Message;
            if (_lastError != message) _error("Shuffle shortcut unavailable: "+message);
            _lastError=message;
        }
    }
    public void Dispose()=>_registration.Dispose();
}
