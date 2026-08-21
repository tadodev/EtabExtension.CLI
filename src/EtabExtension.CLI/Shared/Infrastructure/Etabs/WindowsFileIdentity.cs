// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// Answers "are these two paths the same file on disk?" by identity rather than by
/// spelling.
///
/// <para>Needed because a model-open confirmation cannot trust either extreme. Path
/// equality alone rejects legitimate re-spellings — UNC shares, <c>subst</c> drives,
/// mapped network drives, junctions — that name the file that was actually opened.
/// File-name equality alone accepts a genuinely different model: this machine holds
/// a byte-identical <c>sample_v2.EDB</c> at both <c>D:\Work\test\</c> and the
/// sanctioned <c>D:\Work\tadoEng\TestModel\</c>, so "same name" proves nothing.</para>
///
/// <para>Windows identifies a file by volume serial number plus file index, which is
/// stable across every path that reaches it. That is what this compares.</para>
/// </summary>
internal static class WindowsFileIdentity
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x00000007; // READ | WRITE | DELETE
    private const uint OpenExisting = 3;

    /// <summary>
    /// True only when both paths resolve to the same file. Any path that cannot be
    /// identified — missing, locked against even an attributes-only open, a folder,
    /// or a non-Windows host — answers false, so an unprovable match never passes as
    /// a proven one.
    /// </summary>
    internal static bool SameFile(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var leftIdentity = TryReadIdentity(left);
            if (leftIdentity is null)
            {
                return false;
            }

            var rightIdentity = TryReadIdentity(right);
            return rightIdentity is not null && leftIdentity == rightIdentity;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static (uint Volume, uint IndexHigh, uint IndexLow)? TryReadIdentity(string path)
    {
        // Attributes-only access with full sharing: ETABS holds the .edb open while
        // the model is loaded, and this must never contend with it.
        using var handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        return GetFileInformationByHandle(handle, out var information)
            ? (information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow)
            : null;
    }

    // Classic DllImport rather than the source-generated LibraryImport: the latter
    // requires AllowUnsafeBlocks project-wide, which is far too broad a change to make
    // for two P/Invokes on a sidecar that otherwise compiles fully safe.
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
