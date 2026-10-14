using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TrueShuffle;

/// <summary>
/// What still runs on the UI thread during the first Apply of a 2,100-song library. The numbers are
/// printed (a machine is not a test oracle); the plugin's calls are timed in the order it makes them.
/// </summary>
internal static class UiThreadTests
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "shuffle-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var main = new FakeMain(); main.Library.Tracks.Clear(); main.Player.IsShuffleEnabled = false;
        main.Library.Tracks.AddRange(Enumerable.Range(1, 2100).Select(i =>
        {
            var file = Path.Combine(folder, "song " + i + ".mp3"); File.WriteAllText(file, "x");
            return new FakeTrack { Id = i.ToString(), FilePath = file, IsRemoteStream = false };
        }));
        main.Player.CurrentTrack = main.Library.Tracks[0];
        var host = new UiHost(Path.Combine(folder, "plugin-data")); var plugin = new TrueShufflePlugin(); plugin.Initialize(host);
        var load = (Task)typeof(TrueShufflePlugin).GetField("_load", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        Pump(() => load.IsCompleted);
        var bridge = new QueueBridge(main, false);
        typeof(TrueShufflePlugin).GetField("_bridge", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, bridge);
        var apply = typeof(TrueShufflePlugin).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // The calls the plugin makes on the UI thread, one by one.
        Console.WriteLine($"UI-THREAD phases, {main.Library.Tracks.Count:N0} songs (first call / second call):");
        Phase("snapshot of the source (reflection over every track)", () => bridge.SnapshotRaw("Whole library"));
        Phase("current song (one file identity)", () => bridge.Current);
        Phase("for comparison, what 1.1.2 did three times: snapshot with the file identity of every song", () => bridge.Snapshot("Whole library"));
        var queue = bridge.Snapshot("Whole library").Skip(1).ToList();
        Phase("bridge.Apply of the whole queue into the fake player", () => bridge.Apply(queue, true));
        var two = queue.Take(200).ToList();
        Phase("bridge.Apply of 200 songs into the fake player", () => bridge.Apply(two, true));
        main.Player.UpNext.Clear();

        foreach (var mode in new[] { ShuffleMode.Discovery, ShuffleMode.Discovery })
        {
            var clock = Stopwatch.StartNew();
            var task = (Task)apply.Invoke(plugin, new object[] { mode, "Whole library", true, false })!;
            double before = clock.Elapsed.TotalMilliseconds, longest = 0;
            while (!task.IsCompleted)
            {
                long start = Stopwatch.GetTimestamp();
                Dispatcher.UIThread.RunJobs();
                longest = Math.Max(longest, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Thread.Sleep(1);
            }
            Console.WriteLine($"UI-THREAD Apply ({mode}): up to the first await {before:0.0} ms, continuation after the worker thread {longest:0.0} ms, whole call {clock.Elapsed.TotalMilliseconds:0.0} ms, queued {main.Player.UpNext.Count:N0}");
            check(task.IsCompletedSuccessfully && main.Player.UpNext.Count > 0, "Apply of a 2,100-song library completes");
        }
        plugin.Shutdown();
    }

    private static void Phase(string name, Action work)
    {
        var times = new double[2];
        for (int run = 0; run < 2; run++)
        {
            long start = Stopwatch.GetTimestamp(); work();
            times[run] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Console.WriteLine($"  {name}: {times[0]:0.0} ms / {times[1]:0.0} ms");
    }

    private static void Phase<T>(string name, Func<T> work) => Phase(name, () => { work(); });

    private static void Pump(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!ready() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
    }
}
