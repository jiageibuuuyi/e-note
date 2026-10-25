using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DesktopNotes.Application.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DesktopNotes.Platforms.Windows;

public sealed class WindowsSourceCleanup : ISourceCleanupPort
{
    public bool SimulateDeleteDenied { get; set; }
    public bool SimulateDeleteUncertain { get; set; }

    public string GetDeviceId(string path)
    {
        string full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (TryGetWin32Identity(full, out uint volSerial, out _, out _))
            {
                return $"win-vol:{volSerial:X8}:{Environment.MachineName}";
            }
        }

        string root = Path.GetPathRoot(full) ?? "root";
        return $"dev:{Environment.MachineName}:{root.ToUpperInvariant()}";
    }

    public string GetFileIdentity(string path)
    {
        string full = Path.GetFullPath(path);
        var attr = File.GetAttributes(full);
        bool isDir = (attr & FileAttributes.Directory) != 0;

        if (OperatingSystem.IsWindows() && TryGetWin32Identity(full, out uint volSerial, out uint idxHigh, out uint idxLow))
        {
            ulong fileIndex = ((ulong)idxHigh << 32) | idxLow;
            return $"win:{volSerial:X8}:{fileIndex:X16}";
        }

        if (isDir)
        {
            var di = new DirectoryInfo(full);
            return $"dir:{di.CreationTimeUtc.Ticks}:{di.FullName}";
        }

        var fi = new FileInfo(full);
        return $"file:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:{fi.CreationTimeUtc.Ticks}";
    }

    public SourceCleanupOutcome RemoveVerifiedSource(string sourcePath, string expectedIdentity, string? expectedSha256)
    {
        if (SimulateDeleteUncertain)
        {
            return SourceCleanupOutcome.Uncertain;
        }

        if (SimulateDeleteDenied)
        {
            return SourceCleanupOutcome.Denied;
        }

        string full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
        {
            return Directory.Exists(full) ? SourceCleanupOutcome.Changed : SourceCleanupOutcome.Removed;
        }

        try
        {
            var info = new FileInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return SourceCleanupOutcome.Changed;
            }

            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
            {
                return SourceCleanupOutcome.Denied;
            }

            string currentIdentity = GetFileIdentity(full);
            if (!string.Equals(currentIdentity, expectedIdentity, StringComparison.Ordinal))
            {
                return SourceCleanupOutcome.Changed;
            }

            // Open with FileShare.None (or Delete-exclusive) while re-verifying SHA-256 so another process
            // cannot modify or swap the file between verification and removal.
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None, 1024 * 64, FileOptions.SequentialScan))
            {
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    byte[] hashBytes = SHA256.HashData(fs);
                    string actualSha = Convert.ToHexStringLower(hashBytes);
                    if (!string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return SourceCleanupOutcome.Changed;
                    }
                }
            }

            File.Delete(full);
            return SourceCleanupOutcome.Removed;
        }
        catch (UnauthorizedAccessException)
        {
            return SourceCleanupOutcome.Denied;
        }
        catch (IOException)
        {
            return SourceCleanupOutcome.Denied;
        }
    }

    public SourceCleanupOutcome RemoveEmptyVerifiedDirectory(
        string directoryPath,
        string expectedIdentity,
        IReadOnlySet<string> expectedChildRelativeNames)
    {
        if (SimulateDeleteUncertain)
        {
            return SourceCleanupOutcome.Uncertain;
        }

        if (SimulateDeleteDenied)
        {
            return SourceCleanupOutcome.Denied;
        }

        string full = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(full))
        {
            return File.Exists(full) ? SourceCleanupOutcome.Changed : SourceCleanupOutcome.Removed;
        }

        try
        {
            var di = new DirectoryInfo(full);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return SourceCleanupOutcome.Changed;
            }

            string currentIdentity = GetFileIdentity(full);
            if (!string.Equals(currentIdentity, expectedIdentity, StringComparison.Ordinal))
            {
                return SourceCleanupOutcome.Changed;
            }

            // Never recursively force-delete! If any child entries remain (e.g. a new file was added during copy),
            // report Changed so the newly added file is preserved.
            var remainingEntries = Directory.EnumerateFileSystemEntries(full).ToList();
            if (remainingEntries.Count > 0)
            {
                return SourceCleanupOutcome.Changed;
            }

            Directory.Delete(full, recursive: false);
            return SourceCleanupOutcome.Removed;
        }
        catch (UnauthorizedAccessException)
        {
            return SourceCleanupOutcome.Denied;
        }
        catch (IOException)
        {
            return SourceCleanupOutcome.Denied;
        }
    }

    private static bool TryGetWin32Identity(string path, out uint volumeSerial, out uint indexHigh, out uint indexLow)
    {
        volumeSerial = 0;
        indexHigh = 0;
        indexLow = 0;

        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint OPEN_EXISTING = 3;
        const uint FILE_SHARE_ALL = 0x00000001 | 0x00000002 | 0x00000004;

        using SafeFileHandle handle = CreateFileW(
            path,
            0, // query metadata only
            FILE_SHARE_ALL,
            nint.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            nint.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
        {
            return false;
        }

        volumeSerial = info.dwVolumeSerialNumber;
        indexHigh = info.nFileIndexHigh;
        indexLow = info.nFileIndexLow;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);
}
