using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TrueShuffle;

/// <summary>How a play ended, as far as the player shows it.</summary>
public enum PlayEnd
{
    /// <summary>The song reached its end (or the crossfade into the next song began in its last seconds).</summary>
    Finished,
    /// <summary>Next was pressed: the following song of the queue started before this one ended.</summary>
    Skipped,
    /// <summary>Previous was pressed: going back is navigation, not an opinion of the song.</summary>
    Previous,
    /// <summary>Another song was picked from a list, the queue panel or the history.</summary>
    Replaced,
    /// <summary>Playback stopped, or Noctis closed: unknown how it would have gone.</summary>
    Stopped,
    Unknown,
}

/// <summary>What a play says about the song. Only a deliberate early Next is a skip; unknown stays neutral.</summary>
public enum PlayOutcome { Completed, Qualified, Partial, StrongSkip, QuickSkip, Neutral }

/// <summary>One play of a song: when, how much of it was heard, and how it ended. One line of listens.jsonl.</summary>
public sealed record PlayRecord(string TrackId, string PathKey, string Title, string Artist, DateTime StartUtc, DateTime EndUtc,
    int DurationMs, int ListenedMs, double Coverage, PlayEnd End, string Origin, bool Imported = false)
{
    /// <summary>Shorter than this and a Next counts as a slip of the finger, not as a verdict on the song.</summary>
    public const int AudibleMs = 3_000;

    [JsonIgnore]
    public PlayOutcome Outcome
    {
        get
        {
            if (End == PlayEnd.Finished) return PlayOutcome.Completed;
            // The scrobble rule: half the song, or four minutes of it.
            if (Coverage >= 0.5 || ListenedMs >= 240_000) return PlayOutcome.Qualified;
            if (End == PlayEnd.Skipped)
                return ListenedMs < AudibleMs ? PlayOutcome.QuickSkip : Coverage < 0.25 ? PlayOutcome.StrongSkip : PlayOutcome.Partial;
            return ListenedMs >= 30_000 ? PlayOutcome.Partial : PlayOutcome.Neutral;
        }
    }
}

/// <summary>
/// Follows the playing song from samples of the player (position, playing or not) and turns it into a
/// <see cref="PlayRecord"/>. Listening time grows only while playing and only by what the position really
/// advanced: a pause adds nothing, a jump forward adds nothing, listening again after jumping back adds
/// time but not coverage. The clock is monotonic and injected, so it is all testable.
/// </summary>
public sealed class PlayTracker
{
    private sealed class Active
    {
        public required string TrackId, PathKey, Title, Artist, Origin;
        public required DateTime StartUtc;
        public required int DurationMs;
        public long ListenedMs;
        public BitArray? Covered;
        public TimeSpan LastPosition, LastClock;
        public bool LastPlaying;
    }

    private Active? _active;
    // The longest listening a single sample may add: longer gaps (sleep, a stalled clock) count as not heard.
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(1.5);

    public bool Running => _active != null;
    public string? TrackId => _active?.TrackId;

    public void Start(string trackId, string pathKey, string title, string artist, TimeSpan duration, string origin,
        DateTime utcNow, TimeSpan clock, TimeSpan position, bool playing)
    {
        int durationMs = (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue);
        _active = new Active
        {
            TrackId = trackId, PathKey = pathKey, Title = title, Artist = artist, Origin = origin,
            StartUtc = utcNow, DurationMs = durationMs,
            // One bit per second of the song, up to four hours.
            Covered = durationMs > 0 ? new BitArray(Math.Min(durationMs / 1000 + 1, 4 * 3600)) : null,
            LastPosition = position, LastClock = clock, LastPlaying = playing,
        };
    }

    /// <summary>A look at the player; call it about once a second and at every change of state.</summary>
    public void Sample(TimeSpan position, bool playing, TimeSpan clock)
    {
        var a = _active;
        if (a == null) return;
        if (a.LastPlaying && playing)
        {
            var moved = position - a.LastPosition;
            var elapsed = clock - a.LastClock;
            // Real listening moves the position about as fast as the clock; anything else was a jump.
            if (moved > TimeSpan.Zero && elapsed > TimeSpan.Zero && elapsed <= MaxStep && moved <= elapsed + Slack)
            {
                a.ListenedMs += (long)moved.TotalMilliseconds;
                if (a.Covered != null)
                {
                    int from = (int)Math.Max(0, a.LastPosition.TotalSeconds), to = (int)Math.Min(a.Covered.Length, Math.Ceiling(position.TotalSeconds));
                    for (int s = from; s < to; s++) a.Covered[s] = true;
                }
            }
        }
        a.LastPosition = position; a.LastClock = clock; a.LastPlaying = playing;
    }

    /// <summary>The play is over; <paramref name="finalPosition"/> is where the song was when it ended.</summary>
    public PlayRecord? End(PlayEnd end, TimeSpan finalPosition, DateTime utcNow, TimeSpan clock)
    {
        var a = _active;
        if (a == null) return null;
        Sample(finalPosition, a.LastPlaying, clock);
        _active = null;
        double coverage = a.Covered == null ? 0 : (double)a.Covered.CountSet() / a.Covered.Length;
        return new PlayRecord(a.TrackId, a.PathKey, a.Title, a.Artist, a.StartUtc, utcNow, a.DurationMs,
            (int)Math.Min(a.ListenedMs, int.MaxValue), Math.Round(Math.Clamp(coverage, 0, 1), 3), end, a.Origin);
    }

    /// <summary>Where the song stands now: its length, how far it got, how much of it has been heard.</summary>
    public (int DurationMs, TimeSpan Position, double Coverage)? Peek()
    {
        var a = _active;
        if (a == null) return null;
        return (a.DurationMs, a.LastPosition, a.Covered == null ? 0 : (double)a.Covered.CountSet() / a.Covered.Length);
    }

    /// <summary>
    /// How the song ended, from what the player shows at the moment the next one starts. <paramref name="remaining"/> is
    /// what was left of the old song; with crossfade the next one starts up to 12 s early.
    /// </summary>
    public static PlayEnd Classify(bool replaced, bool wentBack, bool advanced, TimeSpan remaining, double coverage)
    {
        if (replaced) return PlayEnd.Replaced;
        if (wentBack) return PlayEnd.Previous;
        if (remaining <= TimeSpan.FromSeconds(3) || (remaining <= TimeSpan.FromSeconds(13) && coverage >= 0.8)) return PlayEnd.Finished;
        return advanced ? PlayEnd.Skipped : PlayEnd.Unknown;
    }

    /// <summary>A tiny bit set: <see cref="System.Collections.BitArray"/> has no count.</summary>
    private sealed class BitArray
    {
        private readonly ulong[] _bits;
        public BitArray(int length) { Length = Math.Max(1, length); _bits = new ulong[(Length + 63) / 64]; }
        public int Length { get; }
        public bool this[int i] { set { if (value) _bits[i >> 6] |= 1UL << (i & 63); } }
        public int CountSet() => _bits.Sum(b => System.Numerics.BitOperations.PopCount(b));
    }
}

/// <summary>
/// listens.jsonl in the plugin's data folder: one play per line, appended in order by a single writer. A damaged
/// line is skipped, never the whole file. Nothing leaves the computer.
/// </summary>
public sealed class ListenStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private const long MaxReadBytes = 64L * 1024 * 1024; // years of listening; beyond it only the newest part is read
    private readonly string _path;
    private Task _write = Task.CompletedTask;

    public ListenStore(string folder) => _path = Path.Combine(folder, "listens.jsonl");
    public string FilePath => _path;

    public List<PlayRecord> Load()
    {
        var result = new List<PlayRecord>();
        if (!File.Exists(_path) || (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0) return result;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxReadBytes) stream.Seek(-MaxReadBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (stream.Position > 0) reader.ReadLine(); // the first line read is cut in half
        for (string? line; (line = reader.ReadLine()) != null;)
        {
            if (line.Length == 0) continue;
            try { if (JsonSerializer.Deserialize<PlayRecord>(line, Json) is { TrackId.Length: > 0 } record) result.Add(record); }
            catch (JsonException) { } // a line cut by a crash
        }
        return result;
    }

    /// <summary>Appends in the order called, off the calling thread. <paramref name="failed"/> hears of a write that did not happen.</summary>
    public Task Append(PlayRecord record, Action<Exception>? failed = null)
    {
        var line = JsonSerializer.Serialize(record, Json) + "\n";
        _write = _write.ContinueWith(_ =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("listens.jsonl is a link; it is not written to.");
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) { failed?.Invoke(ex); }
        }, TaskScheduler.Default);
        return _write;
    }

    /// <summary>The writes queued so far.</summary>
    public Task Flushed => _write;
}

/// <summary>
/// Noctis's own play_history.json, read once and never written. It tells when a song was played and whether Noctis
/// marked it skipped, not how much of it was heard, so it counts for less than our own records, and only for the time
/// before they began.
/// </summary>
public static class NoctisHistory
{
    private sealed record Event(Guid TrackId, string? Title, string? Artist, DateTime PlayedAtUtc, bool Skipped);

    public static List<PlayRecord> Read(string path, DateTime before)
    {
        var result = new List<PlayRecord>();
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return result;
        var info = new FileInfo(path);
        if (info.Length > 256L * 1024 * 1024) return result;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var events = JsonSerializer.Deserialize<List<Event>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        foreach (var e in events)
        {
            var at = DateTime.SpecifyKind(e.PlayedAtUtc, DateTimeKind.Utc);
            if (e.TrackId == Guid.Empty || at >= before) continue;
            result.Add(new PlayRecord(e.TrackId.ToString(), "", e.Title ?? "", e.Artist ?? "", at, at, 0, 0, 0,
                e.Skipped ? PlayEnd.Skipped : PlayEnd.Unknown, "noctis-history", Imported: true));
        }
        return result;
    }
}
