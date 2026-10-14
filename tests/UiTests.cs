using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Plugins;
using TrueShuffle;

internal static class UiTests
{
    public static void Run(Action<bool, string> check)
    {
        AppBuilder.Configure<UiApp>().UseSkia().UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        int applies = 0, resets = 0, stops = 0;
        ShuffleMode chosen = ShuffleMode.EqualChance; string source = ""; bool kept = false;
        var w = new ShuffleWindow("invalid saved choice", "Current queue", false,
            (m, s, k, d) => { applies++; chosen = m; source = s; kept = k; return Task.CompletedTask; },
            () => stops++, () => { resets++; return Task.CompletedTask; });
        w.Show(); Dispatcher.UIThread.RunJobs();
        var modes = w.GetVisualDescendants().OfType<ComboBox>().Last();
        var scopes = w.GetVisualDescendants().OfType<ComboBox>().First();
        check(modes.SelectedIndex == 1 && scopes.SelectedIndex == 0, "Invalid default mode falls back to No repeats");
        check(applies == 0 && resets == 0 && stops == 0, "Opening controls changes no playback or history");
        Click(Button(w, "Apply shuffle"));
        check(applies == 1 && chosen == ShuffleMode.NoRepeats && source == "Current queue" && kept, "UI apply passes selected source/mode and keeps current by default");
        modes.SelectedIndex = 3; scopes.SelectedIndex = 2;
        Click(Button(w, "Apply shuffle"));
        check(chosen == ShuffleMode.NeverPlayed && source == "Whole library", "UI supports alternate modes and sources");
        Click(Button(w, "Reset remembered cycle…"));
        check(resets == 0 && Button(w, "Confirm reset") != null, "History reset requires explicit second click");
        Click(Button(w, "Confirm reset")); check(resets == 1, "Confirmed reset executes once");
        w.Busy = true;
        check(!Button(w, "Apply shuffle").IsEnabled && !modes.IsEnabled && !Button(w, "Reset remembered cycle…").IsEnabled, "Busy operation prevents conflicting changes");
        check(Button(w, "Return to Noctis controls").IsEnabled, "Cancel remains available while preparing");
        w.Busy = false; Click(Button(w, "Return to Noctis controls")); check(stops == 1, "Return control ends session only on click");
        modes.SelectedIndex = -1;
        Click(Button(w, "Apply shuffle")); check(applies == 2 && w.Status.Contains("Choose a source"), "Incomplete selection is refused without callback");
        modes.SelectedIndex = 1;
        w.Status = "2,000 upcoming songs queued. Repeat and similar-song Autoplay are suspended for this session; Track Radio ended. Close this window to keep listening.";
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var frame = w.CaptureRenderedFrame())
        {
            check(frame != null && frame.PixelSize.Width > 400, "Shuffle window renders headlessly");
            string path = Path.Combine(AppContext.BaseDirectory, "shuffle-window.png");
            frame!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine("UI preview: " + path);
        }
        w.Close();

        var errors = new ShuffleWindow(null, null, false, (_, _, _, _) => Task.FromException(new Exception("safe apply error")),
            () => throw new Exception("safe stop error"), () => Task.FromException(new Exception("safe reset error")));
        errors.Show(); Click(Button(errors, "Apply shuffle"));
        check(errors.Status == "safe apply error", "Apply callback failure becomes a status message");
        Click(Button(errors, "Return to Noctis controls")); check(errors.Status == "safe stop error", "Stop callback failure contained");
        Click(Button(errors, "Reset remembered cycle…")); Click(Button(errors, "Confirm reset"));
        check(errors.Status == "safe reset error", "Reset callback failure contained"); errors.Close();

        PluginLifecycle(check);
        ShuffleButton(check);
        UiThreadTests.Run(check);
        RollingTests.Run(check);
    }

    private static void ShuffleButton(Action<bool, string> check)
    {
        int nativeToggles = 0, applications = 0, opens = 0;
        ShuffleMode picked = ShuffleMode.EqualChance; string selectedScope = "";
        var command = new FakeCommand(() => nativeToggles++);
        var native = new TestShuffleButton { Content = "Shuffle", Command = command };
        var original = new ContextMenu { ItemsSource = new[] { new MenuItem { Header = "original" } } };
        native.ContextMenu = original; ToolTip.SetTip(native, "Native shuffle");
        var other = new Button { Content = "Other", Command = new FakeCommand(() => { }) };
        var root = new Window { Width = 440, Height = 400, Content = new StackPanel { Children = { native, other } } };
        root.Show();
        var menu = new ShuffleButtonMenu(command,
            (mode, scope) => { applications++; picked = mode; selectedScope = scope; return Task.CompletedTask; },
            () => command.Execute(null), () => opens++, _ => { });
        menu.Refresh(new[] { root }); menu.Refresh(new[] { root });
        check(native.ContextMenu != original && other.ContextMenu == null && nativeToggles == 0, "Only shuffle-bound buttons gain a menu; attach never toggles playback");
        int tipSets = 0;
        native.PropertyChanged += (_, e) => { if (e.Property == ToolTip.TipProperty) tipSets++; };
        menu.Refresh(new[] { root }); menu.Refresh(new[] { root });
        check(tipSets == 0 && menu.Count == 1, "A scan does not set an unchanged tooltip again");
        var mini = new Window();
        check(ShuffleButtonMenu.Fingerprint(new[] { root }) == ShuffleButtonMenu.Fingerprint(new[] { root }) && ShuffleButtonMenu.Fingerprint(new[] { root }) != ShuffleButtonMenu.Fingerprint(new[] { root, mini }),
            "A window that came or went changes the fingerprint that triggers a scan");
        check(ShuffleButtonMenu.ScanInterval(1, TimeSpan.Zero) == TimeSpan.FromSeconds(5) && ShuffleButtonMenu.ScanInterval(0, TimeSpan.FromSeconds(5)) == TimeSpan.FromSeconds(1)
            && ShuffleButtonMenu.ScanInterval(0, TimeSpan.FromSeconds(31)) == TimeSpan.FromSeconds(5), "Scans back off once the buttons are attached, and later when there are none");
        native.Activate(); Dispatcher.UIThread.RunJobs();
        check(native.ContextMenu!.IsOpen && nativeToggles == 0, "Actual Button.OnClick opens choices without also running weighted shuffle");
        var noRepeats = native.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "No repeats"));
        noRepeats.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
        check(applications == 1 && picked == ShuffleMode.NoRepeats && selectedScope == "Current queue", "Shuffle-button mode applies directly without opening plugin settings");
        var whole = native.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Whole library"));
        var discover = whole.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Discovery"));
        discover.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
        check(applications == 2 && picked == ShuffleMode.Discovery && selectedScope == "Whole library", "Source submenu supplies its exact scope");
        menu.Selected(ShuffleMode.Discovery, "Whole library");
        check(discover.IsChecked && !noRepeats.IsChecked && ToolTip.GetTip(native)!.ToString()!.Contains("Discovery"), "Selected mode appears in menu and button tooltip");
        var normal = native.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Noctis shuffle (normal)"));
        normal.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
        check(nativeToggles == 1, "Normal native shuffle remains available exactly once");
        native.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "More shuffle controls…"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
        check(opens == 1, "Advanced controls are optional");
        native.ContextMenu.Open(native); Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var frame = root.CaptureRenderedFrame()) frame?.Save(Path.Combine(AppContext.BaseDirectory, "shuffle-button-menu.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        menu.Dispose();
        check(native.ContextMenu == original && Equals(ToolTip.GetTip(native), "Native shuffle"), "Disable restores original button menu and tooltip");
        native.Activate(); Dispatcher.UIThread.RunJobs();
        check(nativeToggles == 2, "Native shuffle command works after detachment");
        root.Close();
    }

    private static void PluginLifecycle(Action<bool, string> check)
    {
        string temp = Path.Combine(Path.GetTempPath(), "shuffle-ui-" + Guid.NewGuid().ToString("N"));
        var host = new UiHost(temp);
        var plugin = new TrueShufflePlugin(); plugin.Initialize(host); Set(plugin, "_refillAt", -1);
        Pump(() => ((Task)Field(plugin, "_load")).IsCompleted);
        check(host.Commands == 1 && !File.Exists(Path.Combine(temp, "cycle.json")), "Plugin enable registers controls without writing a queue/history");
        var main = new FakeMain(); main.Player.IsShuffleEnabled = false;
        main.Player.CurrentTrack = main.Library.Tracks[0]; main.Player.UpNext.AddRange(main.Library.Tracks.Skip(1));
        var bridge = new QueueBridge(main, false);
        Set(plugin, "_bridge", bridge);
        var window = new ShuffleWindow(null, null, false, (_, _, _, _) => Task.CompletedTask, () => { }, () => Task.CompletedTask);
        window.Show(); Set(plugin, "_window", window);
        Task Apply(ShuffleMode mode) => (Task)typeof(TrueShufflePlugin).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(plugin, new object[] { mode, "Whole library", true, false })!;
        var apply = Apply(ShuffleMode.NoRepeats); Pump(() => apply.IsCompleted);
        check(main.Player.UpNext.Count == 2 && !main.Player.AutoplayEnabled && main.Player.RepeatMode == FakeRepeat.Off, "Plugin apply suspends biased continuation and preserves queue membership");
        main.Player.CurrentTrack = main.Player.UpNext[0]; main.Player.UpNext.RemoveAt(0); host.Playing.Fire();
        var heard = (HashSet<string>)Field(plugin, "_seen");
        check(heard.Count == 2, "Only actual starts enter remembered progress");
        var again = Apply(ShuffleMode.NoRepeats); Pump(() => again.IsCompleted);
        check(main.Player.UpNext.Count == 1 && main.Player.UpNext[0].Id != "1", "Reshuffling during playback does not resurrect previous starts");
        var allKeys = main.Library.Tracks.Select(t => Song.StableKey(t.Id, "", true)).ToHashSet(StringComparer.Ordinal);
        Set(plugin, "_seen", allKeys);
        main.Player.FailNextAppend = true;
        var failed = Apply(ShuffleMode.NoRepeats); Pump(() => failed.IsCompleted);
        check(((HashSet<string>)Field(plugin, "_seen")).SetEquals(allKeys) && main.Player.UpNext.Count == 1,
            "Failed cycle reset/application preserves remembered progress and previous queue");
        Set(plugin, "_seen", heard);
        var resume = Apply(ShuffleMode.NoRepeats); Pump(() => resume.IsCompleted);
        main.Player.RepeatMode = FakeRepeat.One;
        check(FieldNullable(plugin, "_lease") == null && main.Player.RepeatMode == FakeRepeat.One && main.Player.AutoplayEnabled, "User repeat override ends custom session and remains respected");
        Pump(() => ((Task)Field(plugin, "_save")).IsCompleted);
        var persisted = new CycleStore(temp).LoadAsync(); Pump(() => persisted.IsCompleted);
        check(persisted.Result.Seen.Length == 2, "Plugin persists starts in its private store");
        plugin.Shutdown();
        check(host.SettingsSource.SubscriberCount == 0 && host.Playing.SubscriberCount == 0 && host.Disposals == 1, "Disable detaches all host callbacks and menu registration");

        var second = new TrueShufflePlugin(); second.Initialize(new UiHost(temp));
        Pump(() => ((Task)Field(second, "_load")).IsCompleted);
        check(((HashSet<string>)Field(second, "_seen")).Count == 2, "Actual plugin entry restores saved cycle on restart");
        second.Shutdown();

        // A return-to-normal action while an asynchronous preparation is pending cancels it.
        var canceled = new TrueShufflePlugin(); canceled.Initialize(new UiHost(Path.Combine(temp, "cancel"))); Set(canceled, "_refillAt", -1);
        Pump(() => ((Task)Field(canceled, "_load")).IsCompleted);
        var large = new FakeMain(); large.Library.Tracks.Clear();
        large.Library.Tracks.AddRange(Enumerable.Range(0, 5000).Select(i => new FakeTrack { Id = i.ToString() }));
        large.Player.CurrentTrack = large.Library.Tracks[0]; large.Player.UpNext.Add(large.Library.Tracks[1]);
        Set(canceled, "_bridge", new QueueBridge(large, false));
        var waiting = new ShuffleWindow(null, null, false, (_, _, _, _) => Task.CompletedTask, () => { }, () => Task.CompletedTask);
        waiting.Show(); Set(canceled, "_window", waiting);
        var preparing = (Task)typeof(TrueShufflePlugin).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(canceled, new object[] { ShuffleMode.NoRepeats, "Whole library", true, false })!;
        typeof(TrueShufflePlugin).GetMethod("EndSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canceled, new object[] { true });
        Pump(() => preparing.IsCompleted);
        check(large.Player.UpNext.Count == 1 && large.Player.AutoplayEnabled && FieldNullable(canceled, "_lease") == null,
            "Cancel during asynchronous preparation does not later replace the queue");
        canceled.Shutdown();

        var quick = new TrueShufflePlugin(); quick.Initialize(new UiHost(Path.Combine(temp, "quick"))); Set(quick, "_refillAt", -1);
        Pump(() => ((Task)Field(quick, "_load")).IsCompleted);
        var quickMain = new FakeMain(); quickMain.Player.CurrentTrack = quickMain.Library.Tracks[0];
        Set(quick, "_bridge", new QueueBridge(quickMain, false));
        var quickTask = (Task)typeof(TrueShufflePlugin).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(quick, new object[] { ShuffleMode.Discovery, "Whole library", true, false })!;
        Pump(() => quickTask.IsCompleted);
        check(quickMain.Player.UpNext.Count == 2 && FieldNullable(quick, "_window") == null && FieldNullable(quick, "_lease") != null,
            "Quick shuffle applies with no settings/control window open");
        quick.Shutdown();

        // Songs with real files: the keys that reach the session are the file-identity ones, worked out in the background.
        var files = Path.Combine(temp, "files"); Directory.CreateDirectory(files);
        var onDisk = new FakeMain(); onDisk.Library.Tracks.Clear();
        onDisk.Library.Tracks.AddRange(Enumerable.Range(1, 4).Select(i =>
        {
            var file = Path.Combine(files, "song " + i + ".mp3"); File.WriteAllText(file, "synthetic audio");
            return new FakeTrack { Id = i.ToString(), FilePath = file, IsRemoteStream = false };
        }));
        onDisk.Player.CurrentTrack = onDisk.Library.Tracks[0];
        var local = new TrueShufflePlugin(); local.Initialize(new UiHost(Path.Combine(temp, "local"))); Set(local, "_refillAt", -1);
        Pump(() => ((Task)Field(local, "_load")).IsCompleted);
        Set(local, "_bridge", new QueueBridge(onDisk, false));
        var localApply = (Task)typeof(TrueShufflePlugin).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(local, new object[] { ShuffleMode.NoRepeats, "Whole library", true, false })!;
        Pump(() => localApply.IsCompleted);
        var sessionKeys = (HashSet<string>)Field(local, "_session");
        var queued = onDisk.Player.UpNext.Select(t => (t.Id, t.FilePath)).ToArray();
        check(queued.Length == 3 && queued.All(q => sessionKeys.Contains(Song.StableKey(q.Id, q.FilePath)) && Song.StableKey(q.Id, q.FilePath) != new Song(q.Id, q.FilePath, "", "", 0, false, false, false, null).LegacyKey
                && sessionKeys.Contains(new Song(q.Id, q.FilePath, "", "", 0, false, false, false, null).LegacyKey)),
            "Apply on real files queues every other song and keys the session by file identity and path");
        local.Shutdown();

        // A save that fails does not stop saving: the next one that can be written holds everything.
        var failingHost = new UiHost(Path.Combine(temp, "failing")); var failing = new TrueShufflePlugin(); failing.Initialize(failingHost); Set(failing, "_refillAt", -1); // these tests look at the first plan and at saving, not at what follows
        Pump(() => ((Task)Field(failing, "_load")).IsCompleted);
        var six = new FakeMain(); six.Player.IsShuffleEnabled = false; six.Library.Tracks.Clear();
        six.Library.Tracks.AddRange(Enumerable.Range(1, 6).Select(i => new FakeTrack { Id = i.ToString() }));
        six.Player.CurrentTrack = six.Library.Tracks[0]; six.Player.UpNext.AddRange(six.Library.Tracks.Skip(1));
        Set(failing, "_bridge", new QueueBridge(six, false));
        var sixApply = (Task)typeof(TrueShufflePlugin).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(failing, new object[] { ShuffleMode.NoRepeats, "Whole library", true, false })!;
        Pump(() => sixApply.IsCompleted); Pump(() => ((Task)Field(failing, "_save")).IsCompleted);
        var history = Path.Combine(temp, "failing", "cycle.json");
        check(File.Exists(history) && failingHost.Notices.Count(n => n.Contains("could not be saved")) == 0, "Applying writes the history");
        void Next() { six.Player.CurrentTrack = six.Player.UpNext[0]; six.Player.UpNext.RemoveAt(0); failingHost.Playing.Fire(); }
        void Settle() { Pump(() => ((Task)Field(failing, "_save")).IsCompleted); }
        var held = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.None); // another program holds cycle.json
        Next(); Settle(); Next(); Settle();
        check(failingHost.Notices.Count(n => n.Contains("could not be saved")) == 1, "A failing save is announced once for the whole streak");
        check(((HashSet<string>)Field(failing, "_seen")).Count == 3 && FieldNullable(failing, "_historyError") == null, "Progress is kept in memory and history stays usable while saving fails");
        held.Dispose();
        Next(); Settle();
        var healed = new CycleStore(Path.Combine(temp, "failing")).LoadAsync(); Pump(() => healed.IsCompleted);
        check(healed.Result.Seen.Length == 4, "The next save that works holds the songs met while saving failed");
        held = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.None);
        Next(); Settle();
        check(failingHost.Notices.Count(n => n.Contains("could not be saved")) == 2, "A new failure streak is announced again");
        held.Dispose();
        Next();
        failing.Shutdown(); // no waiting for the save here: Shutdown itself waits for it
        check(((Task)Field(failing, "_save")).IsCompleted, "Disabling waits for the pending history save");
        var last = new CycleStore(Path.Combine(temp, "failing")).LoadAsync(); Pump(() => last.IsCompleted);
        check(last.Result.Seen.Length == 6, "The song that started last is in the saved history");
    }

    private static object Field(object o, string name) => FieldNullable(o, name)!;
    private static object? FieldNullable(object o, string name) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);
    private static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);
    private static Button Button(Window w, string text) => w.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!ready() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
        if (!ready()) throw new Exception("Headless operation did not finish");
    }
}

internal sealed class TestShuffleButton : Button { public void Activate() => OnClick(); }

internal sealed class UiApp : Application
{
    public override void Initialize() { RequestedThemeVariant = ThemeVariant.Dark; Styles.Add(new FluentTheme()); }
}
internal sealed class UiSettings : IPluginSettings
{
    private EventHandler<string>? _changed;
    public int SubscriberCount { get; private set; }
    public event EventHandler<string>? Changed { add { _changed += value; SubscriberCount++; } remove { _changed -= value; SubscriberCount--; } }
    public string? GetString(string key) => null; public bool GetBool(string key, bool fallback = false) => fallback;
    public double GetNumber(string key, double fallback = 0) => fallback;
    public void Fire(string key) => _changed?.Invoke(this, key);
}
internal sealed class UiPlaying : INowPlaying
{
    private EventHandler? _changed;
    public int SubscriberCount { get; private set; }
    public event EventHandler? TrackChanged { add { _changed += value; SubscriberCount++; } remove { _changed -= value; SubscriberCount--; } }
    public event EventHandler? IsPlayingChanged { add { } remove { } }
    public NowPlayingTrack? Track => null; public bool IsPlaying => false; public TimeSpan Position => TimeSpan.Zero;
    public void Fire() => _changed?.Invoke(this, EventArgs.Empty);
}
internal sealed class UiHost : IPluginHost
{
    public UiHost(string folder) => DataDirectory = folder;
    public string AppVersion => "1.5.9"; public string DataDirectory { get; }
    public UiSettings SettingsSource { get; } = new(); public UiPlaying Playing { get; } = new();
    public IPluginSettings Settings => SettingsSource; public INowPlaying NowPlaying => Playing;
    public IBeatSource Beat => throw new NotSupportedException(); public ISpectrumSource Spectrum => throw new NotSupportedException();
    public int Commands { get; private set; } public int Disposals { get; private set; }
    public List<string> Notices { get; } = new();
    public void Log(string message) { } public void Notify(string message) => Notices.Add(message);
    public void RegisterVisualLayer(IVisualLayerProvider provider) => throw new NotSupportedException();
    public IDisposable RegisterTrackCommand(string label, string? icon, Action<TrackInfo> handler) { Commands++; return new DisposeAction(() => Disposals++); }
    private sealed class DisposeAction : IDisposable { private readonly Action _action; public DisposeAction(Action a) => _action = a; public void Dispose() => _action(); }
}
