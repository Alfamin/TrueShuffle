using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;

namespace TrueShuffle;

public sealed record TrackEntry(object Native, Song Song);

/// <summary>
/// Noctis kit 1.2 has no queue API. This narrow, validated adapter uses the desktop player's
/// existing queue methods with its own Track objects; it never writes files or patches methods.
/// If the contract changes, fail before a queue mutation instead of guessing.
/// </summary>
public sealed class QueueBridge
{
    public object Player { get; }
    private readonly object _main;
    private readonly object _library;
    private readonly Type _trackType;
    private readonly MethodInfo _replace, _clear, _add;

    public QueueBridge(object main, bool enforceHostTypes = true)
    {
        if (enforceHostTypes && main.GetType().FullName != "Noctis.ViewModels.MainWindowViewModel")
            throw new NotSupportedException("This Noctis window does not expose a compatible queue.");
        _main = main;
        Player = Get(main, "Player") ?? throw new NotSupportedException("Noctis player is unavailable.");
        _library = main.GetType().GetField("_library", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new NotSupportedException("Noctis library access has changed.");
        var methods = Player.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
        _replace = methods.SingleOrDefault(m => m.Name == "ReplaceQueueAndPlay" && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType == typeof(int)) ?? throw Changed();
        var parameter = _replace.GetParameters()[0].ParameterType;
        if (!parameter.IsGenericType || parameter.GetGenericTypeDefinition() != typeof(IList<>)) throw Changed();
        _trackType = parameter.GetGenericArguments()[0];
        if (enforceHostTypes && _trackType.FullName != "Noctis.Models.Track") throw Changed();
        _clear = Player.GetType().GetMethod("ClearQueue", Type.EmptyTypes) ?? throw Changed();
        // 1.6 added optional sourceName/announce arguments. Reflection must supply them;
        // GetMethod with the old one-argument signature no longer finds the method.
        _add = FindAppend(Player.GetType(), parameter) ?? throw Changed();
        foreach (string p in new[] { "AutoplayEnabled", "RepeatMode", "IsRadioActive", "IsShuffleEnabled" })
            if (Property(Player, p)?.CanWrite != true || Property(Player, p)?.CanRead != true) throw Changed();
        foreach (string p in new[] { "AutoplayEnabled", "IsRadioActive", "IsShuffleEnabled" })
            if (Property(Player, p)?.PropertyType != typeof(bool)) throw Changed();
        var repeatType = Property(Player, "RepeatMode")!.PropertyType;
        if (!repeatType.IsEnum || !Enum.IsDefined(repeatType, "Off")) throw Changed();
        if (Property(Player, "CurrentTrack")?.PropertyType != _trackType ||
            Get(_main, "Settings") is not object settings || Property(settings, "AllowExplicitContent")?.PropertyType != typeof(bool)) throw Changed();
        if (Get(Player, "ToggleShuffleCommand") is not ICommand || Get(Player, "UpNext") is not IEnumerable) throw Changed();
        foreach (string p in new[] { "Id", "FilePath", "Title", "Artist", "PlayCount", "SkipWhenShuffling", "IsExplicit", "IsDisliked", "SnoozedUntil", "IsRemoteStream" })
            if (_trackType.GetProperty(p) == null) throw Changed();
        if (Get(_library, "Tracks") is not IEnumerable) throw Changed();
    }

    public TrackEntry? Current => Get(Player, "CurrentTrack") is object t ? Wrap(t) : null;
    public bool ExplicitAllowed => Get(_main, "Settings") is object s ? (bool)(Get(s, "AllowExplicitContent") ?? throw Changed()) : throw Changed();

    public List<TrackEntry> Snapshot(string scope)
    {
        IEnumerable collection;
        if (scope == "Current queue")
        {
            var list = ((IEnumerable)Get(Player, "UpNext")!).Cast<object>().ToList();
            if (Get(Player, "CurrentTrack") is object current) list.Insert(0, current);
            collection = list;
        }
        else if (scope == "Open playlist")
        {
            var view = Get(_main, "CurrentView");
            if (view == null || view.GetType().FullName != "Noctis.ViewModels.PlaylistViewModel")
                throw new InvalidOperationException("Open a playlist in Noctis first, or choose Current queue / Whole library.");
            collection = Get(view, "Tracks") as IEnumerable ?? throw Changed();
        }
        else if (scope == "Whole library") collection = (IEnumerable)Get(_library, "Tracks")!;
        else throw new ArgumentException("Unknown scope");
        return collection.Cast<object>().Select(Wrap).ToList();
    }

    public void Apply(IReadOnlyList<TrackEntry> ordered, bool keepCurrent)
    {
        if (ordered.Count == 0) return;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_trackType))!;
        foreach (var entry in ordered)
        {
            if (!_trackType.IsInstanceOfType(entry.Native)) throw Changed();
            list.Add(entry.Native);
        }
        var original = ((IEnumerable)Get(Player, "UpNext")!).Cast<object>().ToList();
        var previousCurrent = Get(Player, "CurrentTrack");
        // Never call Noctis's weighted shuffle here. Disable its existing mode via its
        // command, which also clears/restores its own order bookkeeping safely.
        if ((bool)Get(Player, "IsShuffleEnabled")!) ((ICommand)Get(Player, "ToggleShuffleCommand")!).Execute(null);
        Set(Player, "IsRadioActive", false);
        if (keepCurrent && Current != null)
        {
            try
            {
                _clear.Invoke(Player, null);
                Append(list);
            }
            catch (Exception failed)
            {
                // A failed append must not silently erase the user's upcoming songs.
                // Restore using native methods, without restarting the playing track.
                if (!ReferenceEquals(Get(Player, "CurrentTrack"), previousCurrent)) throw;
                var restore = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_trackType))!;
                foreach (var track in original) restore.Add(track);
                try { _clear.Invoke(Player, null); Append(restore); }
                catch (Exception recovery) { throw new AggregateException("Queue application and recovery failed. No music files were changed.", failed, recovery); }
                throw new InvalidOperationException("Shuffle could not be applied. Previous upcoming songs were restored; built-in Shuffle is off.", failed);
            }
        }
        else _replace.Invoke(Player, new object[] { list, 0 });
    }

    public static MethodInfo? FindAppend(Type player, Type listType) => player.GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .SingleOrDefault(m => m.Name == "AddRangeToQueue" && m.ReturnType==typeof(void) && (
            m.GetParameters() is var p && p.Length >= 1 && p[0].ParameterType == listType &&
            (p.Length == 1 || (p.Length == 3 && p[1].ParameterType == typeof(string) && p[1].IsOptional &&
                p[2].ParameterType == typeof(bool) && p[2].IsOptional))));

    private void Append(IList tracks) => _add.Invoke(Player, _add.GetParameters().Length == 1
        ? new object?[] { tracks } : new object?[] { tracks, null, false });

    private TrackEntry Wrap(object t)
    {
        if (!_trackType.IsInstanceOfType(t)) throw Changed();
        return new(t, new Song(Get(t, "Id")!.ToString()!, (string)Get(t, "FilePath")!,
            (string)Get(t, "Title")!, (string)Get(t, "Artist")!, (int)Get(t, "PlayCount")!,
            (bool)Get(t, "SkipWhenShuffling")!, (bool)Get(t, "IsExplicit")!, (bool)Get(t, "IsDisliked")!,
            (DateTime?)Get(t, "SnoozedUntil"), (bool)Get(t, "IsRemoteStream")!));
    }
    internal static PropertyInfo? Property(object o, string p) => o.GetType().GetProperty(p, BindingFlags.Instance | BindingFlags.Public);
    internal static object? Get(object o, string p) => Property(o, p)?.GetValue(o);
    internal static void Set(object o, string p, object value) => (Property(o, p) ?? throw Changed()).SetValue(o, value);
    private static NotSupportedException Changed() => new("This Noctis version changed its queue contract. Nothing was forced.");
}

/// <summary>Temporary session overrides; preserve a setting the user changes during the session.</summary>
public sealed class PlaybackLease : IDisposable
{
    private readonly object _player;
    private readonly Dictionary<string, (object Previous, object Applied)> _values = new();
    private readonly HashSet<string> _edited = new();
    private bool _setting, _disposed;
    public PlaybackLease(object player)
    {
        _player = player;
        var repeat = QueueBridge.Get(player, "RepeatMode") ?? throw new NotSupportedException("Repeat mode unavailable");
        if (!repeat.GetType().IsEnum || !Enum.IsDefined(repeat.GetType(), "Off")) throw new NotSupportedException("Repeat mode changed");
        _values.Add("RepeatMode", (repeat, Enum.Parse(repeat.GetType(), "Off")));
        _values.Add("AutoplayEnabled", (QueueBridge.Get(player, "AutoplayEnabled")!, false));
        // Starting a custom queue ends Track Radio. ReplaceQueueAndPlay clears its
        // private seed, so restoring only the active flag would create a broken radio.
        if (player is INotifyPropertyChanged events) events.PropertyChanged += Changed;
        _setting = true;
        try { foreach (var pair in _values) QueueBridge.Set(player, pair.Key, pair.Value.Applied); }
        catch { _setting = false; Dispose(); throw; }
        finally { _setting = false; }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!_setting && e.PropertyName != null && _values.ContainsKey(e.PropertyName)) _edited.Add(e.PropertyName);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_player is INotifyPropertyChanged events) events.PropertyChanged -= Changed;
        foreach (var pair in _values)
            if (!_edited.Contains(pair.Key) && Equals(QueueBridge.Get(_player, pair.Key), pair.Value.Applied))
                QueueBridge.Set(_player, pair.Key, pair.Value.Previous);
    }
}
