using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrueShuffle;

/// <summary>The listening log (honest labels from what the player shows) and the For you picks (pure, seeded).</summary>
internal static class ForYouTests
{
    private static readonly TimeSpan Length = TimeSpan.FromSeconds(240);

    public static void Run(Action<bool, string> check)
    {
        Tracker(check);
        Store(check);
        Model(check);
        Planner(check);
    }

    /// <summary>Plays a song second by second: <paramref name="script"/> is (position, playing) per second of wall time.</summary>
    private static PlayRecord Play(IEnumerable<(double Position, bool Playing)> script, PlayEnd end, double? finalPosition = null)
    {
        var tracker = new PlayTracker();
        var clock = TimeSpan.Zero;
        var start = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        tracker.Start("song", "path", "Title", "Artist", Length, "test", start, clock, TimeSpan.Zero, true);
        double last = 0;
        foreach (var (position, playing) in script)
        {
            clock += TimeSpan.FromSeconds(1);
            tracker.Sample(TimeSpan.FromSeconds(position), playing, clock);
            last = position;
        }
        return tracker.End(end, TimeSpan.FromSeconds(finalPosition ?? last), start + clock, clock)!;
    }
    private static IEnumerable<(double, bool)> Listen(double from, double seconds)
        { for (int i = 1; i <= seconds; i++) yield return (from + i, true); }
    private static IEnumerable<(double, bool)> Pause(double at, double seconds)
        { for (int i = 1; i <= seconds; i++) yield return (at, false); }

    private static void Tracker(Action<bool, string> check)
    {
        var natural = Play(Listen(0, 238), PlayTracker.Classify(false, false, true, TimeSpan.FromSeconds(2), 238.0 / 240));
        check(natural.End == PlayEnd.Finished && natural.Outcome == PlayOutcome.Completed, "Telemetry_NaturalEnd_IsPositive: a song played to its end counts as finished");
        var early = Play(Listen(0, 20), PlayTracker.Classify(false, false, true, TimeSpan.FromSeconds(220), 20.0 / 240));
        check(early.End == PlayEnd.Skipped && early.Outcome == PlayOutcome.StrongSkip, "Telemetry_EarlyNext_IsStrongSkip: Next after 20 s of 4 min is a skip");
        var half = Play(Listen(0, 130), PlayTracker.Classify(false, false, true, TimeSpan.FromSeconds(110), 130.0 / 240));
        check(half.End == PlayEnd.Skipped && half.Outcome == PlayOutcome.Qualified, "Telemetry_HalfPlayedThenNext_IsNotStrongSkip: Next after half the song counts as listened");
        var slip = Play(Listen(0, 1), PlayEnd.Skipped);
        check(slip.Outcome == PlayOutcome.QuickSkip, "A Next within the first seconds is a slip, weaker than a skip");
        var paused = Play(Listen(0, 30).Concat(Pause(30, 300)).Concat(Listen(30, 30)), PlayEnd.Stopped);
        check(Math.Abs(paused.ListenedMs - 60_000) <= 2_000, $"Telemetry_PauseDoesNotCountAsListening: 30 s, 5 min paused, 30 s = about 60 s (got {paused.ListenedMs} ms)");
        var forward = Play(Listen(0, 10).Append((210, true)).Concat(Listen(210, 10)), PlayEnd.Stopped);
        check(Math.Abs(forward.ListenedMs - 20_000) <= 2_000 && forward.Coverage < 0.1, $"Telemetry_SeekForwardDoesNotFakeListening: about 20 s heard (got {forward.ListenedMs} ms)");
        var back = Play(Listen(0, 60).Append((30, true)).Concat(Listen(30, 60)), PlayEnd.Stopped);
        check(Math.Abs(back.ListenedMs - 120_000) <= 2_000 && Math.Abs(back.Coverage - 90.0 / 240) < 0.02,
            $"Telemetry_SeekBackward_DoesCountActualRepeatedListeningTime: 120 s heard, but only 90 s of the song covered (got {back.ListenedMs} ms, {back.Coverage:P0})");
        var shutdown = Play(Listen(0, 20), PlayEnd.Stopped);
        check(shutdown.Outcome is not (PlayOutcome.StrongSkip or PlayOutcome.QuickSkip), "Telemetry_Shutdown_DoesNotLabelUnknownAsSkip");
        check(Play(Listen(0, 10), PlayTracker.Classify(false, true, false, TimeSpan.FromSeconds(230), 0.04)).Outcome == PlayOutcome.Neutral
            && Play(Listen(0, 10), PlayTracker.Classify(true, false, true, TimeSpan.FromSeconds(230), 0.04)).Outcome == PlayOutcome.Neutral,
            "Previous and picking another song are navigation, never skips");
        check(PlayTracker.Classify(false, false, true, TimeSpan.FromSeconds(10), 0.9) == PlayEnd.Finished
            && PlayTracker.Classify(false, false, true, TimeSpan.FromSeconds(10), 0.3) == PlayEnd.Skipped,
            "A crossfade into the next song in the last seconds of a heard song is a finish; jumping there and pressing Next is not");
        var tracker = new PlayTracker();
        tracker.Start("s", "p", "t", "a", Length, "test", DateTime.UtcNow, TimeSpan.Zero, TimeSpan.Zero, true);
        tracker.Sample(TimeSpan.FromSeconds(30), true, TimeSpan.FromHours(8)); // the computer slept
        check(tracker.End(PlayEnd.Stopped, TimeSpan.FromSeconds(30), DateTime.UtcNow, TimeSpan.FromHours(8))!.ListenedMs == 0, "A long gap between looks (sleep) is not counted as listening");
    }

    private static void Store(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "shuffle-listens-" + Guid.NewGuid().ToString("N"));
        var store = new ListenStore(folder);
        var a = new PlayRecord("1", "p1", "One", "Artist", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-6), 240_000, 230_000, 0.96, PlayEnd.Finished, "for-you");
        var b = a with { TrackId = "2", End = PlayEnd.Skipped, ListenedMs = 9_000, Coverage = 0.04 };
        store.Append(a); store.Append(b).Wait();
        File.AppendAllText(store.FilePath, "{\"trackId\":\"3\",\"broken\n"); // a crash in the middle of a line
        store.Append(b with { TrackId = "4" }).Wait();
        var loaded = store.Load();
        check(loaded.Select(r => r.TrackId).SequenceEqual(new[] { "1", "2", "4" }) && loaded[0] == a && loaded[1].Outcome == PlayOutcome.StrongSkip,
            "listens.jsonl keeps plays in order, survives a cut line, and reads back exactly");

        var history = Path.Combine(folder, "play_history.json");
        var id = Guid.NewGuid();
        File.WriteAllText(history, "[{\"TrackId\":\"" + id + "\",\"Title\":\"T\",\"Artist\":\"A\",\"PlayedAtUtc\":\"2026-10-01T10:00:00Z\",\"Skipped\":true}," +
            "{\"TrackId\":\"" + id + "\",\"Title\":\"T\",\"Artist\":\"A\",\"PlayedAtUtc\":\"2026-10-02T10:00:00Z\",\"Skipped\":false}," +
            "{\"TrackId\":\"" + id + "\",\"Title\":\"T\",\"Artist\":\"A\",\"PlayedAtUtc\":\"2026-10-09T10:00:00Z\",\"Skipped\":false}]");
        var imported = NoctisHistory.Read(history, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        check(imported.Count == 2 && imported.All(r => r.Imported) && imported[0].End == PlayEnd.Skipped && imported[1].End == PlayEnd.Unknown,
            "Noctis history is read only up to where our own records begin, marked as imported");
        check(TasteModel.Weight(imported[0]) > -2.5 && TasteModel.Weight(imported[1]) < 1.5, "Imported history counts for less than our own records");
    }

    private static TrackFacts F(string id, string artist, string genre, int year = 2019, int bpm = 140, string key = "", int plays = 0, bool favorite = false, int rating = 0)
        => new(id, "path-" + id, "Song " + id, artist, artist, "Album " + artist, TrackFacts.SplitGenres(genre), year, bpm, key, 200_000, plays, rating, favorite, false, null, null);

    private static PlayRecord P(string id, PlayEnd end, double coverage, DateTime at, int listenedMs = -1)
        => new(id, "path-" + id, "Song " + id, "", at, at.AddMinutes(3), 200_000, listenedMs >= 0 ? listenedMs : (int)(coverage * 200_000), coverage, end, "test");

    private static void Model(Action<bool, string> check)
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var library = new List<TrackFacts> { F("loved", "A", "rap"), F("skipped", "B", "pop"), F("never", "C", "rock"), F("fav", "D", "rap", favorite: true) };
        var plays = new List<PlayRecord>();
        for (int i = 0; i < 6; i++) plays.Add(P("loved", PlayEnd.Finished, 1, now.AddDays(-i)));
        for (int i = 0; i < 3; i++) plays.Add(P("skipped", PlayEnd.Skipped, 0.05, now.AddDays(-i)));
        var model = TasteModel.Build(plays, library, Array.Empty<IReadOnlyCollection<string>>(), now);
        check(model.Track("loved") > 0.5 && model.Track("skipped") < -0.5, "Finished plays raise a song, early skips lower it");
        check(model.Track("never") == 0 && !model.Familiar("never"), "HybridRanker_DoesNotTreatUnseenAsNegative: a song never played is unknown, not disliked");
        check(model.Track("fav") > 0 && model.Track("fav") < 1 && model.Track("loved") < 1, "HybridRanker_FavoriteBoostsWithoutMakingInfiniteLoop: taste is bounded");
        var old = TasteModel.Build(plays.Select(p => p with { StartUtc = p.StartUtc.AddDays(-200), EndUtc = p.EndUtc.AddDays(-200) }), library, Array.Empty<IReadOnlyCollection<string>>(), now);
        check(Math.Abs(old.Track("loved")) < model.Track("loved") / 4 && Math.Abs(old.Track("skipped")) < Math.Abs(model.Track("skipped")) / 4,
            "DecayFeature_RecentEventWeightsMoreThanOld: a phase from half a year ago has faded");
        check(model.Artist("a") > 0 && model.Artist("b") < 0, "Artist taste follows the artist's songs");
        check(TrackFacts.MainOf("Juice WRLD feat. Halsey") == "juice wrld" && TrackFacts.MainOf("Travis Scott; Drake") == "travis scott", "A song's main artist ignores featured artists");
        check(TasteModel.Similarity(F("x", "A", "rap", bpm: 70), F("y", "B", "rap", bpm: 140)) > TasteModel.Similarity(F("x", "A", "rap", bpm: 100), F("y", "B", "rap", bpm: 140)),
            "Half time feels like the same tempo");
        check(double.IsFinite(TasteModel.Similarity(F("x", "", ""), F("y", "", ""))), "Songs without tags compare as unknown, not as an error");
    }

    private static void Planner(Action<bool, string> check)
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var genres = new[] { "rap", "trap", "pop", "rock", "rnb" };
        var library = Enumerable.Range(0, 300).Select(i => F("t" + i, "artist" + (i % 30), genres[i % 5], 2000 + i % 25, 70 + i % 90)).ToList();
        var plays = new List<PlayRecord>();
        // The listener: rap and trap, finished often; rock skipped.
        foreach (var f in library.Where(f => f.Genres[0] is "rap" or "trap").Take(40)) plays.Add(P(f.Id, PlayEnd.Finished, 1, now.AddDays(-3)));
        foreach (var f in library.Where(f => f.Genres[0] == "rock").Take(15)) plays.Add(P(f.Id, PlayEnd.Skipped, 0.05, now.AddDays(-2)));
        var playlist = library.Where(f => f.Genres[0] == "rnb").Take(20).Select(f => f.Id).ToList();
        var model = TasteModel.Build(plays, library, new[] { (IReadOnlyCollection<string>)playlist }, now);
        var ids = library.Select(f => f.Id).ToList();
        ForYouContext Ctx(int seed, IReadOnlyList<string>? recent = null, IReadOnlyCollection<string>? skipped = null, double explore = 0.2, int count = 25)
            => new(recent ?? Array.Empty<string>(), skipped ?? Array.Empty<string>(), explore, count, seed, now);

        var a = ForYouPlanner.Plan(ids, model, Ctx(7));
        var b = ForYouPlanner.Plan(ids, model, Ctx(7));
        var c = ForYouPlanner.Plan(ids, model, Ctx(8));
        check(a.SequenceEqual(b) && !a.SequenceEqual(c), "HybridRanker_DeterministicGivenSeedAndData: the same seed gives the same picks, another seed others");
        check(a.Count == 25 && a.Distinct().Count() == 25, "Reranker_NoDuplicateRecording: 25 picks, none twice");
        var artists = a.Select(id => model.Facts(id)!.MainArtist).ToList();
        check(artists.Zip(artists.Skip(1)).All(p => p.First != p.Second)
              && Enumerable.Range(0, artists.Count).All(i => artists.Skip(Math.Max(0, i - 19)).Take(Math.Min(20, i + 1)).GroupBy(x => x).All(g => g.Count() <= 3)),
            "Reranker_ArtistCapAppliedInBalancedMode: no artist twice in a row, at most three in twenty");
        int Liked(List<string> plan) => plan.Count(id => model.Facts(id)!.Genres[0] is "rap" or "trap");
        int Rock(List<string> plan) => plan.Count(id => model.Facts(id)!.Genres[0] == "rock");
        check(Liked(a) > Rock(a) + 5, $"Picks follow taste: more rap/trap than skipped rock ({Liked(a)} vs {Rock(a)})");

        var current = library.First(f => f.Genres[0] == "rnb" && playlist.Contains(f.Id)).Id;
        var withContext = ForYouPlanner.Plan(ids, model, Ctx(7, new[] { current }));
        check(!withContext.Contains(current), "CandidateGenerator_ExcludesCurrentTrack");
        int FromPlaylist(List<string> plan) => plan.Count(playlist.Contains);
        check(FromPlaylist(withContext) > FromPlaylist(a), $"Songs from the playlist of the playing song come forward ({FromPlaylist(withContext)} vs {FromPlaylist(a)})");

        var skippedNow = library.Where(f => f.Genres[0] == "trap").Take(3).Select(f => f.Id).ToList();
        var afterSkips = ForYouPlanner.Plan(ids, model, Ctx(7, skippedNow, skippedNow));
        int Trap(List<string> plan) => plan.Count(id => model.Facts(id)!.Genres[0] == "trap");
        check(Trap(afterSkips) < Trap(ForYouPlanner.Plan(ids, model, Ctx(7, skippedNow))),
            "QA-10: songs like the ones just skipped are held back for this session");

        var recentlyHeard = plays.Where(p => p.End == PlayEnd.Finished).Select(p => p.TrackId).First();
        var justNow = TasteModel.Build(plays.Append(P(recentlyHeard, PlayEnd.Finished, 1, now.AddHours(-1))), library, Array.Empty<IReadOnlyCollection<string>>(), now);
        check(!ForYouPlanner.Plan(ids, justNow, Ctx(7, count: 100)).Contains(recentlyHeard), "A song heard an hour ago rests (no endless loop of favorites)");

        var cold = TasteModel.Build(Array.Empty<PlayRecord>(), library, Array.Empty<IReadOnlyCollection<string>>(), now);
        var coldPlan = ForYouPlanner.Plan(ids, cold, Ctx(3));
        check(coldPlan.Count == 25 && coldPlan.Distinct().Count() == 25, "ColdStart_ReturnsNonEmptySet: no history still gives a full, varied plan");
        check(ForYouPlanner.Plan(new List<string>(), model, Ctx(3)).Count == 0, "No eligible songs: an honest empty plan");
        var shortList = library.Take(10).Select(f => f.Id).ToList();
        var playedThrough = ForYouPlanner.Plan(shortList, model, Ctx(5, recent: Enumerable.Reverse(shortList).ToList()));
        check(playedThrough.Count == 5 && playedThrough.All(id => shortList.IndexOf(id) < 5),
            "A short list played through in one sitting: the songs heard longest ago come back, the latest half stays rested");
        var tiny = new List<TrackFacts> { F("x1", "Solo", "rap"), F("x2", "Solo", "rap"), F("x3", "Solo", "rap") };
        check(ForYouPlanner.Plan(tiny.Select(t => t.Id).ToList(), TasteModel.Build(Array.Empty<PlayRecord>(), tiny, Array.Empty<IReadOnlyCollection<string>>(), now), Ctx(1)).Count == 3,
            "A library of one artist still plays: artist spacing gives way rather than stopping");

        double Unheard(double explore) => Enumerable.Range(0, 20).Average(seed => ForYouPlanner.Plan(ids, model, Ctx(seed, explore: explore)).Count(id => !model.Familiar(id)) / 25.0);
        double familiar = Unheard(0.1), adventurous = Unheard(0.35);
        check(familiar < adventurous && adventurous > 0.2 && familiar < 0.2, $"New-song share follows the setting (familiar {familiar:P0}, adventurous {adventurous:P0})");

        check(ForYouPlanner.Percentiles(new double[] { 1, 1, 1 }).All(v => v == 0) && ForYouPlanner.Percentiles(new double[] { 3, 1, 2 }).SequenceEqual(new[] { 1.0, 0.0, 0.5 }),
            "Ranker_AllScoresFinite: constant parts give zero, ranks are 0..1");
    }
}
