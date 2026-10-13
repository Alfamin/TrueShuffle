using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrueShuffle;

public sealed record CycleData(int Version, string[] Seen, string? Last, string? LastPath = null);

/// <summary>Only this plugin's private file is written. No Noctis settings/history/database edits.</summary>
public sealed class CycleStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaxKeys = 50000;
    public CycleStore(string folder) => _path = Path.Combine(folder, "cycle.json");

    public async Task<CycleData> LoadAsync()
    {
        if (!File.Exists(_path)) return new(1, Array.Empty<string>(), null);
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A linked shuffle history file was left untouched.");
        if (new FileInfo(_path).Length > 4_000_000) throw new IOException("Shuffle history is too large; it was left untouched.");
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > 4_000_000) throw new IOException("Shuffle history exceeded its size limit.");
            buffer.Write(chunk, 0, count);
        }
        var data = JsonSerializer.Deserialize<CycleData>(buffer.ToArray())
            ?? throw new IOException("Shuffle history is empty or invalid.");
        if (data.Version != 1 || data.Seen == null || data.Seen.Length > MaxKeys ||
            data.Seen.Any(k => !ValidKey(k)) || (data.Last != null && !ValidKey(data.Last)) || (data.LastPath != null && !ValidKey(data.LastPath)))
            throw new IOException("Shuffle history has an unsupported format; it was left untouched.");
        return data with { Seen = data.Seen.Distinct(StringComparer.Ordinal).ToArray() };
    }

    public async Task SaveAsync(CycleData data)
    {
        if (data.Version != 1 || data.Seen.Length > MaxKeys || data.Seen.Any(k => !ValidKey(k)) ||
            (data.Last != null && !ValidKey(data.Last)) || (data.LastPath != null && !ValidKey(data.LastPath))) throw new IOException("Shuffle history is invalid or exceeds its limit.");
        await _gate.WaitAsync();
        string? temp = null;
        try
        {
            var folder = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(folder);
            if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A linked shuffle history file was left untouched.");
            temp = Path.Combine(folder, ".cycle-" + Guid.NewGuid().ToString("N") + ".tmp");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data);
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await f.WriteAsync(bytes);
                f.Flush(true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (temp != null && File.Exists(temp)) File.Delete(temp); // this call's exclusive temp only
            _gate.Release();
        }
    }

    private static bool ValidKey(string k) => k != null && k.Length == 64 && k.All(c => char.IsAsciiHexDigit(c));
}
