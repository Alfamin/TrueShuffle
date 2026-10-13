using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Noctis.Plugins;

namespace TrueShuffle;

public sealed class TrueShufflePlugin : INoctisPlugin
{
    public PluginInfo Info { get; } = new("dev.moshi.trueshuffle", "True Shuffle", "1.1.2", "moshi",
        "Equal-chance shuffle, persistent no-repeat cycles, and discovery modes for your queue, playlist or library.");
    private IPluginHost? _host;
    private IDisposable? _command;
    private ShuffleShortcut? _shortcut;
    private ShuffleWindow? _window;
    private QueueBridge? _bridge;
    private ShuffleButtonMenu? _buttons;
    private DispatcherTimer? _buttonTimer;
    private PlaybackLease? _lease;
    private QueueFrontPolicy? _priority;
    private CycleStore? _store;
    private Task _load = Task.CompletedTask, _save = Task.CompletedTask;
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private HashSet<string> _session = new(StringComparer.Ordinal);
    private string? _last, _lastPath, _historyError;
    private bool _closed, _busy, _ownChange;
    private int _epoch, _pending;
    private string? _buttonError;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _closed = false;
        _busy = false;
        _seen = new(StringComparer.Ordinal); _last = null; _lastPath = null; _historyError = null;
        int epoch = ++_epoch;
        _store = new CycleStore(host.DataDirectory);
        _load = LoadAsync(_store, epoch);
        _command = host.RegisterTrackCommand("True Shuffle…", null, (Action<TrackInfo>)(_ => Open()));
        host.Settings.Changed += SettingsChanged;
        host.NowPlaying.TrackChanged += TrackChanged;
        _shortcut = new ShuffleShortcut(
            _ => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is object main ? QueueBridge.Get(main,"Settings") : null,
            window => window.GetType().FullName is "Noctis.Views.MainWindow" or "Noctis.Views.MiniPlayerWindow",
            () => _host?.Settings.GetString("shortcut"), window =>
            {
                if (_closed) return;
                RefreshButtons(null,EventArgs.Empty);
                if (_buttons?.Open(window) != true) Open();
            }, Notice);
        _buttonTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _buttonTimer.Tick += RefreshButtons; _buttonTimer.Start();
        host.Log("ready; playback remains unchanged until Apply shuffle is clicked");
    }

    private async Task LoadAsync(CycleStore store, int epoch)
    {
        try
        {
            await _save;
            var data = await store.LoadAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { if (!_closed && epoch == _epoch) { _seen = new(data.Seen, StringComparer.Ordinal); _last = data.Last; _lastPath = data.LastPath; } });
        }
        catch (Exception ex) { if (!_closed && epoch == _epoch) _historyError = ex.Message; }
    }

    private void SettingsChanged(object? sender, string key)
    {
        if (key == "open") Open();
    }

    private void Open()
    {
        if (_closed || _host == null) return;
        if (_window != null) { _window.Activate(); return; }
        try
        {
            EnsureBridge();
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
                desktop.MainWindow?.DataContext is not object main)
                throw new InvalidOperationException("The Noctis desktop window is not ready.");
            _window = new ShuffleWindow(_host.Settings.GetString("mode"), _host.Settings.GetString("scope"),
                _host.Settings.GetBool("skipDisliked", false), ApplyAsync, () => EndSession(), ResetAsync);
            _window.Closed += (_, _) => { _window = null; ++_pending; };
            _window.Show(desktop.MainWindow);
            _ = UpdatePreviewAsync();
        }
        catch (Exception ex) { Notice(ex.GetBaseException().Message); }
    }

    private void EnsureBridge()
    {
        if (_bridge != null) return;
        if (_host == null) throw new InvalidOperationException("The plugin is stopped.");
        _ = _host.Playback; _ = _host.Library; // declared host permissions still gate access
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.DataContext is not object main) throw new InvalidOperationException("Noctis is not ready.");
        _bridge = new QueueBridge(main);
    }

    private void RefreshButtons(object? sender, EventArgs e)
    {
        if (_closed || _host == null || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow?.DataContext == null) return;
        try
        {
            EnsureBridge();
            _buttons ??= new ShuffleButtonMenu((ICommand)QueueBridge.Get(_bridge!.Player, "ToggleShuffleCommand")!,
                (mode, scope) => ApplyAsync(mode, scope, true, _host.Settings.GetBool("skipDisliked", false)),
                () => { EndSession(); ((ICommand)QueueBridge.Get(_bridge!.Player, "ToggleShuffleCommand")!).Execute(null); },
                Open, Notice);
            _buttons.Refresh(desktop.Windows);
            _buttonError = null;
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            // A window/player may still be loading. Keep retrying without repeating notices.
            if (_buttonError != message)
                Notice("Shuffle-button menu unavailable: " + message + " Use the song's True Shuffle menu instead.");
            _buttonError = message;
        }
    }

    private void Status(string text)
    { if (_window != null) _window.Status = text; else Notice(text); }

    private async Task UpdatePreviewAsync()
    {
        await _load;
        if (_window == null || _bridge == null || _closed) return;
        if (_historyError != null) _window.Status = "History could not be loaded/saved. Equal chance still works. " + _historyError;
        else _window.Status = _lease != null ? "Custom queue active. Progress is remembered as songs start." : "Choose a source and mode, then apply. Enabling the plugin alone changes nothing.";
    }

    private async Task ApplyAsync(ShuffleMode mode, string scope, bool keepCurrent, bool skipDisliked)
    {
        if (_busy || _closed || _bridge == null) return;
        int epoch = _epoch, operation = ++_pending;
        var window = _window;
        _busy = true; if (_window != null) _window.Busy = true;
        try
        {
            await _load;
            if (_closed || epoch != _epoch || operation != _pending || !ReferenceEquals(window, _window)) return;
            if (_historyError != null && mode != ShuffleMode.EqualChance)
                throw new IOException("Remembered progress is unavailable. Use Equal chance, or inspect the plugin's cycle.json before continuing.");
            var bridge = _bridge;
            var entries = bridge.Snapshot(scope);
            var upgraded = ShuffleEngine.UpgradeHistory(bridge.Snapshot("Whole library").Select(e=>e.Song),_seen,_last,_lastPath);
            _seen = new(upgraded.Seen,StringComparer.Ordinal); _last=upgraded.Last;
            var current = bridge.Current?.Song;
            string? currentKey = keepCurrent ? current?.Key : null;
            bool explicitAllowed = bridge.ExplicitAllowed;
            var remembered = current != null ? _seen.Append(current.Key).ToArray() : _seen.ToArray();
            var last = _last;
            if (_window != null) _window.Status = "Preparing the queue…";
            var plan = await Task.Run(() => ShuffleEngine.Build(entries.Select(e => e.Song)
                .Where(s => s.Remote || (!string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path))),
                mode, remembered, currentKey, last, explicitAllowed, skipDisliked));
            if (_closed || epoch != _epoch || operation != _pending || !ReferenceEquals(window, _window)) return;
            if (keepCurrent && bridge.Current?.Song.Key != currentKey)
                throw new InvalidOperationException("The playing song changed while preparing. Apply again to keep the new song.");
            if (bridge.ExplicitAllowed != explicitAllowed || !entries.Select(e => e.Song).SequenceEqual(bridge.Snapshot(scope).Select(e => e.Song)))
                throw new InvalidOperationException("The source or playback policy changed while preparing. Apply again to use the updated selection.");
            if (plan.Songs.Count == 0)
            {
                Status(mode == ShuffleMode.NeverPlayed ? "No eligible never-played songs remain in this source. Try Discovery or another source." : "No eligible upcoming songs in this source. Nothing was changed.");
                return;
            }
            var native = entries.DistinctBy(e => e.Song.Key).ToDictionary(e => e.Song.Key);
            var ordered = plan.Songs.Select(s => native[s.Key]).ToList();
            var previousSeen = new HashSet<string>(_seen, StringComparer.Ordinal);
            var previousLast = _last;
            var previousLastPath = _lastPath;
            EndSession(cancelPending: false);
            _ownChange = true;
            try
            {
                _lease = new PlaybackLease(bridge.Player);
                _session = ordered.SelectMany(e => new[]{e.Song.Key,e.Song.LegacyKey}).ToHashSet(StringComparer.Ordinal);
                if (currentKey != null) { _session.Add(currentKey); _session.Add(current!.LegacyKey); }
                _seen = new(plan.Seen, StringComparer.Ordinal);
                if (bridge.Player is INotifyPropertyChanged events) events.PropertyChanged += PlayerChanged;
                bridge.Apply(ordered, keepCurrent);
                if (QueueBridge.Get(bridge.Player,"UpNext") is System.Collections.Specialized.INotifyCollectionChanged)
                    _priority=new QueueFrontPolicy(bridge.Player, requested=>
                    {
                        foreach(var track in requested)
                        {
                            var song=bridge.Entry(track).Song;
                            _session.Add(song.Key);_session.Add(song.LegacyKey);
                        }
                        Notice($"{requested.Count} song{(requested.Count==1 ? "" : "s")} added next, ahead of the shuffle.");
                    },Notice);
            }
            catch { _seen = previousSeen; _last = previousLast; _lastPath = previousLastPath; EndSession(); throw; }
            finally { _ownChange = false; }
            MarkCurrent(persist: false);
            QueueSave();
            _buttons?.Selected(mode, scope);
            Status($"{plan.Songs.Count:N0} upcoming songs queued. " + (plan.NewCycle ? "A fresh cycle started because this source was exhausted. " : "") +
                "Repeat and similar-song Autoplay are suspended for this session; Track Radio ended.");
        }
        catch (Exception ex) { if (!_closed && epoch == _epoch && ReferenceEquals(window, _window)) Status(ex.GetBaseException().Message); }
        finally { if (epoch == _epoch) { _busy = false; if (_window != null) _window.Busy = false; } }
    }

    private void TrackChanged(object? sender, EventArgs e)
    {
        if (_lease == null || _closed) return;
        try
        {
            var current = _bridge?.Current?.Song;
            if (current != null && !_session.Contains(current.Key) && !_session.Contains(current.LegacyKey)) { EndSession(); return; }
            MarkCurrent();
        }
        catch (Exception ex) { Notice("Progress tracking stopped: " + ex.GetBaseException().Message); EndSession(); }
    }

    private void MarkCurrent(bool persist = true)
    {
        var song = _bridge?.Current?.Song;
        if (song != null && (_session.Contains(song.Key) || _session.Contains(song.LegacyKey)))
        {
            bool changed = _seen.Add(song.Key);
            changed = _seen.Add(song.LegacyKey) || changed || _last != song.Key;
            _last = song.Key;
            _lastPath = song.LegacyKey;
            if (changed && persist && !_ownChange) QueueSave();
        }
    }

    private void PlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_ownChange || _lease == null || _bridge == null) return;
        try
        {
        bool normalControls = e.PropertyName == "IsShuffleEnabled" && (bool)QueueBridge.Get(_bridge.Player, "IsShuffleEnabled")!;
        bool auto = e.PropertyName == "AutoplayEnabled" && (bool)QueueBridge.Get(_bridge.Player, "AutoplayEnabled")!;
        bool radio = e.PropertyName == "IsRadioActive" && (bool)QueueBridge.Get(_bridge.Player, "IsRadioActive")!;
        bool repeat = e.PropertyName == "RepeatMode" && QueueBridge.Get(_bridge.Player, "RepeatMode")!.ToString() != "Off";
        if (normalControls || auto || radio || repeat)
        {
            EndSession();
            Notice("True Shuffle session ended because you changed the normal playback controls.");
        }
        }
        catch (Exception ex) { Notice("Custom controls stopped: " + ex.GetBaseException().Message); EndSession(); }
    }

    private void QueueSave()
    {
        if (_historyError != null || _store == null) return;
        var snapshot = new CycleData(1, _seen.ToArray(), _last, _lastPath);
        var store = _store;
        int epoch = _epoch;
        // Chain immutable snapshots in order, so rapid Next clicks cannot persist older progress last.
        _save = _save.ContinueWith(async _ =>
        {
            try { await store.SaveAsync(snapshot); }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_closed && epoch == _epoch)
                    {
                        _historyError = ex.Message;
                        Notice("Shuffle progress could not be saved. This queue can finish; persistent modes are paused.");
                    }
                });
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task ResetAsync()
    {
        if (_closed || _busy) return;
        int epoch = _epoch;
        ++_pending;
        await _load;
        if (_closed || epoch != _epoch) return;
        if (_historyError != null) { Notice("Invalid history was preserved. Inspect cycle.json in the plugin data folder."); return; }
        _seen.Clear(); _last = null; _lastPath = null;
        if (_lease != null) MarkCurrent();
        QueueSave();
        if (_window != null) _window.Status = "Remembered cycle reset. The playing queue was kept.";
    }

    private void EndSession(bool cancelPending = true)
    {
        if (cancelPending) ++_pending;
        _priority?.Dispose(); _priority=null;
        if (_bridge?.Player is INotifyPropertyChanged events) events.PropertyChanged -= PlayerChanged;
        _session.Clear();
        var lease = _lease; _lease = null;
        _buttons?.Selected(null, null);
        try { lease?.Dispose(); }
        catch (Exception ex) { Notice("Could not restore a playback preference: " + ex.GetBaseException().Message); }
        if (_window != null) _window.Status = "Normal Noctis playback controls restored. The arranged queue is kept.";
    }

    private void Notice(string text)
    {
        try { _host?.Notify(text); _host?.Log(text); } catch { }
    }

    public void Shutdown()
    {
        _closed = true;
        ++_epoch; ++_pending;
        if (_buttonTimer != null) { _buttonTimer.Stop(); _buttonTimer.Tick -= RefreshButtons; _buttonTimer = null; }
        _buttons?.Dispose(); _buttons = null;
        _shortcut?.Dispose(); _shortcut = null;
        if (_host != null)
        {
            _host.Settings.Changed -= SettingsChanged;
            _host.NowPlaying.TrackChanged -= TrackChanged;
        }
        EndSession();
        _window?.Close(); _window = null;
        _command?.Dispose(); _command = null;
        _bridge = null; _host = null;
    }
}
