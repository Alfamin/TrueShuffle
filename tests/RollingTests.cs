using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TrueShuffle;

/// <summary>The rolling queue: a window of the plan in Noctis's queue, more added as it runs low, endless until the session ends.</summary>
internal static class RollingTests
{
    public static void Run(Action<bool, string> check)
    {
        // The button's top-level modes operate on the session's source, not just its visible window.
        using (var rig = new Rig(12, 5, 2, track => track.PlayCount = 12 - int.Parse(track.Id)))
        {
            rig.Apply(ShuffleMode.EqualChance);
            rig.Apply(ShuffleMode.Discovery, "Current queue");
            var expected = rig.Main.Library.Tracks.Skip(1).OrderBy(t => t.PlayCount).Select(t => t.Id).ToArray();
            check(rig.Plan().SequenceEqual(expected) && rig.Queue.SequenceEqual(expected.Take(5)), "Changing mode to Discovery keeps the full source behind the rolling window and takes effect immediately");
            check(rig.Main.Player.CurrentTrack!.Id == "1" && rig.Main.Player.Replacements == 0, "Changing mode needs no song click and never restarts current playback");
        }

        using (var rig = new Rig(12, 5, 2))
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(TrueShufflePlugin).GetField("_refill", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Plugin, gate.Task);
            var first = rig.BeginApply(ShuffleMode.EqualChance);
            var latest = rig.BeginApply(ShuffleMode.Discovery);
            check(!latest.IsCompleted, "A mode selected during preparation is retained instead of silently dropped");
            gate.SetResult(); Pump(() => first.IsCompleted && latest.IsCompleted); rig.Settle();
            check(rig.Roll!.GetType().GetProperty("Mode")!.GetValue(rig.Roll) is ShuffleMode.Discovery, "The latest selected mode is applied after the pending preparation yields");
        }

        // The window: only the first part of the plan is queued, in plan order; the rest waits in memory.
        using (var rig = new Rig(12, 5, 2))
        {
            rig.Apply(ShuffleMode.NoRepeats);
            var plan = rig.Plan();
            check(plan.Length == 11 && rig.Queue.Length == 5 && rig.Queue.SequenceEqual(plan.Take(5)), "Apply queues only the first window of the plan, in plan order");
            check(rig.Remaining == 6 && rig.Seen == 1, "The rest of the plan waits in memory and nothing counts as heard yet");
            rig.Next(); rig.Next();
            check(rig.Queue.Length == 3 && rig.Remaining == 6, "Above the threshold nothing is added");
            rig.Next();
            check(rig.Queue.SequenceEqual(plan.Skip(3).Take(7)) && rig.Remaining == 1, "At the threshold the next window is appended behind the upcoming songs, in plan order");
            check(rig.Seen == 4, "A song counts as heard when it starts, not when it is appended");
            check(rig.Main.Player.CurrentTrack!.Id == plan[2] && rig.Main.Player.Replacements == 0, "Appending never restarts or replaces the playing song");
        }

        // Listening on through several cycles: music never stops, nothing is queued twice, a fresh cycle forgets the old one.
        foreach (var mode in new[] { ShuffleMode.NoRepeats, ShuffleMode.EqualChance })
            using (var rig = new Rig(12, 5, 2))
            {
                rig.Apply(mode);
                var started = new List<string> { "1" };
                bool neverEmpty = true, tidy = true, noImmediateRepeat = true, forgot = false;
                int previousSeen = rig.Seen;
                for (int step = 0; step < 45 && neverEmpty; step++)
                {
                    if (rig.Queue.Length == 0) { neverEmpty = false; break; }
                    rig.Next();
                    var current = rig.Main.Player.CurrentTrack!.Id;
                    noImmediateRepeat &= started[^1] != current;
                    started.Add(current);
                    var queue = rig.Queue;
                    tidy &= queue.Distinct().Count() == queue.Length && !queue.Contains(current);
                    if (rig.Seen < previousSeen) forgot = true;
                    previousSeen = rig.Seen;
                }
                check(neverEmpty && started.Count > 40, $"{mode}: the queue never runs dry across cycle boundaries");
                check(tidy && noImmediateRepeat, $"{mode}: nothing is queued twice, the playing song is never queued, no song follows itself");
                if (mode == ShuffleMode.NoRepeats)
                    check(started.Take(12).Distinct().Count() == 12 && forgot, "No repeats: the first cycle plays every song once, then a fresh cycle starts");
            }

        // The user's own edits win: a removed song is not put back, a song moved up is not played twice, a song gone from the library is not queued.
        using (var rig = new Rig(12, 5, 2, null, local: true))
        {
            rig.Apply(ShuffleMode.NoRepeats);
            var plan = rig.Plan();
            var removed = rig.Queue[2];
            rig.Main.Player.UpNext.RemoveAt(2);
            var jumper = plan[7];
            rig.Main.Player.UpNext.Insert(0, rig.Main.Library.Tracks.First(t => t.Id == jumper));
            var deleted = plan[6];
            rig.Main.Library.Tracks.RemoveAll(t => t.Id == deleted);
            var started = new List<string>(); var everQueued = new List<string>();
            var thisPlan = rig.PlanList();
            // While this plan lasts. (A later cycle may bring a removed song back: it was never heard.)
            for (int step = 0; step < 12 && ReferenceEquals(rig.PlanList(), thisPlan); step++)
            {
                rig.Next(); started.Add(rig.Main.Player.CurrentTrack!.Id);
                if (ReferenceEquals(rig.PlanList(), thisPlan)) everQueued.AddRange(rig.Queue);
            }
            check(started[0] == jumper && started.Count(id => id == jumper) == 1, "A song the user moved up plays next and is not queued again from the plan");
            check(!everQueued.Contains(removed) && !started.Contains(removed), "A song the user removed from the queue is not put back");
            check(!everQueued.Contains(deleted) && !started.Contains(deleted), "A song no longer in the library is skipped");
        }

        // Ending the session ends the continuation, including a plan that is being made at that moment.
        using (var rig = new Rig(12, 5, 2))
        {
            rig.Apply(ShuffleMode.NoRepeats);
            rig.Next();
            rig.Main.Player.RepeatMode = FakeRepeat.One; Dispatcher.UIThread.RunJobs();
            check(rig.Roll == null, "Changing the normal controls drops the plan");
            rig.Next(); rig.Next(); rig.Next();
            check(rig.Queue.Length == 1, "No songs are added after the session ended");
        }
        using (var rig = new Rig(12, 5, 2))
        {
            rig.Apply(ShuffleMode.NoRepeats);
            for (int step = 0; step < 20 && !(rig.Remaining == 0 && rig.Queue.Length == 3); step++) rig.Next(); // the plan is used up and the queue is about to run low
            rig.Advance(); Dispatcher.UIThread.RunJobs(); // the next plan is being made on the worker thread now
            var before = rig.Queue.Length;
            rig.EndSession();
            rig.Settle();
            check(rig.Roll == null && rig.Queue.Length == before, "A plan that is still being made when the session ends adds nothing");
        }
        using (var rig = new Rig(12, 5, 2))
        {
            rig.Apply(ShuffleMode.NoRepeats);
            typeof(TrueShufflePlugin).GetMethod("OnQueueReplaced", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Plugin, null);
            check(rig.Roll == null && FieldOf(rig.Plugin, "_lease") == null && rig.Host.Notices.Any(n => n.Contains("cleared or replaced")),
                "A cleared or replaced queue ends the session and says so");
        }

        // Never played only: when no never-played song is left the queue plays out, with one notice.
        using (var rig = new Rig(8, 5, 2))
        {
            rig.Apply(ShuffleMode.NeverPlayed);
            var plan = rig.Plan();
            var playedElsewhere = plan[6];
            rig.Main.Library.Tracks.First(t => t.Id == playedElsewhere).PlayCount = 1;
            var queued = new List<string>();
            int longest = 0;
            while (rig.Queue.Length > 0 && longest++ < 20) { rig.Next(); queued.AddRange(rig.Queue); }
            check(!queued.Contains(playedElsewhere), "A song that was played for the first time elsewhere is not queued as never played");
            check(rig.Host.Notices.Count(n => n.Contains("No never-played songs are left")) == 1 && rig.Queue.Length == 0, "Never played only plays out the queue and says once that none are left");
            check(rig.Roll == null || (bool)rig.Roll.GetType().GetProperty("Finished")!.GetValue(rig.Roll)!, "... and stops looking for more");
        }

        // Discovery: the next plan is made again from the play counts.
        using (var rig = new Rig(9, 3, 1, track => track.PlayCount = (int.Parse(track.Id) * 7) % 5))
        {
            rig.Apply(ShuffleMode.Discovery);
            var firstPlan = rig.PlanList();
            for (int step = 0; step < 30 && ReferenceEquals(rig.PlanList(), firstPlan) && rig.Queue.Length > 0; step++) rig.Next();
            var counts = rig.Plan().Select(id => rig.Main.Library.Tracks.First(t => t.Id == id).PlayCount).ToArray();
            check(!ReferenceEquals(rig.PlanList(), firstPlan) && counts.Length > 0 && counts.SequenceEqual(counts.Order()), "Discovery makes its next plan from the play counts, least played first");
        }

        // For you: a short window at a time, re-picked as the queue runs low, never a song twice, never the playing one.
        using (var rig = new Rig(120, 200, 20))
        {
            rig.Apply(ShuffleMode.ForYou);
            var first = rig.Queue;
            check(first.Length == 25 && first.Distinct().Count() == 25 && !first.Contains(rig.Main.Player.CurrentTrack!.Id) && rig.Remaining == 0,
                "For you queues a window of 25 picks, re-planned rather than drawn from one long plan");
            var heard = new List<string> { rig.Main.Player.CurrentTrack!.Id };
            bool neverEmpty = true, tidy = true;
            for (int step = 0; step < 60 && neverEmpty; step++)
            {
                if (rig.Queue.Length == 0) { neverEmpty = false; break; }
                rig.Next();
                heard.Add(rig.Main.Player.CurrentTrack!.Id);
                tidy &= rig.Queue.Distinct().Count() == rig.Queue.Length && !rig.Queue.Contains(rig.Main.Player.CurrentTrack!.Id);
            }
            check(neverEmpty && tidy, "For you keeps going across re-plans, with nothing queued twice");
            check(heard.Take(60).Distinct().Count() == 60, "Within a sitting no song comes back: what was just heard rests");
            check((string)typeof(TrueShufflePlugin).GetMethod("RefillAt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Plugin, new[] { rig.Roll! })!.ToString()! == "6",
                "For you re-picks when 6 songs are left, so a skip soon changes what comes");
        }

        // A library smaller than the threshold is queued whole, and the session stays alive for later.
        using (var rig = new Rig(12, 200, 20))
        {
            rig.Apply(ShuffleMode.NoRepeats);
            check(rig.Queue.Length == 11 && rig.Remaining == 0 && rig.Roll != null && !(bool)rig.Roll.GetType().GetProperty("Finished")!.GetValue(rig.Roll)!, "A small library is queued whole and the session keeps running");
            rig.Next();
            check(rig.Queue.Length == 10 && rig.Queue.Distinct().Count() == 10, "... and nothing is queued twice while everything eligible is already queued");
        }
    }

    private static object? FieldOf(object o, string name)
        => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);

    private static void Pump(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!ready() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
        Dispatcher.UIThread.RunJobs();
        if (!ready()) throw new Exception("Headless operation did not finish");
    }

    /// <summary>A plugin on a fake Noctis whose library holds songs 1..n and whose player plays song 1.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly TrueShufflePlugin Plugin = new();
        public readonly UiHost Host;
        public readonly FakeMain Main = new();

        /// <param name="local">Songs with real files (so a song can also be missing from the library); otherwise streams.</param>
        public Rig(int songs, int window, int refillAt, Action<FakeTrack>? tweak = null, bool local = false)
        {
            var folder = Path.Combine(Path.GetTempPath(), "shuffle-rolling-" + Guid.NewGuid().ToString("N"));
            Host = new UiHost(Path.Combine(folder, "plugin-data"));
            Main.Library.Tracks.Clear(); Main.Player.IsShuffleEnabled = false;
            for (int i = 1; i <= songs; i++)
            {
                var track = new FakeTrack { Id = i.ToString() };
                if (local)
                {
                    Directory.CreateDirectory(folder);
                    track.FilePath = Path.Combine(folder, "song " + i + ".mp3"); File.WriteAllText(track.FilePath, "synthetic audio"); track.IsRemoteStream = false;
                }
                tweak?.Invoke(track); Main.Library.Tracks.Add(track);
            }
            Main.Player.CurrentTrack = Main.Library.Tracks[0];
            Plugin.Initialize(Host);
            Pump(() => ((Task)FieldOf(Plugin, "_load")!).IsCompleted);
            Set("_bridge", new QueueBridge(Main, false)); Set("_windowSize", window); Set("_refillAt", refillAt);
        }

        private void Set(string name, object value) => typeof(TrueShufflePlugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Plugin, value);

        public void Apply(ShuffleMode mode, string scope = "Whole library")
        {
            var apply = BeginApply(mode, scope);
            Pump(() => apply.IsCompleted);
            Settle();
        }
        public Task BeginApply(ShuffleMode mode, string scope = "Whole library") => (Task)typeof(TrueShufflePlugin)
            .GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Plugin, new object[] { mode, scope, true, false })!;

        /// <summary>The next song starts: the player takes it from the queue and says so.</summary>
        public void Advance()
        {
            Main.Player.CurrentTrack = Main.Player.UpNext[0]; Main.Player.UpNext.RemoveAt(0); Host.Playing.Fire();
        }
        public void Next() { Advance(); Settle(); }
        public void Settle() { Dispatcher.UIThread.RunJobs(); Pump(() => ((Task)FieldOf(Plugin, "_refill")!).IsCompleted); }
        public void EndSession() => typeof(TrueShufflePlugin).GetMethod("EndSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Plugin, new object[] { true });

        public string[] Queue => Main.Player.UpNext.Select(t => t.Id).ToArray();
        public object? Roll => FieldOf(Plugin, "_roll");
        public int Seen => ((HashSet<string>)FieldOf(Plugin, "_seen")!).Count;
        public int Remaining => (int)Roll!.GetType().GetProperty("Remaining")!.GetValue(Roll)!;
        public List<TrackEntry> PlanList() => (List<TrackEntry>)Roll!.GetType().GetProperty("Plan")!.GetValue(Roll)!;
        public string[] Plan() => PlanList().Select(e => e.Song.Id).ToArray();

        public void Dispose() { Plugin.Shutdown(); }
    }
}
