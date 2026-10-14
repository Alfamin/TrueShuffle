using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Windows.Input;
using TrueShuffle;

internal static class ShuffleTests
{
    private static int _passed;
    private static Song S(int i, int plays = 0, bool disliked = false) => new(i.ToString(), "", "Song " + i, "Artist", plays, false, false, disliked, null);
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _passed++; }
    private static void Throws(Action a, string name)
    {
        try { a(); } catch { _passed++; return; }
        throw new Exception("Did not reject: " + name);
    }
    private static Func<int, int> R(int seed) { var r = new Random(seed); return r.Next; }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var pool = Enumerable.Range(1, 2000).Select(i => S(i, i % 7, i % 2 == 0)).ToArray();
            var random = ShuffleEngine.Build(pool, ShuffleMode.EqualChance, pool.Select(s => s.Key), null, null, draw: R(3));
            Check(random.Songs.Count == 2000 && random.Songs.Select(s => s.Key).Distinct().Count() == 2000, "Complete nonduplicate permutation");
            Check(random.Songs.Select(s => s.Id).Order().SequenceEqual(pool.Select(s => s.Id).Order()), "No track lost or invented");
            var noDislikes = pool.Select(s => s with { Disliked = false }).ToArray();
            Check(ShuffleEngine.Build(pool, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: R(7)).Songs.Select(s => s.Id)
                .SequenceEqual(ShuffleEngine.Build(noDislikes, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: R(7)).Songs.Select(s => s.Id)), "Disliked flag cannot bias equal-chance order");
            Check(!random.Songs.Select(s => s.Id).SequenceEqual(ShuffleEngine.Build(pool, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: R(4)).Songs.Select(s => s.Id)), "Different random draws produce different orders");
            var small = Enumerable.Range(1, 4).Select(i => S(i)).ToArray();
            var seen = new[] { small[0].Key, small[2].Key };
            var remaining = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, seen, null, null, draw: R(1));
            Check(remaining.Songs.Select(s => s.Id).Order().SequenceEqual(new[] { "2", "4" }), "Previously started songs excluded");
            Check(!remaining.NewCycle && seen.All(k => remaining.Seen.Contains(k)), "Building does not consume unstarted queued songs");
            var reset = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, small.Select(s => s.Key), small[0].Key, small[3].Key, draw: R(2));
            Check(reset.NewCycle && reset.Songs.Count == 3 && reset.Songs.All(s => s.Key != small[0].Key), "Exhaustion starts a new cycle without restarting current");
            Check(reset.Songs[0].Key != small[3].Key, "No immediate repeat at cycle boundary");
            var foreign = S(99).Key;
            var resetSubset = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, small.Select(s => s.Key).Append(foreign), null, null, draw: R(2));
            Check(resetSubset.Seen.Contains(foreign), "Subset reset preserves progress outside this source");
            var added = small.Append(S(5));
            Check(ShuffleEngine.Build(added, ShuffleMode.NoRepeats, small.Select(s => s.Key), null, null).Songs.Single().Id == "5", "New library additions immediately eligible");
            var discovery = ShuffleEngine.Build(new[] { S(1, 20), S(2, 0), S(3, 1), S(4, 0) }, ShuffleMode.Discovery, Array.Empty<string>(), null, null, draw: R(2));
            Check(discovery.Songs.Select(s => s.PlayCount).SequenceEqual(new[] { 0, 0, 1, 20 }), "Discovery gives least played songs priority");
            var boundaryDiscovery = ShuffleEngine.Build(new[] { S(1, 0), S(2, 20) }, ShuffleMode.Discovery,
                new[] { S(1).Key, S(2).Key }, null, S(1).Key, draw: R(2));
            Check(boundaryDiscovery.Songs.Select(s => s.PlayCount).SequenceEqual(new[] { 0, 20 }), "Discovery boundary avoidance cannot overtake unplayed songs");
            var never = ShuffleEngine.Build(new[] { S(1, 0), S(2, 1), S(3, 0) }, ShuffleMode.NeverPlayed, new[] { S(1).Key }, null, null);
            Check(never.Songs.Count == 1 && never.Songs[0].Id == "3", "Never played excludes both played and remembered starts");
            var emptyNever = ShuffleEngine.Build(small, ShuffleMode.NeverPlayed, small.Select(s => s.Key), null, null);
            Check(emptyNever.Songs.Count == 0 && !emptyNever.NewCycle, "Never played never silently falls back");
            var waiting = new HashSet<string> { small[1].Key };
            var withoutWaiting = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, Array.Empty<string>(), null, null, draw: R(5), upcoming: waiting);
            Check(withoutWaiting.Songs.Count == 3 && withoutWaiting.Songs.All(s => s.Key != small[1].Key), "A later plan leaves out the songs that are already upcoming");
            var cycled = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, small.Select(s => s.Key), small[0].Key, null, draw: R(5), upcoming: waiting);
            Check(cycled.NewCycle && cycled.Songs.Count == 2 && cycled.Songs.All(s => s.Key != small[1].Key && s.Key != small[0].Key), "A fresh cycle also keeps out what is upcoming and what is playing");
            var heard = new HashSet<string> { S(2).Key }; var rightNow = DateTime.UtcNow;
            Check(ShuffleEngine.StillWanted(S(1), ShuffleMode.NoRepeats, heard, true, false, rightNow) && !ShuffleEngine.StillWanted(S(2), ShuffleMode.NoRepeats, heard, true, false, rightNow)
                && ShuffleEngine.StillWanted(S(2), ShuffleMode.EqualChance, heard, true, false, rightNow) && !ShuffleEngine.StillWanted(S(3, 1), ShuffleMode.NeverPlayed, heard, true, false, rightNow)
                && !ShuffleEngine.StillWanted(S(4) with { Skip = true }, ShuffleMode.EqualChance, heard, true, false, rightNow),
                "A song taken from a plan later is checked against the rules of the mode again");
            var gated = new[] { S(1), S(2) with { Skip = true }, S(3) with { Explicit = true }, S(4) with { SnoozedUntil = DateTime.UtcNow.AddHours(1) }, S(5, disliked: true) };
            Check(ShuffleEngine.Build(gated, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, explicitAllowed: false, skipDisliked: true).Songs.Single().Id == "1", "Eligibility respects skips, snoozes, explicit toggle and chosen dislike exclusion");
            Check(ShuffleEngine.Build(new[] { S(1), S(1) }, ShuffleMode.EqualChance, Array.Empty<string>(), null, null).Songs.Count == 1, "Duplicate ids deduplicated");
            Check(ShuffleEngine.Build(new[] { S(1) }, ShuffleMode.NoRepeats, Array.Empty<string>(), S(1).Key, null).Songs.Count == 0, "Single current song is not requeued");
            Check(ShuffleEngine.Build(Array.Empty<Song>(), ShuffleMode.EqualChance, Array.Empty<string>(), null, null).Songs.Count == 0, "Empty source harmless");
            // Enumerate every bounded draw combination for three tracks: all six permutations once.
            var permutations = new HashSet<string>();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++)
            {
                var draws = new Queue<int>(new[] { i, j });
                var p = ShuffleEngine.Build(small.Take(3), ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: _ => draws.Dequeue());
                permutations.Add(string.Join(",", p.Songs.Select(s => s.Id)));
            }
            Check(permutations.Count == 6, "Every permutation has one equally likely draw sequence");
            Throws(() => ShuffleEngine.Build(small, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: _ => -1), "Invalid RNG");

            string temp = Path.Combine(Path.GetTempPath(), "true-shuffle-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var store = new CycleStore(temp);
            Check((await store.LoadAsync()).Seen.Length == 0, "Missing private history starts empty");
            await store.SaveAsync(new(1, seen, seen[1]));
            var loaded = await new CycleStore(temp).LoadAsync();
            Check(loaded.Seen.SequenceEqual(seen) && loaded.Last == seen[1], "No-repeat progress survives restart");
            Check(ShuffleEngine.Build(small, ShuffleMode.NoRepeats, loaded.Seen, null, loaded.Last).Songs.All(s => !seen.Contains(s.Key)), "Restart excludes previous starts");
            Check(!Directory.EnumerateFiles(temp, ".cycle-*.tmp").Any(), "No staged history debris");
            string old = await File.ReadAllTextAsync(Path.Combine(temp, "cycle.json"));
            await File.WriteAllTextAsync(Path.Combine(temp, "cycle.json"), "invalid history");
            bool rejected = false;
            try { await store.LoadAsync(); } catch { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(temp, "cycle.json")) == "invalid history", "Corrupt history preserved rather than silently reset");
            await File.WriteAllTextAsync(Path.Combine(temp, "cycle.json"), old);

            Check(QueueBridge.FindAppend(typeof(OptionalAppend),typeof(IList<FakeTrack>)) != null,"Noctis 1.6 optional append signature accepted");
            Check(QueueBridge.FindAppend(typeof(IncompatibleAppend),typeof(IList<FakeTrack>)) == null,"Unknown required queue arguments refused");
            var main = new FakeMain();
            var bridge = new QueueBridge(main, enforceHostTypes: false);
            var entries = bridge.Snapshot("Whole library");
            Check(entries.Count == 3 && bridge.ExplicitAllowed, "Bridge snapshots full library and host policy");
            Check(bridge.SnapshotRaw("Whole library").Select(r => r.Entry().Song).SequenceEqual(entries.Select(e => e.Song)), "Raw snapshot resolves to exactly the same songs");
            string identityFile = Path.Combine(temp, "identity.mp3"); File.WriteAllText(identityFile, "x");
            var identities = new Dictionary<string, string>();
            var onDisk = new RawTrack(new object(), "9", identityFile, "t", "a", 0, false, false, false, null, false);
            var resolved = onDisk.Entry(identities).Song;
            Check(resolved.Key == Song.StableKey("9", identityFile) && resolved.Key != resolved.LegacyKey && onDisk.Entry(identities).Song.Key == resolved.Key && identities.Count == 1,
                "File identity keys match the stable key and are worked out once per file");
            main.Player.CurrentTrack = main.Library.Tracks[0];
            main.Player.UpNext.AddRange(main.Library.Tracks.Skip(1));
            Check(bridge.Snapshot("Current queue").Count == 3, "Queue source includes playing plus upcoming");
            Throws(() => bridge.Snapshot("Open playlist"), "Wrong view never falls back to whole library");
            var ordered = entries.Skip(1).Reverse().ToList();
            using (var lease = new PlaybackLease(main.Player))
            {
                Check(!main.Player.AutoplayEnabled && main.Player.RepeatMode == FakeRepeat.Off, "Lease prevents biased continuation and repeats");
                bridge.Apply(ordered, keepCurrent: true);
                Check(main.Player.CurrentTrack == main.Library.Tracks[0] && main.Player.Replacements == 0, "Current playback uninterrupted");
                Check(main.Player.UpNext.Select(t => t.Id).SequenceEqual(new[] { "3", "2" }), "Exact order reaches native upcoming queue");
                Check(main.Player.ToggleCount == 1 && !main.Player.IsShuffleEnabled, "Weighted native shuffle disabled via its command");
            }
            Check(main.Player.AutoplayEnabled && main.Player.RepeatMode == FakeRepeat.All && !main.Player.IsRadioActive, "Continuation preferences restored; old radio is not falsely reactivated");
            using (var lease = new PlaybackLease(main.Player)) { main.Player.AutoplayEnabled = true; main.Player.RepeatMode = FakeRepeat.One; }
            Check(main.Player.AutoplayEnabled && main.Player.RepeatMode == FakeRepeat.One, "User changes are never overwritten on release");
            bridge.Apply(ordered, keepCurrent: false);
            Check(main.Player.Replacements == 1 && main.Player.CurrentTrack?.Id == "3", "Explicit start-now uses native replacement method");
            main.Player.UpNext.Clear(); main.Player.UpNext.AddRange(main.Library.Tracks);
            var previousQueue = main.Player.UpNext.Select(t => t.Id).ToArray();
            main.Player.FailNextAppend = true;
            Throws(() => bridge.Apply(ordered, true), "Failed append");
            Check(main.Player.UpNext.Select(t => t.Id).SequenceEqual(previousQueue), "Failed native append restores previous upcoming songs");
            Throws(() => new QueueBridge(new object(), false), "Unsupported host rejected before mutation");

            if (args.Length > 0)
            {
                Environment.SetEnvironmentVariable("NOCTIS_DATA_DIR", Path.Combine(temp, "isolated-noctis"));
                VerifyInstalledContract(args[0]);
            }
            UiTests.Run(Check);
            CompatibilityTests.Run(Check);
            if (args.Length > 0) NativePlayerTests.Run(args[0], Check);
            Console.WriteLine($"PASS: {_passed} assertions; no real playback, library writes or network calls.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void VerifyInstalledContract(string root)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(root, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "Noctis.dll"));
        var player = app.GetType("Noctis.ViewModels.PlayerViewModel", true)!;
        var main = app.GetType("Noctis.ViewModels.MainWindowViewModel", true)!;
        var signature = player.GetMethods().Single(m => m.Name == "ReplaceQueueAndPlay" && m.GetParameters().Length == 2);
        var track = signature.GetParameters()[0].ParameterType.GetGenericArguments()[0];
        Console.WriteLine("Installed track type: " + track.FullName + "; assembly: " + track.Assembly.GetName().Name);
        Check(main.GetProperty("Player")?.PropertyType == player && main.GetField("_library", BindingFlags.Instance | BindingFlags.NonPublic) != null, "Installed main-window bridge contract");
        var replace = player.GetMethod("ReplaceQueueAndPlay", new[] { typeof(IList<>).MakeGenericType(track), typeof(int) });
        Check(replace != null && player.GetMethod("ClearQueue", Type.EmptyTypes) != null && QueueBridge.FindAppend(player, typeof(IList<>).MakeGenericType(track)) != null, "Installed queue methods match tested bridge, including 1.6 optional arguments");
        Check(new[] { "AutoplayEnabled", "RepeatMode", "IsRadioActive", "IsShuffleEnabled", "CurrentTrack", "UpNext", "ToggleShuffleCommand" }.All(p => player.GetProperty(p) != null), "Installed player state contract");
        Check(new[] { "Id", "FilePath", "Title", "Artist", "PlayCount", "SkipWhenShuffling", "IsExplicit", "IsDisliked", "SnoozedUntil", "IsRemoteStream" }.All(p => track.GetProperty(p) != null), "Installed track snapshot contract");
        var settings = app.GetType("Noctis.ViewModels.SettingsViewModel", true)!;
        Check(settings.GetProperty("AllowExplicitContent")?.PropertyType == typeof(bool), "Installed explicit setting readable");
        var playlist = app.GetType("Noctis.ViewModels.PlaylistViewModel", true)!;
        Check(playlist.GetProperty("Tracks") != null, "Installed playlist view contract");
    }
}

public enum FakeRepeat { Off, All, One }
public sealed class OptionalAppend { public void AddRangeToQueue(IList<FakeTrack> tracks,string? sourceName=null,bool announce=true){} }
public sealed class IncompatibleAppend { public void AddRangeToQueue(IList<FakeTrack> tracks,string sourceName,bool announce){} }
public sealed class FakeTrack
{
    public string Id { get; set; } = ""; public string FilePath { get; set; } = ""; public string Title => "Track " + Id;
    public string Artist => "Artist"; public int PlayCount { get; set; } public bool SkipWhenShuffling => false;
    public bool IsExplicit => false; public bool IsDisliked => false; public DateTime? SnoozedUntil => null;
    public bool IsRemoteStream { get; set; } = true;
}
public sealed class FakeLibrary { public List<FakeTrack> Tracks { get; } = new() { new() { Id = "1" }, new() { Id = "2" }, new() { Id = "3" } }; }
public sealed class FakeSettings { public bool AllowExplicitContent => true; }
public sealed class FakeMain
{
    private readonly FakeLibrary _library = new();
    public FakeLibrary Library => _library;
    public FakePlayer Player { get; } = new(); public FakeSettings Settings { get; } = new(); public object? CurrentView => null;
}
public sealed class FakeCommand : ICommand
{
    private readonly Action _action; public FakeCommand(Action action) => _action = action;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true; public void Execute(object? parameter) => _action();
}
public sealed class FakePlayer : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _autoplay = true, _radio = true; private FakeRepeat _repeat = FakeRepeat.All;
    public bool AutoplayEnabled { get => _autoplay; set { if (_autoplay == value) return; _autoplay = value; PropertyChanged?.Invoke(this, new(nameof(AutoplayEnabled))); } }
    public bool IsRadioActive { get => _radio; set { if (_radio == value) return; _radio = value; PropertyChanged?.Invoke(this, new(nameof(IsRadioActive))); } }
    public FakeRepeat RepeatMode { get => _repeat; set { if (_repeat == value) return; _repeat = value; PropertyChanged?.Invoke(this, new(nameof(RepeatMode))); } }
    public bool IsShuffleEnabled { get; set; } = true; public FakeTrack? CurrentTrack { get; set; }
    public List<FakeTrack> UpNext { get; } = new(); public int Replacements { get; private set; } public int ToggleCount { get; private set; }
    public bool FailNextAppend { get; set; }
    public ICommand ToggleShuffleCommand => new FakeCommand(() => { ToggleCount++; IsShuffleEnabled = !IsShuffleEnabled; });
    public void ClearQueue() => UpNext.Clear();
    public void AddRangeToQueue(IList<FakeTrack> tracks) { if (FailNextAppend) { FailNextAppend = false; throw new InvalidOperationException("simulated append failure"); } UpNext.AddRange(tracks); }
    public void ReplaceQueueAndPlay(IList<FakeTrack> tracks, int startIndex) { Replacements++; CurrentTrack = tracks[startIndex]; UpNext.Clear(); UpNext.AddRange(tracks.Skip(startIndex + 1)); }
}
