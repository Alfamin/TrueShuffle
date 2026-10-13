using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using Avalonia.Threading;

namespace TrueShuffle;

/// <summary>Manual appends take priority over the prepared shuffle; native methods retain queue bookkeeping.</summary>
public sealed class QueueFrontPolicy : IDisposable
{
    private readonly object _player;
    private readonly IEnumerable _queue;
    private readonly INotifyCollectionChanged _events;
    private readonly MethodInfo _move, _remove;
    private readonly Action<IReadOnlyList<object>> _admit;
    private readonly Action<string> _error;
    private object[] _before;
    private readonly List<object> _pending = new();
    private bool _posted, _editing, _disposed;
    public QueueFrontPolicy(object player, Action<IReadOnlyList<object>> admit, Action<string> error)
    {
        _player=player;
        _queue=QueueBridge.Get(player,"UpNext") as IEnumerable ?? throw new NotSupportedException("Upcoming queue is unavailable.");
        _events=_queue as INotifyCollectionChanged ?? throw new NotSupportedException("Upcoming queue has no change notifications.");
        _move=player.GetType().GetMethod("MoveInQueue",new[]{typeof(int),typeof(int)}) ?? throw new NotSupportedException("Native queue move is unavailable.");
        _remove=player.GetType().GetMethod("RemoveFromQueue",new[]{typeof(int)}) ?? throw new NotSupportedException("Native queue removal is unavailable.");
        _admit=admit; _error=error; _before=Snapshot();
        _events.CollectionChanged+=Changed;
    }
    private object[] Snapshot()=>_queue.Cast<object>().ToArray();
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || _editing) return;
        try
        {
        var after=Snapshot();
        if (!_editing)
        {
            bool appended=after.Length>_before.Length && _before.Select((track,i)=>ReferenceEquals(track,after[i])).All(same=>same);
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
            else _pending.Clear(); // later user edits/removals win; never resurrect a removed row
        }
        _before=after;
        }
        catch(Exception ex)
        {
            Dispose();
            _error("Requested-song priority stopped: "+ex.GetBaseException().Message+". Existing playback and queue were kept.");
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
    private static bool Same(object a, object b)=>ReferenceEquals(a,b) || (Recording(a) is string key && key==Recording(b));
    private void Promote()
    {
        _posted=false;
        if (_disposed) return;
        var requested=_pending.ToArray();_pending.Clear();
        if (requested.Length==0) return;
        _editing=true;
        try
        {
            var promoted=new List<object>();
            foreach (var request in requested)
            {
                if (promoted.Any(track=>Same(track,request))) continue;
                var snapshot=Snapshot();
                var matches=snapshot.Select((track,i)=>(track,i)).Where(row=>Same(row.track,request)).ToArray();
                if (matches.Length==0) continue;
                // Retain the newly requested row, remove its older upcoming duplicates only.
                var kept=matches[^1].track;
                foreach (var row in matches.Take(matches.Length-1).Reverse()) _remove.Invoke(_player,new object[]{row.i});
                int from=Array.FindLastIndex(Snapshot(),track=>ReferenceEquals(track,kept));
                if (from<0) continue;
                int to=promoted.Count;
                if (from!=to) _move.Invoke(_player,new object[]{from,to});
                promoted.Add(kept);
            }
            if (promoted.Count>0) _admit(promoted);
        }
        catch(Exception ex) { _error("Could not put requested songs next: "+ex.GetBaseException().Message+". Check the queue; playback was not restarted."); }
        finally { _editing=false;_before=Snapshot(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed=true; _pending.Clear();_events.CollectionChanged-=Changed;
    }
}
