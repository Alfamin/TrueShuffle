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
                ((ICommand)Get(player, "NextCommand")!).Execute(null); Dispatcher.UIThread.RunJobs();
                check(Get(Get(player, "CurrentTrack")!, "Title")!.ToString() == "Synthetic 3", "Actual native Next consumes first custom song");
                audio.Raise("TrackEnded"); Dispatcher.UIThread.RunJobs();
                check(Get(Get(player, "CurrentTrack")!, "Title")!.ToString() == "Synthetic 2", "Actual natural track end follows custom order");
                audio.Raise("TrackEnded"); Dispatcher.UIThread.RunJobs();
                check(!bridge.Snapshot("Current queue").Any(e => e.Song.Title != "Synthetic 2") && !(bool)Get(player, "IsPlaying")!, "Actual queue drain does not choose biased Autoplay or repeat");
            }
            check(audio.Calls.Contains("Play") && !library.Calls.Any(c => c == "ScanAsync" || c == "ImportFilesAsync"), "Native integration uses fake audio and never scans real music");
        }
        finally
        {
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
