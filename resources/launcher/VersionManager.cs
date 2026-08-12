using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodexPatch.NativeLauncher
{
    internal sealed class VersionMetadata
    {
        internal string Note = String.Empty;
        internal bool IsPinned;
    }

    internal static class VersionCatalog
    {
        private static readonly object Sync = new object();

        internal static void Apply(string root, IEnumerable<InstalledVersion> versions)
        {
            Dictionary<string, VersionMetadata> metadata = Load(root);
            foreach (InstalledVersion version in versions)
            {
                VersionMetadata value;
                if (!metadata.TryGetValue(version.ArtifactBase, out value)) continue;
                version.Note = value.Note;
                version.IsPinned = value.IsPinned;
            }
        }

        internal static VersionMetadata Get(string root, string artifactBase)
        {
            VersionMetadata value;
            return Load(root).TryGetValue(artifactBase, out value) ? value : new VersionMetadata();
        }

        internal static void SetNote(string root, string artifactBase, string note)
        {
            ValidateArtifact(artifactBase);
            string normalized = (note ?? String.Empty).Trim();
            if (normalized.Length > 160) throw new ArgumentException("版本备注不能超过 160 个字符。");
            using (InstallOperationLock.Acquire(root))
            lock (Sync)
            using (RootFileMutex.Acquire(root, "VersionCatalog"))
            {
                RequireInstalledArtifact(root, artifactBase);
                Dictionary<string, VersionMetadata> values = LoadUnlocked(root);
                VersionMetadata value = GetOrCreate(values, artifactBase);
                value.Note = normalized;
                PruneEmpty(values, artifactBase);
                SaveUnlocked(root, values);
            }
        }

        internal static void SetPinned(string root, string artifactBase, bool pinned)
        {
            ValidateArtifact(artifactBase);
            using (InstallOperationLock.Acquire(root))
            lock (Sync)
            using (RootFileMutex.Acquire(root, "VersionCatalog"))
            {
                RequireInstalledArtifact(root, artifactBase);
                Dictionary<string, VersionMetadata> values = LoadUnlocked(root);
                VersionMetadata value = GetOrCreate(values, artifactBase);
                value.IsPinned = pinned;
                PruneEmpty(values, artifactBase);
                SaveUnlocked(root, values);
            }
        }

        internal static void Remove(string root, string artifactBase)
        {
            lock (Sync)
            using (RootFileMutex.Acquire(root, "VersionCatalog"))
            {
                Dictionary<string, VersionMetadata> values = LoadUnlocked(root);
                if (!values.Remove(artifactBase)) return;
                SaveUnlocked(root, values);
            }
        }

        private static Dictionary<string, VersionMetadata> Load(string root)
        {
            lock (Sync) return LoadUnlocked(root);
        }

        private static Dictionary<string, VersionMetadata> LoadUnlocked(string root)
        {
            Dictionary<string, VersionMetadata> result = new Dictionary<string, VersionMetadata>(StringComparer.Ordinal);
            Dictionary<string, object> document = JsonStore.ReadObject(Path.Combine(root, "versions.json"));
            if (document == null) return result;
            if (JsonStore.Integer(document, "schemaVersion") != 1) throw new InvalidDataException("Unsupported version metadata schema.");
            Dictionary<string, object> entries = JsonStore.Object(document, "versions");
            foreach (KeyValuePair<string, object> item in entries)
            {
                if (!LauncherConstants.ArtifactPattern.IsMatch(item.Key)) continue;
                Dictionary<string, object> value = item.Value as Dictionary<string, object>;
                if (value == null) continue;
                string note = JsonStore.OptionalString(value, "note") ?? String.Empty;
                object pinned = JsonStore.Optional(value, "pinned");
                result[item.Key] = new VersionMetadata
                {
                    Note = note.Length <= 160 ? note : note.Substring(0, 160),
                    IsPinned = pinned != null && Convert.ToBoolean(pinned, CultureInfo.InvariantCulture)
                };
            }
            return result;
        }

        private static void SaveUnlocked(string root, Dictionary<string, VersionMetadata> values)
        {
            Dictionary<string, object> entries = new Dictionary<string, object>();
            foreach (KeyValuePair<string, VersionMetadata> item in values)
            {
                entries[item.Key] = new Dictionary<string, object>
                {
                    { "note", item.Value.Note }, { "pinned", item.Value.IsPinned }
                };
            }
            JsonStore.WriteAtomic(Path.Combine(root, "versions.json"), new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "versions", entries }
            });
        }

        private static VersionMetadata GetOrCreate(Dictionary<string, VersionMetadata> values, string artifactBase)
        {
            VersionMetadata value;
            if (!values.TryGetValue(artifactBase, out value))
            {
                value = new VersionMetadata();
                values[artifactBase] = value;
            }
            return value;
        }

        private static void PruneEmpty(Dictionary<string, VersionMetadata> values, string artifactBase)
        {
            VersionMetadata value;
            if (values.TryGetValue(artifactBase, out value) && !value.IsPinned && String.IsNullOrEmpty(value.Note))
                values.Remove(artifactBase);
        }

        private static void ValidateArtifact(string artifactBase)
        {
            if (!LauncherConstants.ArtifactPattern.IsMatch(artifactBase ?? String.Empty))
                throw new ArgumentException("Invalid installed version name.");
        }

        private static void RequireInstalledArtifact(string root, string artifactBase)
        {
            string canonical = Path.Combine(PathSafety.NormalizeRoot(root), artifactBase);
            LauncherCore.ReadInstalledVersion(root, canonical, artifactBase, true);
        }
    }

    internal static class VersionManager
    {
        internal static Process LaunchInstalled(string root, InstalledVersion version, IEnumerable<string> arguments)
        {
            if (version == null) throw new ArgumentNullException("version");
            using (InstallOperationLock.Acquire(root))
            {
                LauncherCore.LoadCurrentStateUnlocked(root);
                string installPath = PathSafety.RequireChild(root, Path.Combine(root, version.ArtifactBase), "Direct launch path");
                InstalledVersion trusted = LauncherCore.RequireInstalledIntegrity(root,
                    LauncherCore.ReadInstalledVersion(root, installPath, version.ArtifactBase, true), true);
                CurrentInstall target = new CurrentInstall
                {
                    InstallPath = trusted.InstallPath,
                    AppPath = Path.Combine(trusted.InstallPath, "ChatGPT.exe"),
                    ArtifactBase = trusted.ArtifactBase,
                    ReleaseTag = trusted.ReleaseTag,
                    MsixVersion = trusted.MsixVersion,
                    PatchVersion = trusted.PatchVersion
                };
                return LauncherCore.LaunchCodex(target, arguments);
            }
        }

        internal static long CalculateSize(InstalledVersion version)
        {
            if (version == null) throw new ArgumentNullException("version");
            return LongPathFileSystem.GetDirectorySize(version.InstallPath);
        }

        internal static void Delete(string root, CurrentInstall current, InstalledVersion version)
        {
            if (version == null) throw new ArgumentNullException("version");
            using (InstallOperationLock.Acquire(root))
            {
                CurrentInstall latest = LauncherCore.LoadCurrentStateUnlocked(root);
                if (String.Equals(latest.ArtifactBase, version.ArtifactBase, StringComparison.Ordinal))
                    throw new InvalidOperationException("不能删除当前正在使用的版本。");
                if (VersionCatalog.Get(root, version.ArtifactBase).IsPinned)
                    throw new InvalidOperationException("该版本已固定，请先取消固定后再删除。");
                string canonical = Path.Combine(PathSafety.NormalizeRoot(root), version.ArtifactBase);
                InstalledVersion target = LauncherCore.ReadInstalledVersion(root, canonical, version.ArtifactBase, true);
                string path = PathSafety.RequireDeletionTarget(root, target.InstallPath, "Version delete path");
                if (UpdateService.ProcessUsesPath(path)) throw new InvalidOperationException("该版本仍有进程正在运行，无法删除。");
                LongPathFileSystem.DeleteDirectory(root, path);
                VersionCatalog.Remove(root, version.ArtifactBase);
                LauncherCore.WriteLog(root, "Manually removed installed version: " + version.ArtifactBase);
            }
        }

        internal static IList<string> EnforceRetention(string root, string currentArtifact, int maximumBackups)
        {
            List<string> removed = new List<string>();
            if (maximumBackups <= 0) return removed;
            using (InstallOperationLock.Acquire(root))
            {
                CurrentInstall latest = LauncherCore.LoadCurrentStateUnlocked(root);
                IList<InstalledVersion> backups = LauncherCore.ListInstalledVersions(root, latest.ArtifactBase)
                    .Where(delegate(InstalledVersion value) { return !value.IsCurrent; }).ToList();
                int excess = backups.Count - maximumBackups;
                if (excess <= 0) return removed;
                foreach (InstalledVersion version in backups.Reverse())
                {
                    if (excess <= 0) break;
                    if (version.IsPinned || UpdateService.ProcessUsesPath(version.InstallPath)) continue;
                    try
                    {
                        if (VersionCatalog.Get(root, version.ArtifactBase).IsPinned) continue;
                        LongPathFileSystem.DeleteDirectory(root, version.InstallPath);
                        VersionCatalog.Remove(root, version.ArtifactBase);
                        removed.Add(version.ArtifactBase);
                        excess--;
                    }
                    catch (Exception error) { LauncherCore.WriteLog(root, "Retention cleanup deferred for " + version.ArtifactBase + ": " + error.Message); }
                }
            }
            return removed;
        }
    }

    internal static class LongPathFileSystem
    {
        private const int FileAttributeDirectory = 0x10;
        private const int FileAttributeReparsePoint = 0x400;
        private const int InvalidFileAttributes = -1;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            internal int FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint Reserved0;
            internal uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] internal string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out FindData data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string fileName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string pathName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesW(string fileName, int attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetFileAttributesW(string fileName);

        internal static long GetDirectorySize(string path)
        {
            long total = 0;
            Walk(path, delegate(string child, FindData data)
            {
                if ((data.FileAttributes & FileAttributeDirectory) == 0)
                    total += ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
            }, false);
            return total;
        }

        internal static void DeleteDirectory(string root, string path)
        {
            string full = PathSafety.RequireDeletionTarget(root, path, "Recursive delete path");
            Walk(full, delegate(string child, FindData data)
            {
                string longChild = ToLongPath(child);
                SetFileAttributesW(longChild, 0x80);
                bool directory = (data.FileAttributes & FileAttributeDirectory) != 0;
                bool reparse = (data.FileAttributes & FileAttributeReparsePoint) != 0;
                if (directory && reparse)
                {
                    if (!RemoveDirectoryW(longChild)) ThrowLastError("remove reparse directory", child);
                }
                else if (!directory && !DeleteFileW(longChild)) ThrowLastError("delete file", child);
            }, true);
            string longRoot = ToLongPath(full);
            SetFileAttributesW(longRoot, 0x80);
            if (!RemoveDirectoryW(longRoot)) ThrowLastError("remove directory", full);
        }

        private static void Walk(string path, Action<string, FindData> visitor, bool deleteDirectories)
        {
            int attributes = GetFileAttributesW(ToLongPath(path));
            if (attributes == InvalidFileAttributes) ThrowLastError("inspect directory", path);
            if ((attributes & FileAttributeDirectory) == 0)
                throw new InvalidDataException("Expected a directory: " + path);
            if ((attributes & FileAttributeReparsePoint) != 0)
                throw new InvalidDataException("Refusing to traverse a reparse-point directory: " + path);
            string pattern = ToLongPath(path.TrimEnd('\\') + "\\*");
            FindData data;
            IntPtr handle = FindFirstFileW(pattern, out data);
            if (handle == InvalidHandle)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2 || error == 3) return;
                throw new Win32Exception(error, "Could not enumerate " + path);
            }
            try
            {
                do
                {
                    if (data.FileName == "." || data.FileName == "..") continue;
                    string child = path.TrimEnd('\\') + "\\" + data.FileName;
                    bool directory = (data.FileAttributes & FileAttributeDirectory) != 0;
                    bool reparse = (data.FileAttributes & FileAttributeReparsePoint) != 0;
                    if (directory && !reparse)
                    {
                        Walk(child, visitor, deleteDirectories);
                        if (deleteDirectories)
                        {
                            string longChild = ToLongPath(child);
                            SetFileAttributesW(longChild, 0x80);
                            if (!RemoveDirectoryW(longChild)) ThrowLastError("remove directory", child);
                        }
                    }
                    else visitor(child, data);
                }
                while (FindNextFileW(handle, out data));
                int finalError = Marshal.GetLastWin32Error();
                if (finalError != 0 && finalError != 18) throw new Win32Exception(finalError, "Could not enumerate " + path);
            }
            finally { FindClose(handle); }
        }

        private static string ToLongPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        private static void ThrowLastError(string operation, string path)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Could not " + operation + ": " + path);
        }
    }
}
