using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using Avalonia.Threading;

namespace TrueShuffle;

/// <summary>
/// Songs the user puts in the queue (Add to Queue, Play Next) are theirs: they go ahead of the prepared shuffle, in the order
/// they were added, and stay there when the shuffle behind them changes. Native methods retain queue bookkeeping.
/// </summary>
public sealed class QueueFrontPolicy : IDisposable
{
    private readonly object _player;
    private readonly IEnumerable _queue;
    private readonly INotifyCollectionChanged _events;
    private readonly MethodInfo _move, _remove;
    private readonly Action<IReadOnlyList<object>> _admit;
    private readonly Action<string> _error;
    private readonly Action? _replaced;
    private readonly Action<IReadOnlyList<object>>? _played;
    private readonly INotifyCollectionChanged? _history;
    private object[] _before;
    private readonly List<object> _pending = new();
    private bool _posted, _editing, _disposed;
    // Noctis plays a song by replacing the queue (see Settle): what was upcoming before that, and what the player was doing.
    private object[]? _replacedFrom;
    private object? _playingBefore;
    private bool _historyPushed, _pushedBefore, _freshRows;
    // The user's own songs that are upcoming. A row taken out during the current call is remembered until the call is over,
    // so that a row moved within the queue keeps what it was.
    private readonly HashSet<object> _manual = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, bool> _lifted = new(ReferenceEqualityComparer.Instance);
    private readonly List<(object Row, object? Playing)> _inserted = new(); // with the song that was playing when it came in
    /// <param name="replaced">Called (later, on the UI thread) when the user cleared the upcoming queue or replaced it with other songs.</param>
    /// <param name="played">Called instead of <paramref name="replaced"/> when the replacement started a song (the user played it from a
    /// list, the queue panel or the history), with the songs that were upcoming until then.</param>
    public QueueFrontPolicy(object player, Action<IReadOnlyList<object>> admit, Action<string> error, Action? replaced = null,
        Action<IReadOnlyList<object>>? played = null)
    {
        _replaced=replaced; _played=played;
        _player=player;
        _queue=QueueBridge.Get(player,"UpNext") as IEnumerable ?? throw new NotSupportedException("Upcoming queue is unavailable.");
        _events=_queue as INotifyCollectionChanged ?? throw new NotSupportedException("Upcoming queue has no change notifications.");
        _move=player.GetType().GetMethod("MoveInQueue",new[]{typeof(int),typeof(int)}) ?? throw new NotSupportedException("Native queue move is unavailable.");
        _remove=player.GetType().GetMethod("RemoveFromQueue",new[]{typeof(int)}) ?? throw new NotSupportedException("Native queue removal is unavailable.");
        _admit=admit; _error=error; _before=Snapshot();
        _events.CollectionChanged+=Changed;
        if (played!=null && QueueBridge.Get(player,"History") is INotifyCollectionChanged history) { _history=history; history.CollectionChanged+=HistoryChanged; }
    }

    /// <summary>
    /// True from the moment Noctis replaces the upcoming queue until this policy has looked at what that was. The song that
    /// starts in between is the one the user picked, not the end of the session.
    /// </summary>
    public bool Replacing=>_replacedFrom!=null;

    /// <summary>Noctis has just moved the playing song to its history to start another list: the queue is replaced next, in the same call.</summary>
    public bool Starting=>_historyPushed;

    /// <summary>The songs among <paramref name="rows"/> that the user put in the queue themselves, in the order given.</summary>
    public List<object> Manual(IEnumerable<object> rows)
    {
        var seen=new HashSet<object>(ReferenceEqualityComparer.Instance);
        return rows.Where(row=>_manual.Contains(row) && seen.Add(row)).ToList();
    }

    private object[] Snapshot()=>_queue.Cast<object>().ToArray();

    private void HistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // To play something new, Noctis first moves the playing song to the top of its history and then replaces the queue.
        // (A song that ends normally goes there too, but the queue then only loses its first row.)
        // Both happen in one call, so the sign is forgotten as soon as that call is over.
        if (e.Action==NotifyCollectionChangedAction.Add && e.NewStartingIndex==0 && e.NewItems is { Count: 1 } items
            && ReferenceEquals(items[0],QueueBridge.Get(_player,"CurrentTrack")))
        {
            _historyPushed=true;
            Dispatcher.UIThread.Post(()=>_historyPushed=false,DispatcherPriority.Send);
        }
    }

    private void Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        bool pushed=_historyPushed; _historyPushed=false; // only the queue change that follows at once belongs to it
        if (_disposed || _editing) return;
        try
        {
        var after=Snapshot();
        if (!_editing)
        {
            bool appended=!pushed && after.Length>_before.Length && _before.Select((track,i)=>ReferenceEquals(track,after[i])).All(same=>same);
            if (appended)
            {
                _pending.AddRange(after.Skip(_before.Length));
                if (!_posted)
                {
                    _posted=true;
                    // Native collections forbid editing inside their notification. Input priority runs
                    // after the Add command completes, before the next ordinary player interaction.
                    Dispatcher.UIThread.Post(Promote,DispatcherPriority.Input);
                }
            }
            else
            {
                var present=new HashSet<object>(after,ReferenceEqualityComparer.Instance);
                _pending.RemoveAll(row=>!present.Contains(row)); // never resurrect a removed row
                bool captured=false;
                // The real player announces a cleared queue, a replaced queue and a bulk addition alike
                // as Reset; an addition keeps the old rows as its prefix (handled above). A Reset that
                // leaves nothing, or brings songs that were not upcoming, is the user starting over.
                if (e.Action==NotifyCollectionChangedAction.Reset && _replacedFrom==null)
                {
                    var known=new HashSet<object>(_before,ReferenceEqualityComparer.Instance);
                    bool fresh=after.Length==0 || after.Any(track=>!known.Contains(track));
                    if (_played!=null && (pushed || fresh))
                    {
                        // Whether this started a song is only known once Noctis is done with it.
                        _replacedFrom=_before; _playingBefore=QueueBridge.Get(_player,"CurrentTrack"); _pushedBefore=pushed; _freshRows=fresh;
                        Dispatcher.UIThread.Post(Settle,DispatcherPriority.Normal);
                        captured=true;
                    }
                    else if (_replaced!=null && _before.Length>0 && fresh)
                        Dispatcher.UIThread.Post(()=>{ if (!_disposed) _replaced(); },DispatcherPriority.Input);
                }
                if (!captured && _replacedFrom==null) Track(after,present);
            }
        }
        _before=after;
        }
        catch(Exception ex)
        {
            Dispose();
            _error("Requested-song priority stopped: "+ex.GetBaseException().Message+". Existing playback and queue were kept.");
        }
    }

    /// <summary>
    /// Which songs are the user's: one that left the queue (it started, or was removed) is no longer theirs, unless it comes
    /// back in the same call (a move). A song that comes in is theirs (Play Next), except the playing song going back in
    /// front (Previous) and songs that come in as another one starts (a jump back through the history); that is told once
    /// the call is over, in <see cref="Promote"/>.
    /// </summary>
    private void Track(object[] after, HashSet<object> present)
    {
        var was=new HashSet<object>(_before,ReferenceEqualityComparer.Instance);
        foreach (var row in was)
            if (!present.Contains(row))
            {
                if (_lifted.Count==0) Dispatcher.UIThread.Post(()=>_lifted.Clear(),DispatcherPriority.Send);
                _lifted[row]=_manual.Remove(row);
            }
        var playing=QueueBridge.Get(_player,"CurrentTrack");
        foreach (var row in after)
        {
            if (was.Contains(row)) continue;
            if (_lifted.Remove(row,out var mine)) { if (mine) _manual.Add(row); continue; }
            if (ReferenceEquals(row,playing)) continue;
            _inserted.Add((row,playing));
            if (!_posted) { _posted=true; Dispatcher.UIThread.Post(Promote,DispatcherPriority.Input); }
        }
    }

    /// <summary>
    /// Noctis plays a picked song with the list it was picked from as the new queue (the queue panel drops the songs above it,
    /// the history plays it alone). When a song started that way, <c>played</c> decides what follows it; a queue that was only
    /// cleared or filled with other songs, with the same song playing on, is the user starting over.
    /// </summary>
    private void Settle()
    {
        var from=_replacedFrom; _replacedFrom=null;
        if (_disposed || from==null) return;
        try
        {
            var playing=QueueBridge.Get(_player,"CurrentTrack");
            if (playing!=null && (_pushedBefore || !ReferenceEquals(playing,_playingBefore))) _played!(from);
            else if (_replaced!=null && from.Length>0 && _freshRows) _replaced();
        }
        catch(Exception ex) { _error("Could not keep the shuffle behind the song you played: "+ex.GetBaseException().Message); }
        finally
        {
            if (!_disposed && !_editing)
            {
                _before=Snapshot();
                _manual.IntersectWith(_before); // a song of the user's that is not back in the queue is the one that started
            }
        }
    }

    private static string? Recording(object track)
    {
        var id=QueueBridge.Get(track,"Id")?.ToString();
        if (string.IsNullOrWhiteSpace(id)) return null;
        var path=QueueBridge.Get(track,"FilePath") as string ?? "";
        if (OperatingSystem.IsWindows()) path=path.ToUpperInvariant();
        return id+"\0"+path;
    }
    internal static bool Same(object a, object b)=>ReferenceEquals(a,b) || (Recording(a) is string key && key==Recording(b));
    private void Promote()
    {
        _posted=false;
        if (_disposed) return;
        var requested=_pending.ToArray();_pending.Clear();
        var inserted=_inserted.ToArray();_inserted.Clear();
        if (requested.Length==0 && inserted.Length==0) return;
        _editing=true;
        try
        {
            // A song put in with Play Next stays where Noctis put it; only another copy further down goes.
            var promoted=new List<object>();
            var playingNow=QueueBridge.Get(_player,"CurrentTrack");
            foreach (var (row,playing) in inserted)
            {
                if (!ReferenceEquals(playing,playingNow)) continue; // came in as another song started: Noctis moving through its history
                var snapshot=Snapshot();
                int first=Array.FindIndex(snapshot,track=>ReferenceEquals(track,row));
                if (first<0 || promoted.Contains(row)) continue;
                for (int i=snapshot.Length-1;i>first;i--) if (Same(snapshot[i],row)) _remove.Invoke(_player,new object[]{i});
                _manual.Add(row);
                promoted.Add(row);
            }
            foreach (var request in requested)
            {
                if (promoted.Any(track=>Same(track,request))) continue;
                var snapshot=Snapshot();
                var matches=snapshot.Select((track,i)=>(track,i)).Where(row=>Same(row.track,request)).ToArray();
                if (matches.Length==0) continue;
                // Retain the newly requested row, remove its older upcoming duplicates only.
                var kept=matches[^1].track;
                foreach (var row in matches.Take(matches.Length-1).Reverse()) _remove.Invoke(_player,new object[]{row.i});
                var now=Snapshot();
                int from=Array.FindLastIndex(now,track=>ReferenceEquals(track,kept));
                if (from<0) continue;
                // Behind the user's songs that already wait at the front, ahead of the shuffle.
                int to=0;
                while (to<now.Length && to!=from && _manual.Contains(now[to])) to++;
                if (from!=to) _move.Invoke(_player,new object[]{from,to});
                _manual.Add(kept);
                promoted.Add(kept);
            }
            if (promoted.Count>0) _admit(promoted);
        }
        catch(Exception ex) { _error("Could not put requested songs next: "+ex.GetBaseException().Message+". Check the queue; playback was not restarted."); }
        finally { _editing=false;_before=Snapshot(); }
    }
    /// <summary>Runs a queue change of the plugin's own (a rolling queue adding more songs) that must not be taken for a request to play something next.</summary>
    public void Own(Action change)
    {
        var was=_editing; _editing=true;
        try { change(); }
        finally { _editing=was; _before=Snapshot(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed=true; _pending.Clear();_inserted.Clear();_manual.Clear();_replacedFrom=null;_events.CollectionChanged-=Changed;
        if (_history!=null) _history.CollectionChanged-=HistoryChanged;
    }
}
