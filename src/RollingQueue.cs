using System;
using System.Collections.Generic;

namespace TrueShuffle;

/// <summary>
/// The plan a custom session follows. Only a window of it sits in Noctis's own queue (a queue the
/// size of a library is slow for Noctis to show and to edit); the rest waits here, in order, until
/// the queue runs low. When the plan is used up the next one is made the same way.
/// </summary>
internal sealed class RollingQueue
{
    public RollingQueue(ShuffleMode mode, string scope, bool skipDisliked, IReadOnlyList<RawTrack>? members,
        List<TrackEntry> plan, int taken, Dictionary<string, string> identities)
    {
        Mode = mode; Scope = scope; SkipDisliked = skipDisliked; Members = members;
        Plan = plan; Next = taken; Identities = identities;
    }
    public ShuffleMode Mode { get; }
    public string Scope { get; }
    public bool SkipDisliked { get; }
    /// <summary>For a source that is not the library: the songs it had when the shuffle was applied (a later plan is made from them).</summary>
    public IReadOnlyList<RawTrack>? Members { get; }
    public List<TrackEntry> Plan { get; set; }
    /// <summary>The first song of <see cref="Plan"/> that is not in the native queue yet.</summary>
    public int Next { get; set; }
    public int Remaining => Plan.Count - Next;
    /// <summary>File identities already worked out, so that a later plan opens no file twice.</summary>
    public Dictionary<string, string> Identities { get; }
    public bool Busy { get; set; }
    public bool Finished { get; set; }
    /// <summary>When everything eligible is queued or playing already, there is nothing to plan yet: ask again later.</summary>
    public DateTime NotBefore { get; set; } = DateTime.MinValue;
}
