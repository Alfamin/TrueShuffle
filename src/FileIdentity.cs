using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrueShuffle;

/// <summary>Read-only Windows file identity; unchanged by a same-volume move or rename.</summary>
internal static class FileIdentity
{
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0) return null;
            ulong index = ((ulong)info.IndexHigh << 32) | info.IndexLow;
            if (index == 0) return null;
            return $"winfile:{info.Volume:X8}:{index:X16}:{info.CreatedHigh:X8}{info.CreatedLow:X8}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
}
