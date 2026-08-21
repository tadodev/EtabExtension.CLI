// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// Whether two paths were shown to name the same file — and, crucially, whether the
/// question could be answered at all.
/// </summary>
internal enum FileIdentityMatch
{
    /// <summary>Both paths resolved, and to the same file.</summary>
    Same,

    /// <summary>Both paths resolved, to different files.</summary>
    Different,

    /// <summary>At least one path could not be identified. Nothing was disproved.</summary>
    Unprovable
}

/// <summary>
/// The verdict plus the Win32 error that produced it, so an unprovable answer can say
/// why rather than leaving a caller to guess at a network share or an exotic
/// filesystem.
/// </summary>
internal readonly record struct FileIdentityResult(FileIdentityMatch Match, int Win32Error)
{
    internal static FileIdentityResult Same { get; } = new(FileIdentityMatch.Same, 0);

    internal static FileIdentityResult Different { get; } = new(FileIdentityMatch.Different, 0);

    internal static FileIdentityResult Unprovable(int win32Error) =>
        new(FileIdentityMatch.Unprovable, win32Error);
}

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
    /// Compares two paths by file identity.
    ///
    /// <para>Never guesses. A path that cannot be identified — missing, a folder, an
    /// exotic or network filesystem that reports no index, or a non-Windows host —
    /// yields <see cref="FileIdentityMatch.Unprovable"/> carrying the Win32 error, not
    /// a match and not a mismatch. Callers decide what an unanswerable question means;
    /// this does not decide it for them by returning "different".</para>
    /// </summary>
    internal static FileIdentityResult Compare(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return FileIdentityResult.Unprovable(0);
        }

        if (!OperatingSystem.IsWindows())
        {
            return FileIdentityResult.Unprovable(0);
        }

        try
        {
            var (leftIdentity, leftError) = TryReadIdentity(left);
            if (leftIdentity is null)
            {
                return FileIdentityResult.Unprovable(leftError);
            }

            var (rightIdentity, rightError) = TryReadIdentity(right);
            if (rightIdentity is null)
            {
                return FileIdentityResult.Unprovable(rightError);
            }

            return leftIdentity == rightIdentity
                ? FileIdentityResult.Same
                : FileIdentityResult.Different;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return FileIdentityResult.Unprovable(0);
        }
    }

    private static ((uint Volume, uint IndexHigh, uint IndexLow)? Identity, int Win32Error)
        TryReadIdentity(string path)
    {
        // Attributes-only access with full sharing: ETABS holds the .edb open while the
        // model is loaded, and this must never contend with it. FILE_READ_ATTRIBUTES is
        // deliberate — unlike GENERIC_READ it is exempt from share-access checking, so
        // even a FileShare.None holder does not block identification.
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
            return (null, Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return (null, Marshal.GetLastWin32Error());
        }

        // Some network redirectors and filesystems report a zero index. That is "no
        // answer", not "index zero" — treating it as an identity would make every such
        // file identical to every other.
        if (information.FileIndexHigh == 0 && information.FileIndexLow == 0)
        {
            return (null, 0);
        }

        return (
            (information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow),
            0);
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
