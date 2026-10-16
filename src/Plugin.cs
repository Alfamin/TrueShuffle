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
    public PluginInfo Info { get; } = new("dev.moshi.trueshuffle", "True Shuffle", "1.4.2", "moshi",
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
    private bool _toldPlayNow, _toldSwitch; // the notices that explain a song played from a list are shown once per run
    private volatile bool _saveFailing; // the last attempt to save progress failed (history that could not be LOADED is _historyError)
    private int _epoch, _pending, _historyVersion; // _historyVersion changes whenever the remembered history is replaced or reset
    private string? _buttonError;
    private DateTime _buttonsScanned = DateTime.MinValue, _buttonsSince = DateTime.UtcNow;
    private int _windowsSeen;
    // A custom session puts only a window of its plan into Noctis's queue, and adds the next window
    // when the queue is down to _refillAt songs (the tests use small numbers).
    private RollingQueue? _roll;
    private Task _refill = Task.CompletedTask;
    private Task _apply = Task.CompletedTask;
    private int _windowSize = 200, _refillAt = 20;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _closed = false;
        _busy = false;
        _seen = new(StringComparer.Ordinal); _last = null; _lastPath = null; _historyError = null; _saveFailing = false; _toldPlayNow = false; _toldSwitch = false;
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
        _buttonsScanned = DateTime.MinValue; _buttonsSince = DateTime.UtcNow; _windowsSeen = 0;
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
                Open, Notice, EndByUser);
            // Finding the buttons walks the visual tree of every window, so a tick does that only when a
            // window came or went (the mini player) or when the pause is over; the shortcut always looks.
            var now = DateTime.UtcNow;
            var windows = ShuffleButtonMenu.Fingerprint(desktop.Windows);
            if (sender != null && windows == _windowsSeen && now - _buttonsScanned < ShuffleButtonMenu.ScanInterval(_buttons.Count, now - _buttonsSince)) return;
            _windowsSeen = windows; _buttonsScanned = now;
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
        if (_closed || _bridge == null) return;
        int epoch = _epoch, operation = ++_pending;
        var window = _window;
        // A second menu click supersedes preparation of the first; it must not silently disappear.
        var previous = _apply;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _apply = complete.Task;
        try
        {
            await previous;
            if (_closed || epoch != _epoch || operation != _pending || !ReferenceEquals(window, _window)) return;
            await ApplyCoreAsync(mode, scope, keepCurrent, skipDisliked, epoch, operation, window);
        }
        finally { complete.TrySetResult(); }
    }

    private async Task ApplyCoreAsync(ShuffleMode mode, string scope, bool keepCurrent, bool skipDisliked,
        int epoch, int operation, ShuffleWindow? window)
    {
        _busy = true; if (_window != null) _window.Busy = true;
        try
        {
            await _load;
            await _refill; // a plan the old session is still making is finished first, so the two never meet
            if (_closed || epoch != _epoch || operation != _pending || !ReferenceEquals(window, _window)) return;
            if (_historyError != null && mode != ShuffleMode.EqualChance)
                throw new IOException("Remembered progress is unavailable. Use Equal chance, or inspect the plugin's cycle.json before continuing.");
            var bridge = _bridge!;
            // The native collections may only be read here, on the UI thread, and that is cheap. What
            // costs is each song's file identity (it opens the file): that, the history upgrade, the
            // shuffle and the session keys are all worked out on a thread of our own.
            var sourceSnapshot = bridge.SnapshotRaw(scope);
            var libraryRaw = scope == "Whole library" ? sourceSnapshot : bridge.SnapshotRaw("Whole library");
            var continuing = scope == "Current queue" && _lease != null ? _roll : null;
            var sourceScope = continuing?.Scope ?? scope;
            var openPlaylist = continuing?.Playlist ?? (scope == "Open playlist" ? bridge.OpenPlaylistId : null);
            var raw = sourceSnapshot;
            if (continuing != null)
            {
                // The native queue holds only a window of the source. Changing mode must retain
                // the unheard songs beyond it, even if its window has drained in the meantime.
                var byId = libraryRaw.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
                raw = continuing.Members == null ? libraryRaw : continuing.Members
                    .Select(m => byId.TryGetValue(m.Id, out var fresh) ? fresh : m.Remote ? m : null).OfType<RawTrack>().ToList();
            }
            var current = bridge.Current?.Song;
            string? currentKey = keepCurrent ? current?.Key : null;
            bool explicitAllowed = bridge.ExplicitAllowed;
            var known = _seen.ToArray(); var knownLast = _last; var knownLastPath = _lastPath;
            if (_window != null) _window.Status = "Preparing the queue…";
            var prepared = await Task.Run(() =>
            {
                var identities = new Dictionary<string, string>(StringComparer.Ordinal);
                var entries = raw.Select(r => r.Entry(identities)).ToList();
                var library = ReferenceEquals(libraryRaw, raw) ? entries : libraryRaw.Select(r => r.Entry(identities)).ToList();
                var history = ShuffleEngine.UpgradeHistory(library.Select(e => e.Song), known, knownLast, knownLastPath);
                var remembered = current != null ? history.Seen.Append(current.Key).ToArray() : history.Seen;
                var built = ShuffleEngine.Build(entries.Select(e => e.Song)
                    .Where(s => s.Remote || (!string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path))),
                    mode, remembered, currentKey, history.Last, explicitAllowed, skipDisliked);
                var native = entries.DistinctBy(e => e.Song.Key).ToDictionary(e => e.Song.Key);
                var queue = built.Songs.Select(s => native[s.Key]).ToList();
                var keys = queue.SelectMany(e => new[] { e.Song.Key, e.Song.LegacyKey }).ToHashSet(StringComparer.Ordinal);
                return (history, plan: built, ordered: queue, session: keys, identities);
            });
            if (_closed || epoch != _epoch || operation != _pending || !ReferenceEquals(window, _window)) return;
            var upgraded = prepared.history; var plan = prepared.plan; var ordered = prepared.ordered;
            _seen = new(upgraded.Seen,StringComparer.Ordinal); _last=upgraded.Last; _historyVersion++;
            if (keepCurrent && bridge.Current?.Song.Key != currentKey)
                throw new InvalidOperationException("The playing song changed while preparing. Apply again to keep the new song.");
            if (bridge.ExplicitAllowed != explicitAllowed || (continuing != null
                ? !ReferenceEquals(_roll, continuing) || !libraryRaw.SequenceEqual(bridge.SnapshotRaw("Whole library"))
                : !sourceSnapshot.SequenceEqual(bridge.SnapshotRaw(scope))))
                throw new InvalidOperationException("The source or playback policy changed while preparing. Apply again to use the updated selection.");
            if (plan.Songs.Count == 0)
            {
                Status(mode == ShuffleMode.NeverPlayed ? "No eligible never-played songs remain in this source. Try Discovery or another source."
                    : scope == "Current queue" && continuing == null ? "This queue has no other eligible songs. Choose Whole library or Open playlist in the shuffle menu."
                    : "No eligible upcoming songs in this source. Nothing was changed.");
                return;
            }
            var previousSeen = new HashSet<string>(_seen, StringComparer.Ordinal);
            var previousLast = _last;
            var previousLastPath = _lastPath;
            var manual = _priority?.Manual(bridge.Upcoming().Select(t => t.Native)) ?? new List<object>();
            var manualEntries = manual.Select(bridge.Entry).ToList();
            var manualKeys = manualEntries.Select(t => t.Song.Key).ToHashSet(StringComparer.Ordinal);
            ordered = ordered.Where(t => !manualKeys.Contains(t.Song.Key)).ToList();
            // Only a window of the plan goes into Noctis's queue; the rest waits in _roll.
            var first = ordered.Count > _windowSize ? ordered.GetRange(0, _windowSize) : ordered;
            EndSession(cancelPending: false);
            _ownChange = true;
            try
            {
                _lease = new PlaybackLease(bridge.Player);
                _session = prepared.session;
                foreach (var entry in manualEntries) { _session.Add(entry.Song.Key); _session.Add(entry.Song.LegacyKey); }
                if (currentKey != null) { _session.Add(currentKey); _session.Add(current!.LegacyKey); }
                _seen = new(plan.Seen, StringComparer.Ordinal);
                if (bridge.Player is INotifyPropertyChanged events) events.PropertyChanged += PlayerChanged;
                bridge.Apply(manualEntries.Concat(first).ToList(), keepCurrent);
                if (QueueBridge.Get(bridge.Player,"UpNext") is System.Collections.Specialized.INotifyCollectionChanged)
                    _priority=new QueueFrontPolicy(bridge.Player, requested=>
                    {
                        foreach(var track in requested)
                        {
                            var song=bridge.Entry(track).Song;
                            _session.Add(song.Key);_session.Add(song.LegacyKey);
                        }
                        Notice($"{requested.Count} song{(requested.Count==1 ? "" : "s")} queued ahead of the shuffle.");
                    },Notice,OnQueueReplaced,OnPlayedNow,manual);
                _roll = new RollingQueue(mode, sourceScope, skipDisliked, sourceScope == "Whole library" ? null : raw, ordered, first.Count, prepared.identities) { Playlist = openPlaylist };
            }
            catch { _seen = previousSeen; _last = previousLast; _lastPath = previousLastPath; EndSession(); throw; }
            finally { _ownChange = false; }
            MarkCurrent(persist: false);
            QueueSave();
            _buttons?.Selected(mode, scope);
            Status($"{manual.Count + first.Count:N0} upcoming songs queued; more are added as you listen" + (mode == ShuffleMode.NeverPlayed ? " until no never-played song is left. " : ". ") +
                (plan.NewCycle ? "A fresh cycle started because this source was exhausted. " : "") +
                "Repeat and similar-song Autoplay are suspended for this session; Track Radio ended.");
            WatchQueue();
        }
        catch (Exception ex) { if (!_closed && epoch == _epoch && ReferenceEquals(window, _window)) Status(ex.GetBaseException().Message); }
        finally { if (epoch == _epoch) { _busy = false; if (_window != null) _window.Busy = false; } }
    }

    // ── the rolling queue: more songs are added as the upcoming queue runs low ──

    private bool Active(RollingQueue roll, int epoch)
        => !_closed && epoch == _epoch && ReferenceEquals(_roll, roll) && _lease != null && _bridge != null;

    /// <summary>Looks at the queue once the player has finished with the song change that caused it.</summary>
    private void WatchQueue()
    {
        var roll = _roll;
        if (roll == null || roll.Busy || roll.Finished) return;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_roll, roll) && !roll.Busy) _refill = ContinueAsync(roll); }, DispatcherPriority.Background);
    }

    private async Task ContinueAsync(RollingQueue roll)
    {
        if (roll.Busy || roll.Finished || _busy || DateTime.UtcNow < roll.NotBefore) return; // while a shuffle is being applied, that one decides (and looks again when it is done)
        roll.Busy = true;
        int epoch = _epoch;
        try
        {
            for (int round = 0; round < 3 && Active(roll, epoch); round++)
            {
                var bridge = _bridge!;
                var upcoming = bridge.Upcoming();
                if (upcoming.Count > _refillAt) return;
                var batch = TakeFromPlan(roll, bridge, upcoming);
                if (batch.Count > 0)
                {
                    // Behind everything that is upcoming; the current song, the order of the queue and
                    // what the user put next are left alone, and a song counts as heard only when it starts.
                    void Add() => bridge.AppendUpcoming(batch);
                    if (_priority != null) _priority.Own(Add); else Add();
                    if (upcoming.Count + batch.Count > _refillAt) return;
                }
                if (roll.Remaining == 0 && !await PlanNextAsync(roll, bridge, epoch)) return;
            }
        }
        catch (Exception ex)
        {
            roll.Finished = true;
            if (!_closed && epoch == _epoch) Notice("More songs could not be added (" + ex.GetBaseException().Message + "). The songs already queued play on.");
        }
        finally { roll.Busy = false; }
    }

    /// <summary>The next songs of the plan that are still wanted: not gone from the library, not already upcoming, not heard since, and still allowed by the mode.</summary>
    private List<TrackEntry> TakeFromPlan(RollingQueue roll, QueueBridge bridge, List<RawTrack> upcoming)
    {
        var library = new Dictionary<string, RawTrack>(StringComparer.Ordinal);
        foreach (var track in bridge.SnapshotRaw("Whole library")) library.TryAdd(track.Id, track);
        var queued = new HashSet<string>(upcoming.Select(t => t.Id), StringComparer.Ordinal);
        if (bridge.CurrentRaw is { } playing) queued.Add(playing.Id);
        bool explicitAllowed = bridge.ExplicitAllowed; var now = DateTime.UtcNow;
        var batch = new List<TrackEntry>();
        while (batch.Count < _windowSize && roll.Remaining > 0)
        {
            var entry = roll.Plan[roll.Next++];
            var song = entry.Song;
            library.TryGetValue(song.Id, out var fresh);
            if (fresh == null && !song.Remote) continue;   // no longer in the library
            if (!queued.Add(song.Id)) continue;            // already upcoming (or playing)
            if (fresh != null) song = song with { PlayCount = fresh.PlayCount, Skip = fresh.Skip, Explicit = fresh.Explicit, Disliked = fresh.Disliked, SnoozedUntil = fresh.SnoozedUntil };
            if (!ShuffleEngine.StillWanted(song, roll.Mode, _seen, explicitAllowed, roll.SkipDisliked, now)) continue;
            batch.Add(new TrackEntry(fresh?.Native ?? entry.Native, song));
        }
        return batch;
    }

    /// <summary>The plan is used up: the next one, with the same mode and source. False when there is none (the session then plays out what is queued).</summary>
    private async Task<bool> PlanNextAsync(RollingQueue roll, QueueBridge bridge, int epoch)
    {
        var upcoming = bridge.Upcoming();
        var current = bridge.CurrentRaw;
        var library = bridge.SnapshotRaw("Whole library");
        IReadOnlyList<RawTrack> source = library;
        if (roll.Members != null)
        {
            var byId = new Dictionary<string, RawTrack>(StringComparer.Ordinal);
            foreach (var track in library) byId.TryAdd(track.Id, track);
            source = roll.Members.Select(m => byId.TryGetValue(m.Id, out var fresh) ? fresh : m.Remote ? m : null).OfType<RawTrack>().ToList();
        }
        bool explicitAllowed = bridge.ExplicitAllowed; var mode = roll.Mode; bool skipDisliked = roll.SkipDisliked;
        var known = _seen.ToArray(); var last = _last; int history = _historyVersion;
        var planned = await Task.Run(() =>
        {
            var identities = roll.Identities;
            var entries = source.Select(r => r.Entry(identities)).ToList();
            var waiting = upcoming.Select(r => r.Entry(identities).Song.Key).ToHashSet(StringComparer.Ordinal);
            var playing = current?.Entry(identities).Song.Key;
            var remembered = playing != null ? known.Append(playing).ToArray() : known;
            var built = ShuffleEngine.Build(entries.Select(e => e.Song)
                .Where(s => s.Remote || (!string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path))),
                mode, remembered, playing, last, explicitAllowed, skipDisliked, upcoming: waiting);
            var native = entries.DistinctBy(e => e.Song.Key).ToDictionary(e => e.Song.Key);
            var queue = built.Songs.Select(s => native[s.Key]).ToList();
            var keys = queue.SelectMany(e => new[] { e.Song.Key, e.Song.LegacyKey }).ToHashSet(StringComparer.Ordinal);
            return (plan: built, ordered: queue, session: keys);
        });
        if (!Active(roll, epoch)) return false;
        if (history != _historyVersion) return false; // the cycle was reset meanwhile: a plan made from the old history is not used
        if (planned.plan.Songs.Count == 0)
        {
            if (mode == ShuffleMode.NeverPlayed)
            {
                roll.Finished = true;
                Notice("No never-played songs are left in this source. The queue ends after the songs already upcoming.");
            }
            else roll.NotBefore = DateTime.UtcNow.AddSeconds(10); // everything eligible is queued or playing: ask again a little later
            return false;
        }
        // Take over what the plan changed in the history (a fresh cycle forgets the old one), not the
        // whole set: a song that started while the plan was being made stays remembered.
        var kept = planned.plan.Seen as ISet<string> ?? planned.plan.Seen.ToHashSet(StringComparer.Ordinal);
        _seen.ExceptWith(known.Where(k => !kept.Contains(k)));
        _seen.UnionWith(planned.plan.Seen);
        if (planned.plan.NewCycle)
        {
            QueueSave();
            if (_window != null) _window.Status = "A fresh cycle started: every song in this source has been played.";
        }
        _session.UnionWith(planned.session);
        roll.Plan = planned.ordered; roll.Next = 0;
        return true;
    }

    /// <summary>The user cleared the upcoming queue or replaced it: that is the end of the session (and of the continuation), not something to fill up again.</summary>
    private void OnQueueReplaced()
    {
        if (_closed || _lease == null) return;
        EndSession();
        Notice("True Shuffle session ended because the queue was cleared or replaced. The current song keeps playing.");
    }

    /// <summary>
    /// Noctis plays a song by making the list it was picked from the new queue (the queue panel drops the songs above it, the
    /// history plays it alone). During a session the song plays now, and the songs the user queued themselves stay first.
    /// Picked from what is being shuffled (or from the queue, the history, or alone), the shuffle that was upcoming stays
    /// behind it, in its order, without a second copy of that song. Picked from another playlist, album, artist or the Songs
    /// page, that list is shuffled from now on, in the same mode (<see cref="SwitchAsync"/>).
    /// </summary>
    private void OnPlayedNow(IReadOnlyList<object> before)
    {
        if (_closed || _lease == null || _bridge == null || _priority == null || _roll == null) return;
        var bridge = _bridge; var policy = _priority; var roll = _roll;
        try
        {
            var playing = QueueBridge.Get(bridge.Player, "CurrentTrack");
            bool NotPlaying(object track) => playing == null || !QueueFrontPolicy.Same(track, playing);
            var list = bridge.PlayedList(); // before ReplaceUpcoming, which forgets the songs above the picked one
            var next = Context(roll, before, playing, list, bridge);
            // A Shuffle button of a list turns Noctis's own shuffle on as it starts the list: True Shuffle shuffles it instead.
            if (QueueBridge.Get(bridge.Player, "IsShuffleEnabled") is true)
                policy.Own(() => ((System.Windows.Input.ICommand)QueueBridge.Get(bridge.Player, "ToggleShuffleCommand")!).Execute(null));
            if (next == null) policy.Own(() => bridge.ReplaceUpcoming(before.Where(NotPlaying).ToList()));
            else
            {
                // Until the new shuffle is ready: the user's songs, then the rest of that list as Noctis queued it.
                var mine = policy.Manual(before).Where(NotPlaying).ToList();
                var rest = bridge.Upcoming().Select(t => t.Native).Where(t => !mine.Contains(t, ReferenceEqualityComparer.Instance));
                policy.Own(() => bridge.ReplaceUpcoming(mine.Concat(rest).ToList()));
                foreach (var track in list) _session.Add(bridge.Raw(track).LegacyKey); // a song of that list that starts meanwhile is not the end of the session
                roll.Finished = true;
                next.Busy = true;
                _roll = next;
                _refill = SwitchAsync(next, _epoch);
            }
            var song = bridge.Current?.Song;
            if (song != null) { _session.Add(song.Key); _session.Add(song.LegacyKey); }
            MarkCurrent();
            if (next == null)
            {
                if (!_toldPlayNow)
                {
                    _toldPlayNow = true;
                    Notice("Playing it now; your shuffle continues after it. Songs from another playlist, album or artist switch the shuffle to that list.");
                }
                WatchQueue();
            }
        }
        catch (Exception ex)
        {
            EndSession();
            Notice("True Shuffle session ended: the shuffle could not be kept behind the song you played (" + ex.GetBaseException().Message + ").");
        }
    }

    /// <summary>
    /// Where the song was picked: null when that is what is being shuffled already, or the queue, the history, or a song on
    /// its own; otherwise the new context to shuffle, whose <see cref="RollingQueue.Members"/> is null for the whole library.
    /// </summary>
    private RollingQueue? Context(RollingQueue roll, IReadOnlyList<object> before, object? playing, List<object> list, QueueBridge bridge)
    {
        if (list.Count <= 1) return null;
        var earlier = new HashSet<object>(before, ReferenceEqualityComparer.Instance);
        if (list.All(track => earlier.Contains(track) || ReferenceEquals(track, playing))) return null; // the queue panel: a song of the upcoming queue
        var library = bridge.SnapshotRaw("Whole library").Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        bool MostOf(ICollection<string> ids, ICollection<string> of) => of.Count > 0 && ids.Count(of.Contains) >= 0.95 * of.Count;
        // Two lists are the same list when nearly every song of each is in the other. Being part of a bigger list is not
        // enough: every playlist is part of the library.
        bool Alike(ICollection<string> a, ICollection<string> b) { int both = a.Count(b.Contains); return both >= 0.9 * a.Count && both >= 0.9 * b.Count; }
        RollingQueue New(IReadOnlyList<RawTrack>? members, string? playlist) => new(roll.Mode, members == null ? "Whole library" : "Current queue", roll.SkipDisliked, members,
            new List<TrackEntry>(), 0, roll.Busy ? new(StringComparer.Ordinal) : new(roll.Identities, StringComparer.Ordinal)) { Playlist = playlist };
        var shuffled = roll.Members?.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        bool shufflingLibrary = shuffled == null || MostOf(shuffled, library);
        // The Songs page is the whole library, whatever its search box shows.
        if (bridge.SongsPageOpen) return shufflingLibrary ? null : New(null, null);
        // A playlist's page is that playlist, whatever its search box shows.
        var playlist = bridge.OpenPlaylistId;
        if (playlist != null && playlist == roll.Playlist) return null;
        var raw = list.Select(bridge.Raw).ToList();
        var ids = raw.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (MostOf(ids, library)) return shufflingLibrary ? null : New(null, null); // a page that lists the whole library (the root folder, say)
        if (!shufflingLibrary && (playlist == null || roll.Playlist == null) && Alike(ids, shuffled!)) return null; // the same album, artist or folder again
        return New(raw, playlist);
    }

    /// <summary>
    /// A song was played from another playlist, album, artist or the Songs page: that list is shuffled from now on, in the
    /// same mode, behind the playing song and the user's own queued songs. The old shuffle is dropped.
    /// </summary>
    private async Task SwitchAsync(RollingQueue roll, int epoch)
    {
        var bridge = _bridge!; var lease = _lease;
        bool Current() => !_closed && epoch == _epoch && ReferenceEquals(_roll, roll) && ReferenceEquals(lease, _lease) && ReferenceEquals(bridge, _bridge);
        try
        {
            var current = bridge.CurrentRaw;
            IReadOnlyList<RawTrack> source = roll.Members ?? bridge.SnapshotRaw("Whole library");
            var upcoming = bridge.Upcoming();
            var mine = _priority?.Manual(upcoming.Select(t => t.Native)) ?? new List<object>();
            var mineRaw = upcoming.Where(t => mine.Contains(t.Native, ReferenceEqualityComparer.Instance)).ToList();
            bool explicitAllowed = bridge.ExplicitAllowed; var mode = roll.Mode; bool skipDisliked = roll.SkipDisliked;
            var known = _seen.ToArray(); var last = _last;
            var identities = roll.Identities;
            var planned = await Task.Run(() =>
            {
                var entries = source.Select(r => r.Entry(identities)).ToList();
                var waiting = mineRaw.Select(r => r.Entry(identities).Song.Key).ToHashSet(StringComparer.Ordinal);
                var playing = current?.Entry(identities).Song.Key;
                var remembered = playing != null ? known.Append(playing).ToArray() : known;
                var built = ShuffleEngine.Build(entries.Select(e => e.Song)
                    .Where(s => s.Remote || (!string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path))),
                    mode, remembered, playing, last, explicitAllowed, skipDisliked, upcoming: waiting);
                var native = entries.DistinctBy(e => e.Song.Key).ToDictionary(e => e.Song.Key);
                var queue = built.Songs.Select(s => native[s.Key]).ToList();
                var keys = queue.SelectMany(e => new[] { e.Song.Key, e.Song.LegacyKey }).ToHashSet(StringComparer.Ordinal);
                return (plan: built, ordered: queue, session: keys);
            });
            if (!Current()) return; // ended, applied again, or another song was played from another list meanwhile
            if (planned.plan.Songs.Count == 0)
            {
                EndSession();
                Notice(mode == ShuffleMode.NeverPlayed
                    ? "True Shuffle ended: the list you played from has no never-played songs left. It plays on in its own order."
                    : "True Shuffle ended: the list you played from has nothing else to shuffle. It plays on in its own order.");
                return;
            }
            var kept = planned.plan.Seen as ISet<string> ?? planned.plan.Seen.ToHashSet(StringComparer.Ordinal);
            _seen.ExceptWith(known.Where(k => !kept.Contains(k)));
            _seen.UnionWith(planned.plan.Seen);
            if (planned.plan.NewCycle) QueueSave();
            _session.UnionWith(planned.session);
            var playingNow = bridge.CurrentRaw?.Id;
            roll.Plan = planned.ordered.Where(e => e.Song.Id != playingNow).ToList();
            var first = roll.Plan.Take(_windowSize).ToList();
            roll.Next = first.Count;
            // The user's songs as they are now (one may have been added meanwhile), then the new shuffle.
            var policy = _priority;
            var ownNow = policy?.Manual(bridge.Upcoming().Select(t => t.Native)) ?? new List<object>();
            var queueNow = ownNow.Concat(first.Select(e => e.Native).Where(t => !ownNow.Contains(t, ReferenceEqualityComparer.Instance))).ToList();
            void Put() => bridge.ReplaceUpcoming(queueNow);
            if (policy != null) policy.Own(Put); else Put();
            _buttons?.Selected(mode, roll.Scope);
            if (!_toldSwitch)
            {
                _toldSwitch = true;
                Notice("Now shuffling the list you played from, in the same mode. Songs you queued yourself stay first. To play a list in its own order, choose End True Shuffle on the shuffle button first.");
            }
        }
        catch (Exception ex)
        {
            if (Current())
            {
                EndSession();
                Notice("True Shuffle session ended: the list you played from could not be shuffled (" + ex.GetBaseException().Message + "). It plays on in its own order.");
            }
            return;
        }
        finally { roll.Busy = false; }
        WatchQueue();
    }

    /// <summary>The shuffle button's End True Shuffle: the queue stays as it is and Noctis's own controls are back.</summary>
    private void EndByUser()
    {
        if (_lease == null) return;
        EndSession();
        Notice("True Shuffle ended. The queue stays as it is; playing a song or album now replaces it, as usual in Noctis.");
    }

    private void TrackChanged(object? sender, EventArgs e)
    {
        if (_lease == null || _closed) return;
        if (_priority?.Replacing == true) return; // a song picked from a list is starting: OnPlayedNow takes it from here
        try
        {
            var current = _bridge?.Current?.Song;
            if (current != null && !_session.Contains(current.Key) && !_session.Contains(current.LegacyKey)) { EndSession(); return; }
            MarkCurrent();
            WatchQueue();
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
        // A Shuffle button of a list also turns Noctis's shuffle on, while it starts that list (OnPlayedNow takes that over).
        bool normalControls = e.PropertyName == "IsShuffleEnabled" && (bool)QueueBridge.Get(_bridge.Player, "IsShuffleEnabled")! && _priority?.Starting != true;
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
        // Every snapshot is the complete progress, so one that could not be written is made good by
        // the next one that can; only the first failure of a streak is announced.
        _save = _save.ContinueWith(async _ =>
        {
            try { await store.SaveAsync(snapshot); _saveFailing = false; }
            catch (Exception ex)
            {
                bool first = !_saveFailing;
                _saveFailing = true;
                if (first)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_closed && epoch == _epoch)
                            Notice("Shuffle progress could not be saved (" + ex.GetBaseException().Message + "). It is tried again with the next song; this queue can finish.");
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
        _seen.Clear(); _last = null; _lastPath = null; _historyVersion++;
        if (_lease != null) MarkCurrent();
        QueueSave();
        if (_window != null) _window.Status = "Remembered cycle reset. The playing queue was kept.";
    }

    private void EndSession(bool cancelPending = true)
    {
        if (cancelPending) ++_pending;
        _priority?.Dispose(); _priority=null;
        var roll = _roll; _roll = null; if (roll != null) roll.Finished = true; // no more songs are added
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
        // The song that started last should not be forgotten because Noctis closed a moment later.
        // The writer never needs this thread, so waiting cannot deadlock; two seconds at most.
        try { if (!_save.IsCompleted) _save.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}
