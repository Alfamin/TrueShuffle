using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueShuffle;

/// <summary>Actual installed PlayerViewModel and Track, with no-op audio/library/persistence services.</summary>
internal static class NativePlayerTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "Noctis.dll"));
        var playerType = app.GetType("Noctis.ViewModels.PlayerViewModel", true)!;
        var ctor = playerType.GetConstructors().Single();
        var args = ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : DispatchProxy.Create(p.ParameterType, typeof(ServiceStub))).ToArray();
        var audio = (ServiceStub)args[0]!;
        var library = (ServiceStub)args[1]!;
        var trackType = playerType.GetMethod("ReplaceQueueAndPlay", new[] { playerType.GetMethods().Single(m => m.Name == "ReplaceQueueAndPlay" && m.GetParameters().Length == 2).GetParameters()[0].ParameterType, typeof(int) })!
            .GetParameters()[0].ParameterType.GetGenericArguments()[0];
        var tracks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
        for (int i = 1; i <= 3; i++)
        {
            var track = Activator.CreateInstance(trackType)!;
            Set(track, "Id", Guid.NewGuid()); Set(track, "Title", "Synthetic " + i); Set(track, "Artist", "Test artist");
            Set(track, "Duration", TimeSpan.FromMinutes(3)); Set(track, "FilePath", Path.Combine(Path.GetTempPath(), "synthetic-shuffle-" + i + ".mp3"));
            tracks.Add(track);
        }
        library.Values["get_Tracks"] = tracks;
        library.Custom = (method, input) => method.Name == "GetTrackById" ? tracks.Cast<object>().FirstOrDefault(t => Equals(Get(t, "Id"), input![0])) : ServiceStub.Unhandled;
        var player = ctor.Invoke(args);
        try
        {
            playerType.GetMethod("ReplaceQueueAndPlay", new[] { typeof(IList<>).MakeGenericType(trackType), typeof(int) })!.Invoke(player, new object[] { tracks, 0 });
            Dispatcher.UIThread.RunJobs();
            var shell = new NativeShell(player, library);
            var bridge = new QueueBridge(shell, false);
            var entries = bridge.Snapshot("Whole library");
            var first = Get(player, "CurrentTrack");
            int plays = audio.Calls.Count(c => c == "Play");
            Set(player, "AutoplayEnabled", true);
            var repeatType = playerType.GetProperty("RepeatMode")!.PropertyType;
            Set(player, "RepeatMode", Enum.Parse(repeatType, "All"));
            using (new PlaybackLease(player))
            {
                bridge.Apply(new[] { entries[2], entries[1] }, true);
                check(ReferenceEquals(first, Get(player, "CurrentTrack")) && audio.Calls.Count(c => c == "Play") == plays, "Installed native queue adapter never restarts current audio");
                check(bridge.Snapshot("Current queue").Select(e => e.Song.Title).SequenceEqual(new[] { "Synthetic 1", "Synthetic 3", "Synthetic 2" }), "Installed native queue receives exact custom order");
                using(var priority=new QueueFrontPolicy(player,_=>{},message=>throw new Exception(message)))
                {
                    playerType.GetMethod("AddToQueue")!.Invoke(player,new object[]{tracks[1]!,false});Dispatcher.UIThread.RunJobs();
                    check(bridge.Snapshot("Current queue").Select(e=>e.Song.Title).SequenceEqual(new[]{"Synthetic 1","Synthetic 2","Synthetic 3"}),"Actual Add to Queue puts an already queued song next without a duplicate");
                    var block=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
                    block.Add(tracks[2]);block.Add(tracks[1]);
                    QueueBridge.FindAppend(playerType,typeof(IList<>).MakeGenericType(trackType))!.Invoke(player,new object?[]{block,null,false});Dispatcher.UIThread.RunJobs();
                    check(bridge.Snapshot("Current queue").Select(e=>e.Song.Title).SequenceEqual(new[]{"Synthetic 1","Synthetic 3","Synthetic 2"}),"Actual multi-song addition stays together in chosen order at the front");
                    check(ReferenceEquals(first,Get(player,"CurrentTrack")) && audio.Calls.Count(c=>c=="Play")==plays,"Queue priority never restarts or interrupts current audio");
                    playerType.GetMethod("AddToQueue")!.Invoke(player,new object[]{tracks[0]!,false});
                    playerType.GetMethod("ClearQueue")!.Invoke(player,null);Dispatcher.UIThread.RunJobs();
                    check(!((IEnumerable)Get(player,"UpNext")!).Cast<object>().Any(),"Clearing before pending promotion never resurrects a requested row");
                }
                bridge.Apply(new[]{entries[2],entries[1]},true);
                ((ICommand)Get(player, "NextCommand")!).Execute(null); Dispatcher.UIThread.RunJobs();
                check(Get(Get(player, "CurrentTrack")!, "Title")!.ToString() == "Synthetic 3", "Actual native Next consumes first custom song");
                audio.Raise("TrackEnded"); Dispatcher.UIThread.RunJobs();
                check(Get(Get(player, "CurrentTrack")!, "Title")!.ToString() == "Synthetic 2", "Actual natural track end follows custom order");
                audio.Raise("TrackEnded"); Dispatcher.UIThread.RunJobs();
                check(!bridge.Snapshot("Current queue").Any(e => e.Song.Title != "Synthetic 2") && !(bool)Get(player, "IsPlaying")!, "Actual queue drain does not choose biased Autoplay or repeat");
            }
            check(audio.Calls.Contains("Play") && !library.Calls.Any(c => c == "ScanAsync" || c == "ImportFilesAsync"), "Native integration uses fake audio and never scans real music");
            var big=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
            for(int i=0;i<2000;i++)
            {
                var item=Activator.CreateInstance(trackType)!;Set(item,"Id",Guid.NewGuid());Set(item,"Title","Prepared "+i);
                Set(item,"FilePath",Path.Combine(Path.GetTempPath(),"prepared-shuffle-"+i+".flac"));big.Add(item);
            }
            { // Rolling queue against the real player: its own additions are left where they are, and a cleared or replaced queue is noticed.
                var listOf=typeof(IList<>).MakeGenericType(trackType);
                IList Some(int from,int count){ var l=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!; for(int i=from;i<from+count;i++)l.Add(big[i]!); return l; }
                object[] Upcoming()=>((IEnumerable)Get(player,"UpNext")!).Cast<object>().ToArray();
                var replace=playerType.GetMethod("ReplaceQueueAndPlay",new[]{listOf,typeof(int)})!;
                var addMany=QueueBridge.FindAppend(playerType,listOf)!;
                int replaced=0;
                replace.Invoke(player,new object[]{Some(0,5),0});Dispatcher.UIThread.RunJobs();
                using(var policy=new QueueFrontPolicy(player,_=>{},message=>throw new Exception(message),()=>replaced++))
                {
                    var window=Some(10,5);
                    policy.Own(()=>addMany.Invoke(player,new object?[]{window,null,false}));Dispatcher.UIThread.RunJobs();
                    var queue=Upcoming();
                    check(queue.Length==9 && ReferenceEquals(queue[0],big[1]) && ReferenceEquals(queue[4],big[10]) && ReferenceEquals(queue[8],big[14]),"A rolling addition stays behind the upcoming songs instead of being promoted to the front");
                    addMany.Invoke(player,new object?[]{Some(20,2),null,false});Dispatcher.UIThread.RunJobs();
                    queue=Upcoming();
                    check(ReferenceEquals(queue[0],big[20]) && ReferenceEquals(queue[1],big[21]) && ReferenceEquals(queue[2],big[1]),"The user's own addition still goes next");
                    ((ICommand)Get(player,"NextCommand")!).Execute(null);Dispatcher.UIThread.RunJobs();
                    playerType.GetMethod("RemoveFromQueue",new[]{typeof(int)})!.Invoke(player,new object[]{0});Dispatcher.UIThread.RunJobs();
                    playerType.GetMethod("AddToQueue")!.Invoke(player,new object[]{big[30]!,false});Dispatcher.UIThread.RunJobs();
                    check(replaced==0,"Playing on, removing a song and adding a song do not end a session");
                    playerType.GetMethod("ClearQueue")!.Invoke(player,null);Dispatcher.UIThread.RunJobs();
                    check(replaced==1,"Clearing the upcoming queue is reported");
                }
                replace.Invoke(player,new object[]{Some(0,5),0});Dispatcher.UIThread.RunJobs();
                using(var policy=new QueueFrontPolicy(player,_=>{},message=>throw new Exception(message),()=>replaced++))
                {
                    replace.Invoke(player,new object[]{Some(40,5),0});Dispatcher.UIThread.RunJobs();
                    check(replaced==2,"Replacing the queue with other songs is reported");
                }
            }
            // What the real Track and PlayerViewModel cost on the UI thread for a library of this size (printed, not asserted).
            library.Values["get_Tracks"]=big;
            for(int run=0;run<2;run++)
            {
                long begun=System.Diagnostics.Stopwatch.GetTimestamp();
                var read=bridge.SnapshotRaw("Whole library");
                Console.WriteLine($"UI-THREAD native snapshot of {read.Count:N0} real Track objects: {System.Diagnostics.Stopwatch.GetElapsedTime(begun).TotalMilliseconds:0.0} ms");
            }
            var append=QueueBridge.FindAppend(playerType,typeof(IList<>).MakeGenericType(trackType))!;
            var clock=System.Diagnostics.Stopwatch.StartNew();
            foreach(int size in new[]{2000,200,2000,200})
            {
                var part=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
                for(int i=0;i<size;i++)part.Add(big[i]!);
                clock.Restart();
                playerType.GetMethod("ClearQueue")!.Invoke(player,null);
                append.Invoke(player,new object?[]{part,null,false});
                Console.WriteLine($"UI-THREAD native ClearQueue + AddRangeToQueue({size:N0} tracks): {clock.Elapsed.TotalMilliseconds:0.0} ms");
                Dispatcher.UIThread.RunJobs();
            }
            playerType.GetMethod("ClearQueue")!.Invoke(player,null);
            QueueBridge.FindAppend(playerType,typeof(IList<>).MakeGenericType(trackType))!.Invoke(player,new object?[]{big,null,false});Dispatcher.UIThread.RunJobs();
            // The whole native part of an Apply with the weighted shuffle ON, as a user who clicks the button would have it.
            var toggle=(ICommand)Get(player,"ToggleShuffleCommand")!;
            clock.Restart();
            if(!(bool)Get(player,"IsShuffleEnabled")!)toggle.Execute(null);
            Console.WriteLine($"UI-THREAD native shuffle switched on with a 2,000-song queue (set-up, not part of Apply): {clock.Elapsed.TotalMilliseconds:0.0} ms");
            foreach(int size in new[]{2000,200})
            {
                var wanted=big.Cast<object>().Take(size).Select(t=>new TrackEntry(t,new Song("x","","","",0,false,false,false,null,true))).ToList();
                if(!(bool)Get(player,"IsShuffleEnabled")!)toggle.Execute(null);
                clock.Restart();
                using(new PlaybackLease(player)){ var lease=clock.Elapsed.TotalMilliseconds; bridge.Apply(wanted,true);
                    Console.WriteLine($"UI-THREAD native Apply path with shuffle on, {size:N0} songs: lease {lease:0.0} ms, lease + shuffle off + clear + append {clock.Elapsed.TotalMilliseconds:0.0} ms"); }
                Dispatcher.UIThread.RunJobs();
            }
            playerType.GetMethod("ClearQueue")!.Invoke(player,null);
            QueueBridge.FindAppend(playerType,typeof(IList<>).MakeGenericType(trackType))!.Invoke(player,new object?[]{big,null,false});Dispatcher.UIThread.RunJobs();
            var playing=Get(player,"CurrentTrack");int played=audio.Calls.Count(c=>c=="Play");
            using(var priority=new QueueFrontPolicy(player,_=>{},message=>throw new Exception(message)))
            {
                playerType.GetMethod("AddToQueue")!.Invoke(player,new object[]{big[1500]!,false});Dispatcher.UIThread.RunJobs();
                var upcoming=((IEnumerable)Get(player,"UpNext")!).Cast<object>().ToArray();
                check(upcoming.Length==2000 && ReferenceEquals(upcoming[0],big[1500]) && ReferenceEquals(upcoming[1],big[0]),"Requested song takes first position in an actual 2,000-song shuffle without duplicating it");
                check(ReferenceEquals(playing,Get(player,"CurrentTrack")) && played==audio.Calls.Count(c=>c=="Play"),"Large queue promotion leaves current audio untouched");
            }
        }
        finally
        {
            if (player is IDisposable disposable) disposable.Dispose();
        }
        PlayNow(ctor, args, library, trackType, check);
    }

    /// <summary>
    /// A song played from a list, the queue panel or the history during a session, on the installed player: Noctis replaces the
    /// queue with that list, True Shuffle plays the song now and puts the shuffle back behind it.
    /// </summary>
    private static void PlayNow(ConstructorInfo ctor, object?[] args, ServiceStub library, Type trackType, Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "shuffle-playnow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var songs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
        for (int i = 0; i < 60; i++)
        {
            var track = Activator.CreateInstance(trackType)!;
            Set(track, "Id", Guid.NewGuid()); Set(track, "Title", "Library " + i); Set(track, "Artist", "Test artist");
            Set(track, "Duration", TimeSpan.FromMinutes(3)); Set(track, "FilePath", Path.Combine(folder, "song " + i + ".mp3"));
            File.WriteAllText((string)Get(track, "FilePath")!, "synthetic audio");
            songs.Add(track);
        }
        library.Values["get_Tracks"] = songs;
        var player = ctor.Invoke(args);
        var playerType = player.GetType();
        var listOf = typeof(IList<>).MakeGenericType(trackType);
        var replace = playerType.GetMethod("ReplaceQueueAndPlay", new[] { listOf, typeof(int) })!;
        IList One(object track) { var l = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!; l.Add(track); return l; }
        object[] Upcoming() => ((IEnumerable)Get(player, "UpNext")!).Cast<object>().ToArray();
        object Playing() => Get(player, "CurrentTrack")!;
        var host = new UiHost(Path.Combine(folder, "plugin-data"));
        var plugin = new TrueShufflePlugin();
        object? Field(string name) => typeof(TrueShufflePlugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin);
        void SetField(string name, object value) => typeof(TrueShufflePlugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, value);
        void Pump(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!ready() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(2); }
            Dispatcher.UIThread.RunJobs();
            if (!ready()) throw new Exception("Headless operation did not finish");
        }
        void Settle() { Dispatcher.UIThread.RunJobs(); Pump(() => ((Task)Field("_refill")!).IsCompleted); }
        bool Active() => Field("_lease") != null && Field("_roll") != null;
        // Noctis tells plugins about a song change from the player's own property change, at once.
        ((System.ComponentModel.INotifyPropertyChanged)player).PropertyChanged += (_, e) => { if (e.PropertyName == "CurrentTrack") host.Playing.Fire(); };
        try
        {
            replace.Invoke(player, new object[] { songs, 0 }); Dispatcher.UIThread.RunJobs();
            plugin.Initialize(host);
            Pump(() => ((Task)Field("_load")!).IsCompleted);
            var shell = new NativeShell(player, library);
            SetField("_bridge", new QueueBridge(shell, false)); SetField("_windowSize", 10); SetField("_refillAt", 3);
            var apply = (Task)typeof(TrueShufflePlugin).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(plugin, new object[] { ShuffleMode.NoRepeats, "Whole library", true, false })!;
            Pump(() => apply.IsCompleted); Settle();
            var shuffle = Upcoming();
            var interrupted = Playing();
            check(Active() && shuffle.Length == 10, "Installed player: a session with ten upcoming songs is running");

            // Double-click in the Songs list: Noctis makes the whole list the queue, from the song picked.
            var picked = songs.Cast<object>().First(t => !shuffle.Contains(t) && !ReferenceEquals(t, interrupted));
            replace.Invoke(player, new object[] { songs, songs.IndexOf(picked) }); Settle();
            check(ReferenceEquals(Playing(), picked) && Upcoming().SequenceEqual(shuffle), "A song played from a list plays now and the shuffle stays behind it, in its order");
            check(Active() && host.Notices.Count(n => n.Contains("Playing it now")) == 1 && !host.Notices.Any(n => n.Contains("session ended")), "... the session goes on, with one notice that says so");
            var heard = (HashSet<string>)Field("_seen")!;
            check(heard.Contains(new QueueBridge(new NativeShell(player, library), false).Current!.Song.Key), "... and the picked song counts as heard in the cycle");
            ((ICommand)Get(player, "PreviousCommand")!).Execute(null); Settle();
            check(ReferenceEquals(Playing(), interrupted) && ReferenceEquals(Upcoming()[0], picked) && Upcoming().Skip(1).SequenceEqual(shuffle) && Active(),
                "Previous returns to the song that was interrupted, not to the list's song above the picked one");
            ((ICommand)Get(player, "NextCommand")!).Execute(null); Settle();
            check(ReferenceEquals(Playing(), picked) && Upcoming().SequenceEqual(shuffle), "Next plays the picked song again, then the shuffle");

            // The queue panel: Noctis drops the songs above the one clicked.
            var target = shuffle[4];
            playerType.GetMethod("PlayFromUpNextAt")!.Invoke(player, new object[] { 4 }); Settle();
            check(ReferenceEquals(Playing(), target) && Upcoming().SequenceEqual(shuffle.Where(t => !ReferenceEquals(t, target))) && Active(),
                "A song clicked in the queue plays now and the songs above it are kept");

            // A song that is further down the shuffle, played from a list: it is not left in the queue to play twice.
            shuffle = Upcoming();
            var later = shuffle[6];
            replace.Invoke(player, new object[] { songs, songs.IndexOf(later) }); Settle();
            check(ReferenceEquals(Playing(), later) && Upcoming().SequenceEqual(shuffle.Where(t => !ReferenceEquals(t, later))), "A song already further down the shuffle plays now and is taken out of its old place");

            // The history (or anything else that plays one song alone).
            shuffle = Upcoming();
            var single = songs.Cast<object>().First(t => !shuffle.Contains(t) && !ReferenceEquals(t, Playing()));
            replace.Invoke(player, new object[] { One(single), 0 }); Settle();
            check(ReferenceEquals(Playing(), single) && Upcoming().SequenceEqual(shuffle) && Active(), "A song played on its own (history, search) keeps the shuffle too");

            // Double-clicking the song that is already playing restarts it and keeps the shuffle.
            shuffle = Upcoming();
            replace.Invoke(player, new object[] { songs, songs.IndexOf(Playing()) }); Settle();
            check(ReferenceEquals(Playing(), single) && Upcoming().SequenceEqual(shuffle) && Active(), "Playing the song that is playing again keeps the shuffle");
            check(host.Notices.Count(n => n.Contains("Playing it now")) == 1, "The notice is shown once, not for every song");

            // The rolling queue goes on behind a played song.
            var started = new List<object>();
            for (int step = 0; step < 12; step++) { ((ICommand)Get(player, "NextCommand")!).Execute(null); Settle(); started.Add(Playing()); }
            check(Active() && Upcoming().Length > 0 && Upcoming().Distinct().Count() == Upcoming().Length, "More shuffle is added behind as usual, with nothing queued twice");

            // The user's own songs: Add to Queue keeps the order they were added in, ahead of the shuffle; Play Next goes first.
            object Call(string name, params object[] given)
            {
                var method = playerType.GetMethods().Where(m => m.Name == name && m.GetParameters().Length >= given.Length
                    && m.GetParameters().Skip(given.Length).All(x => x.IsOptional)).OrderBy(m => m.GetParameters().Length).First();
                return method.Invoke(player, given.Concat(method.GetParameters().Skip(given.Length).Select(x => x.DefaultValue)).ToArray())!;
            }
            List<object> Mine() => (List<object>)Field("_priority")!.GetType().GetMethod("Manual")!.Invoke(Field("_priority"), new object[] { Upcoming() })!;
            shuffle = Upcoming();
            var outside = songs.Cast<object>().Where(t => !shuffle.Contains(t) && !ReferenceEquals(t, Playing())).ToArray();
            Call("AddToQueue", outside[0]); Settle();
            Call("AddToQueue", outside[1]); Settle();
            check(ReferenceEquals(Upcoming()[0], outside[0]) && ReferenceEquals(Upcoming()[1], outside[1]) && Upcoming().Skip(2).SequenceEqual(shuffle),
                "Songs added to the queue wait ahead of the shuffle in the order they were added");
            // Play Next on a song heard a moment ago (it is in Noctis's history): still the user's own song.
            outside[2] = ((IEnumerable)Get(player, "History")!).Cast<object>().First(t => !Upcoming().Contains(t) && !ReferenceEquals(t, Playing()) && !outside.Take(2).Contains(t));
            Call("AddNext", outside[2]); Settle();
            check(ReferenceEquals(Upcoming()[0], outside[2]) && Mine().SequenceEqual(new[] { outside[2], outside[0], outside[1] }) && Active(),
                "Play Next goes first, and all three count as the user's own songs");

            // Another playlist: a song played from it plays now, that playlist is shuffled from now on, the user's songs stay first.
            var playlist = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
            var keys = new QueueBridge(shell, false);
            var heardSoFar = (HashSet<string>)Field("_seen")!;
            foreach (var track in songs.Cast<object>().Where(t => !outside.Take(3).Contains(t) && !ReferenceEquals(t, Playing()) && !heardSoFar.Contains(keys.Entry(t).Song.Key)).Take(8))
                playlist.Add(track);
            var fromPlaylist = playlist[2]!;
            replace.Invoke(player, new object[] { playlist, 2 }); Settle();
            var members = playlist.Cast<object>().Where(t => !ReferenceEquals(t, fromPlaylist)).ToHashSet();
            var queue = Upcoming();
            check(ReferenceEquals(Playing(), fromPlaylist) && queue.Take(3).SequenceEqual(new[] { outside[2], outside[0], outside[1] }), "Switching playlists keeps the user's songs first, in their order");
            check(queue.Skip(3).ToHashSet().SetEquals(members) && queue.Length == 3 + members.Count && Active(), "... followed by the whole other playlist, and nothing from the old shuffle");
            check(host.Notices.Count(n => n.Contains("Now shuffling the list you played from")) == 1, "The switch is explained once");
            var heardBefore = ((HashSet<string>)Field("_seen")!).Count;
            // Within that playlist again: it is the same list now, so the shuffle stays.
            queue = Upcoming();
            var again = queue[5];
            replace.Invoke(player, new object[] { playlist, playlist.IndexOf(again) }); Settle();
            check(ReferenceEquals(Playing(), again) && Upcoming().SequenceEqual(queue.Where(t => !ReferenceEquals(t, again))), "A song from the playlist being shuffled plays now and keeps that shuffle");
            check(((HashSet<string>)Field("_seen")!).Count > heardBefore, "... and counts as heard");
            // The playlist runs out and goes round again; the user's songs play first; nothing from outside the playlist comes back.
            var played = new List<object>();
            for (int step = 0; step < 14; step++) { ((ICommand)Get(player, "NextCommand")!).Execute(null); Settle(); played.Add(Playing()); }
            check(played.Take(3).SequenceEqual(new[] { outside[2], outside[0], outside[1] }) && played.Skip(3).All(t => playlist.Contains(t)) && Active() && Upcoming().Length > 0,
                "The user's songs play first, then only that playlist, round again when it runs out");
            check(Mine().Count == 0, "Songs of the user's that have played are no longer kept ahead");

            // The Songs page (here with a search that shows three songs): the whole library is shuffled again.
            Call("AddToQueue", outside[3]); Settle();
            shell.CurrentView = new Noctis.ViewModels.LibrarySongsViewModel();
            var searched = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
            foreach (var track in songs.Cast<object>().Where(t => !playlist.Contains(t) && !ReferenceEquals(t, outside[3])).Take(3)) searched.Add(track);
            replace.Invoke(player, new object[] { searched, 1 }); Settle();
            queue = Upcoming();
            check(ReferenceEquals(Playing(), searched[1]) && ReferenceEquals(queue[0], outside[3]) && queue.Length == 11 && queue.Skip(1).Any(t => !playlist.Contains(t) && !searched.Contains(t)),
                "A song from the Songs page switches back to the whole library, with the user's song still first");
            shell.CurrentView = null;

            // Clearing the queue is still the way to start over.
            playerType.GetMethod("ClearQueue")!.Invoke(player, null); Settle();
            check(!Active() && Upcoming().Length == 0 && host.Notices.Any(n => n.Contains("cleared or replaced")), "Clearing the queue still ends the session");
            var free = songs.Cast<object>().First(t => !ReferenceEquals(t, Playing()));
            replace.Invoke(player, new object[] { songs, songs.IndexOf(free) }); Settle();
            check(ReferenceEquals(Playing(), free) && Upcoming().Length == songs.Count - songs.IndexOf(free) - 1, "Without a session, playing from a list is Noctis's own behaviour again");
        }
        finally
        {
            plugin.Shutdown();
            if (player is IDisposable disposable) disposable.Dispose();
        }
    }

    private static object? Get(object o, string p) => o.GetType().GetProperty(p)!.GetValue(o);
    private static void Set(object o, string p, object v) => o.GetType().GetProperty(p)!.SetValue(o, v);
    private sealed class NativeShell
    {
        private readonly object _library;
        public object Library => _library;
        public object Player { get; } public FakeSettings Settings { get; } = new();
        public object? CurrentView { get; set; }
        public NativeShell(object player, object library) { Player = player; _library = library; }
    }
}

public class ServiceStub : DispatchProxy
{
    public static readonly object Unhandled = new();
    public Dictionary<string, object?> Values { get; } = new();
    public List<string> Calls { get; } = new();
    private readonly Dictionary<string, Delegate?> _events = new();
    public Func<MethodInfo, object?[]?, object?>? Custom { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!; string name = method.Name; Calls.Add(name);
        if (name.StartsWith("add_")) { string key = name[4..]; _events.TryGetValue(key, out var old); _events[key] = Delegate.Combine(old, (Delegate?)args![0]); return null; }
        if (name.StartsWith("remove_")) { string key = name[7..]; _events.TryGetValue(key, out var old); _events[key] = Delegate.Remove(old, (Delegate?)args![0]); return null; }
        if (name.StartsWith("set_")) { Values["get_" + name[4..]] = args![0]; return null; }
        if (Values.TryGetValue(name, out var value)) return value;
        if (Custom != null) { var answer = Custom(method, args); if (!ReferenceEquals(answer, Unhandled)) return answer; }
        return Default(method.ReturnType);
    }
    private static object? Default(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, new[] { Default(type.GetGenericArguments()[0]) });
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return Activator.CreateInstance(type, Default(type.GetGenericArguments()[0]));
        if (type == typeof(string)) return "test";
        if (type == typeof(TimeSpan)) return TimeSpan.Zero;
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) return Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]));
        return null;
    }
    public void Raise(string name) { if (_events.TryGetValue(name, out var handler)) handler?.DynamicInvoke(this, EventArgs.Empty); }
}

namespace Noctis.ViewModels
{
    /// <summary>Stands for Noctis's Songs page: the plugin knows that page by this name.</summary>
    internal sealed class LibrarySongsViewModel { }
}
