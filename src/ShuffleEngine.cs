using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TrueShuffle;

public enum ShuffleMode { EqualChance, NoRepeats, Discovery, NeverPlayed }
public sealed record Song(string Id, string Path, string Title, string Artist, int PlayCount,
    bool Skip, bool Explicit, bool Disliked, DateTime? SnoozedUntil, bool Remote = false, string? IdentityKey = null)
{
    public string Key => IdentityKey ?? StableKey(Id, Path, Remote);
    public string LegacyKey => PathKey(Id, Path, Remote);
    public static string StableKey(string id, string path, bool remote = false)
        => !remote && !string.IsNullOrWhiteSpace(path) && FileIdentity.Read(path) is string identity
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) : PathKey(id, path, remote);

    private static string PathKey(string id, string path, bool remote)
    {
        string value = "id:" + id;
        if (!remote && !string.IsNullOrWhiteSpace(path))
        {
            value = System.IO.Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) value = value.ToUpperInvariant();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed record ShufflePlan(IReadOnlyList<Song> Songs, IReadOnlyCollection<string> Seen,
    bool NewCycle, int Eligible, int Unplayed);

public static class ShuffleEngine
{
    /// <summary>Retain legacy aliases so atomic tag replacement at the same path keeps progress too.</summary>
    public static CycleData UpgradeHistory(IEnumerable<Song> songs, IEnumerable<string> remembered, string? last, string? lastPath = null)
    {
        var seen = new HashSet<string>(remembered, StringComparer.Ordinal);
        foreach (var song in songs)
        {
            if (seen.Contains(song.LegacyKey)) seen.Add(song.Key);
            if (last == song.LegacyKey || (last != null && lastPath == song.LegacyKey)) last = song.Key;
        }
        return new(1, seen.ToArray(), last, lastPath);
    }
    public static bool IsEligible(Song s, bool explicitAllowed, bool skipDisliked, DateTime now)
        => !s.Skip && (explicitAllowed || !s.Explicit) && (!skipDisliked || !s.Disliked)
           && (s.SnoozedUntil == null || s.SnoozedUntil <= now);

    public static List<Song> Eligible(IEnumerable<Song> songs, bool explicitAllowed, bool skipDisliked, DateTime now)
        => songs.Where(s => IsEligible(s, explicitAllowed, skipDisliked, now)).DistinctBy(s => s.Key).ToList();

    /// <summary>
    /// The rules of a mode, asked again when a rolling queue takes a song from its plan: since the plan
    /// was made the song may have been heard, skipped, snoozed or played for the first time.
    /// </summary>
    public static bool StillWanted(Song song, ShuffleMode mode, ISet<string> seen, bool explicitAllowed, bool skipDisliked, DateTime now)
        => IsEligible(song, explicitAllowed, skipDisliked, now)
           && (mode != ShuffleMode.NeverPlayed || song.PlayCount == 0)
           && (mode == ShuffleMode.EqualChance || !seen.Contains(song.Key));

    /// <param name="upcoming">Keys of songs that are already waiting in the queue: a later plan must not repeat them.</param>
    public static ShufflePlan Build(IEnumerable<Song> input, ShuffleMode mode, IEnumerable<string> remembered,
        string? current, string? last, bool explicitAllowed = true, bool skipDisliked = false,
        DateTime? now = null, Func<int, int>? draw = null, ISet<string>? upcoming = null)
    {
        draw ??= RandomNumberGenerator.GetInt32;
        var pool = Eligible(input, explicitAllowed, skipDisliked, now ?? DateTime.UtcNow);
        if (upcoming != null) pool = pool.Where(s => !upcoming.Contains(s.Key)).ToList();
        var seen = new HashSet<string>(remembered, StringComparer.Ordinal);
        if (current != null) seen.Add(current);
        var available = pool.Where(s => s.Key != current).ToList();
        int unplayed = available.Count(s => s.PlayCount == 0);
        bool reset = false;
        List<Song> result;
        if (mode == ShuffleMode.EqualChance) result = available;
        else
        {
            if (mode == ShuffleMode.NeverPlayed) available = available.Where(s => s.PlayCount == 0).ToList();
            result = available.Where(s => !seen.Contains(s.Key)).ToList();
            if (result.Count == 0 && available.Count > 0 && mode != ShuffleMode.NeverPlayed)
            {
                foreach (var s in pool) { seen.Remove(s.Key); seen.Remove(s.LegacyKey); }
                if (current != null) seen.Add(current);
                result = available;
                reset = true;
            }
        }
        if (mode == ShuffleMode.Discovery)
        {
            result = result.GroupBy(s => Math.Max(0, s.PlayCount)).OrderBy(g => g.Key)
                .SelectMany(g => Permute(g.ToList(), draw)).ToList();
        }
        else Permute(result, draw);
        // At a new cycle boundary don't start with the last song again when there is an alternative.
        // EqualChance stays a strictly uniform permutation and deliberately has no recency rule.
        if (mode != ShuffleMode.EqualChance && result.Count > 1 && result[0].Key == last)
        {
            // Discovery's count order takes priority: never pull a familiar song ahead
            // of an unplayed one merely to avoid a boundary repeat.
            int alternatives = mode == ShuffleMode.Discovery
                ? result.TakeWhile(s => Math.Max(0, s.PlayCount) == Math.Max(0, result[0].PlayCount)).Count() - 1
                : result.Count - 1;
            if (alternatives > 0)
            {
                int alternative = 1 + draw(alternatives);
                (result[0], result[alternative]) = (result[alternative], result[0]);
            }
        }
        return new(result, seen, reset, pool.Count, unplayed);
    }

    private static List<T> Permute<T>(List<T> list, Func<int, int> draw)
    {
        for (int i = list.Count - 1; i > 0; --i)
        {
            int j = draw(i + 1);
            if (j < 0 || j > i) throw new InvalidOperationException("Invalid random draw");
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
