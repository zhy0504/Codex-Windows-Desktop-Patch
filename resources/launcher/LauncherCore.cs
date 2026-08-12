using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexPatch.NativeLauncher
{
    internal static class LauncherConstants
    {
        internal const string Version = "1.0.0";
        internal const long UpdateSchemaVersion = 2;
        internal const string Repository = "zhy0504/Codex-Windows-Desktop-Patch";
        internal const string NativeFilename = "CodexPatchLauncher.exe";
        internal const string ManagerIconFilename = "CodexPatchManager.ico";
        internal const string ManagerShortcutFilename = "Codex Desktop Patch Manager.lnk";
        internal const string ManifestFilename = "CodexPatch-update.json";
        internal const string CleanupFilename = "pending-cleanup.json";
        internal const string RepairStateFilename = "pending-repair.json";
        internal const string IntegrityFilename = "CodexPatch-integrity.json";
        internal const string LaunchIntegrityFilename = "launch-integrity.json";
        internal const string NativeHostArgument = "--codex-patch-native-host";
        internal const string DirectLaunchArgument = "-NoUpdate";
        internal const string SelfTestArgument = "-SelfTest";
        internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        internal static readonly TimeSpan LaunchIntegrityInterval = TimeSpan.FromHours(24);
        internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromHours(1);
        internal static readonly Regex ArtifactPattern = new Regex(
            @"^CX-\d+(?:\.\d+){2,3}-p\d+(?:\.\d+)+$",
            RegexOptions.CultureInvariant);
        internal static readonly Regex ReleasePattern = new Regex(
            @"^windows-msstore-(?<msix>\d+(?:\.\d+){2,3})-desktop-patch-(?<patch>\d+(?:\.\d+)+)$",
            RegexOptions.CultureInvariant);
        internal static readonly string[] RequiredPayloadFiles = {
            "ChatGPT.exe", "Codex.exe", NativeFilename,
            @"resources\app.asar", @"resources\codex.exe",
            @"resources\codex-powershell-resolver.js", @"resources\codex-powershell-shim.exe"
        };
    }

    internal static class JsonStore
    {
        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 8 * 1024 * 1024;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        internal static Dictionary<string, object> ReadObject(string path)
        {
            if (!File.Exists(path)) return null;
            object value = CreateSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            Dictionary<string, object> result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("JSON root must be an object: " + path);
            return result;
        }

        internal static string Serialize(object value)
        {
            return CreateSerializer().Serialize(value);
        }

        internal static void WriteAtomic(string path, object value)
        {
            string parent = Path.GetDirectoryName(path);
            if (System.String.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("JSON path has no parent: " + path);
            Directory.CreateDirectory(parent);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string backup = path + ".backup-" + Guid.NewGuid().ToString("N");
            bool replaced = false;
            try
            {
                File.WriteAllText(temporary, Serialize(value) + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, backup, true);
                    replaced = true;
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (replaced && File.Exists(backup)) File.Delete(backup);
            }
        }

        internal static object Required(Dictionary<string, object> value, string name)
        {
            object result;
            if (value == null || !value.TryGetValue(name, out result) || result == null)
                throw new InvalidDataException("Required JSON property is missing: " + name);
            return result;
        }

        internal static object Optional(Dictionary<string, object> value, string name)
        {
            object result;
            return value != null && value.TryGetValue(name, out result) ? result : null;
        }

        internal static Dictionary<string, object> Object(Dictionary<string, object> value, string name)
        {
            Dictionary<string, object> result = Required(value, name) as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("JSON property must be an object: " + name);
            return result;
        }

        internal static string String(Dictionary<string, object> value, string name)
        {
            string result = Convert.ToString(Required(value, name), CultureInfo.InvariantCulture);
            if (System.String.IsNullOrWhiteSpace(result)) throw new InvalidDataException("JSON property is empty: " + name);
            return result;
        }

        internal static string OptionalString(Dictionary<string, object> value, string name)
        {
            object result = Optional(value, name);
            return result == null ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
        }

        internal static bool Boolean(Dictionary<string, object> value, string name)
        {
            return Convert.ToBoolean(Required(value, name), CultureInfo.InvariantCulture);
        }

        internal static long Integer(Dictionary<string, object> value, string name)
        {
            return Convert.ToInt64(Required(value, name), CultureInfo.InvariantCulture);
        }
    }

    internal static class PathSafety
    {
        internal static string NormalizeRoot(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Install root is required.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static string RequireChild(string root, string candidate, string label)
        {
            string normalizedRoot = NormalizeRoot(root);
            string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(label + " must remain under the install root: " + normalizedCandidate);
            return normalizedCandidate;
        }

        internal static bool IsChild(string root, string candidate)
        {
            try { RequireChild(root, candidate, "Path"); return true; }
            catch { return false; }
        }

        internal static string RequireDeletionTarget(string root, string candidate, string label)
        {
            string normalizedRoot = NormalizeRoot(root);
            string normalizedCandidate = RequireChild(normalizedRoot, candidate, label);
            RejectReparsePoint(normalizedRoot, label);
            string relative = normalizedCandidate.Substring(normalizedRoot.Length + 1);
            string current = normalizedRoot;
            foreach (string segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                RejectReparsePoint(current, label);
            }
            return normalizedCandidate;
        }

        private static void RejectReparsePoint(string path, string label)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(label + " crosses a reparse point: " + path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        internal static string SafeArchivePath(string destination, string entryName)
        {
            string normalized = (entryName ?? String.Empty).Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            if (String.IsNullOrWhiteSpace(normalized)) return null;
            string[] segments = normalized.Split('/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                Regex.IsMatch(normalized, @"^[A-Za-z]:/", RegexOptions.CultureInvariant) ||
                segments.Any(delegate(string part)
                {
                    return part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
                }))
                throw new InvalidDataException("ZIP contains an unsafe path: " + entryName);
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return root + normalized.Replace('/', Path.DirectorySeparatorChar);
        }
    }

    internal static class Versioning
    {
        internal static int Compare(string left, string right)
        {
            if (!Regex.IsMatch(left ?? String.Empty, @"^\d+(?:\.\d+)+$") ||
                !Regex.IsMatch(right ?? String.Empty, @"^\d+(?:\.\d+)+$"))
                throw new InvalidDataException("Versions must contain numeric dot-separated components.");
            string[] a = left.Split('.');
            string[] b = right.Split('.');
            int count = Math.Max(a.Length, b.Length);
            for (int index = 0; index < count; index++)
            {
                UInt64 av = index < a.Length ? UInt64.Parse(a[index], CultureInfo.InvariantCulture) : 0;
                UInt64 bv = index < b.Length ? UInt64.Parse(b[index], CultureInfo.InvariantCulture) : 0;
                if (av < bv) return -1;
                if (av > bv) return 1;
            }
            return 0;
        }

        internal static int CompareRelease(CurrentInstall current, ReleaseIdentity candidate)
        {
            int upstream = Compare(current.MsixVersion, candidate.MsixVersion);
            return upstream != 0 ? upstream : Compare(current.PatchVersion, candidate.PatchVersion);
        }
    }

    internal sealed class RootFileMutex : IDisposable
    {
        private Mutex _mutex;

        internal static RootFileMutex Acquire(string root, string scope, int timeoutMilliseconds = 10000)
        {
            if (!Regex.IsMatch(scope ?? String.Empty, @"^[A-Za-z0-9]+$"))
                throw new ArgumentException("Mutex scope is invalid.", "scope");
            string normalized = PathSafety.NormalizeRoot(root).ToLowerInvariant();
            string key;
            using (SHA256 hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", String.Empty).Substring(0, 24);
            Mutex mutex = new Mutex(false, @"Local\CodexDesktopPatch" + scope + "-" + key);
            bool acquired;
            try { acquired = mutex.WaitOne(timeoutMilliseconds); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { mutex.Dispose(); throw new TimeoutException("Timed out waiting to update " + scope + "."); }
            return new RootFileMutex { _mutex = mutex };
        }

        public void Dispose()
        {
            if (_mutex == null) return;
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }

    internal sealed class InstallOperationLock : IDisposable
    {
        private const string DesktopBaselineMutexName = @"Local\CodexDesktopPatchUpdater-zhy0504";
        private Mutex _desktopBaselineMutex;
        private RootFileMutex _rootMutex;

        internal static InstallOperationLock Acquire(string root, int timeoutMilliseconds = 30000)
        {
            Mutex desktopBaseline = null;
            RootFileMutex rootMutex = null;
            try
            {
                desktopBaseline = AcquireNamed(DesktopBaselineMutexName, timeoutMilliseconds);
                rootMutex = RootFileMutex.Acquire(root, "InstallOperation", timeoutMilliseconds);
                return new InstallOperationLock
                {
                    _desktopBaselineMutex = desktopBaseline,
                    _rootMutex = rootMutex
                };
            }
            catch
            {
                ReleaseNamed(desktopBaseline);
                throw;
            }
        }

        private static Mutex AcquireNamed(string name, int timeoutMilliseconds)
        {
            Mutex mutex = new Mutex(false, name);
            bool acquired;
            try { acquired = mutex.WaitOne(timeoutMilliseconds); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return mutex;
            mutex.Dispose();
            throw new TimeoutException("Another Codex Desktop Patch installation operation is still running.");
        }

        private static void ReleaseNamed(Mutex mutex)
        {
            if (mutex == null) return;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }

        public void Dispose()
        {
            if (_rootMutex != null)
            {
                _rootMutex.Dispose();
                _rootMutex = null;
            }
            ReleaseNamed(_desktopBaselineMutex);
            _desktopBaselineMutex = null;
        }
    }

    internal sealed class CurrentInstall
    {
        internal Dictionary<string, object> State;
        internal string InstallPath;
        internal string AppPath;
        internal string ArtifactBase;
        internal string ReleaseTag;
        internal string MsixVersion;
        internal string PatchVersion;
    }

    internal sealed class LauncherSettings
    {
        internal bool AutoUpdateEnabled = true;
        internal bool KeepCurrentVersion = true;
        internal int MaxRetainedVersions;

        internal Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "autoUpdateEnabled", AutoUpdateEnabled },
                { "keepCurrentVersion", KeepCurrentVersion },
                { "maxRetainedVersions", MaxRetainedVersions }
            };
        }
    }

    internal sealed class InstalledVersion
    {
        internal string ArtifactBase;
        internal string ReleaseTag;
        internal string MsixVersion;
        internal string PatchVersion;
        internal string InstallPath;
        internal string InstalledAt;
        internal bool IsCurrent;
        internal string Note = String.Empty;
        internal bool IsPinned;
        internal bool HasIntegrityEvidence;
        internal bool IntegrityEvidenceFromSidecar;
        internal string IntegrityIssue;
    }

    internal sealed class InstalledIntegrityException : IOException
    {
        internal InstalledIntegrityException(string message) : base(message) { }
    }

    internal sealed class LauncherArguments
    {
        internal string InstallRoot;
        internal bool NativeHost;
        internal bool CheckOnly;
        internal bool UpdateOnly;
        internal bool ForceUpdateCheck;
        internal bool NoUpdate;
        internal bool AcceptUpdate;
        internal bool SkipBackup;
        internal bool DisableAutoUpdate;
        internal bool EnableAutoUpdate;
        internal bool InstallOnly;
        internal bool SelfTest;
        internal string RollbackTo;
        internal readonly List<string> CodexArguments = new List<string>();

        internal bool HasCommandMode
        {
            get
            {
                return CheckOnly || UpdateOnly || NoUpdate || AcceptUpdate || SkipBackup ||
                    DisableAutoUpdate || EnableAutoUpdate || InstallOnly || !String.IsNullOrWhiteSpace(RollbackTo) ||
                    SelfTest || CodexArguments.Count > 0;
            }
        }

        internal static LauncherArguments Parse(string[] args)
        {
            LauncherArguments result = new LauncherArguments();
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                string normalized = argument.ToLowerInvariant();
                if (normalized == LauncherConstants.NativeHostArgument) result.NativeHost = true;
                else if (normalized == "-installroot" || normalized == "--install-root") result.InstallRoot = ReadValue(args, ref index, argument);
                else if (normalized == "-checkonly" || normalized == "--check-only") result.CheckOnly = true;
                else if (normalized == "-updateonly" || normalized == "--update-only") result.UpdateOnly = true;
                else if (normalized == "-forceupdatecheck" || normalized == "--force-update-check") result.ForceUpdateCheck = true;
                else if (normalized == "-noupdate" || normalized == "--no-update") result.NoUpdate = true;
                else if (normalized == "-acceptupdate" || normalized == "--accept-update") result.AcceptUpdate = true;
                else if (normalized == "-skipbackup" || normalized == "--skip-backup") result.SkipBackup = true;
                else if (normalized == "-disableautoupdate" || normalized == "--disable-auto-update") result.DisableAutoUpdate = true;
                else if (normalized == "-enableautoupdate" || normalized == "--enable-auto-update") result.EnableAutoUpdate = true;
                else if (normalized == "-installonly" || normalized == "--install-only") result.InstallOnly = true;
                else if (normalized == "-rollbackto" || normalized == "--rollback-to") result.RollbackTo = ReadValue(args, ref index, argument);
                else if (normalized == "-selftest" || normalized == "--self-test") result.SelfTest = true;
                else result.CodexArguments.Add(argument);
            }
            if (result.DisableAutoUpdate && result.EnableAutoUpdate)
                throw new ArgumentException("DisableAutoUpdate and EnableAutoUpdate cannot be used together.");
            if (result.InstallOnly && String.IsNullOrWhiteSpace(result.InstallRoot))
                throw new ArgumentException("InstallOnly requires InstallRoot.");
            return result;
        }

        private static string ReadValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length) throw new ArgumentException(name + " requires a value.");
            index++;
            string value = args[index];
            if (String.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException(name + " requires a value.");
            return value;
        }

        internal string[] ForwardedArguments(string root)
        {
            List<string> result = new List<string>();
            result.Add(LauncherConstants.NativeHostArgument);
            result.Add("-InstallRoot");
            result.Add(root);
            if (CheckOnly) result.Add("-CheckOnly");
            if (UpdateOnly) result.Add("-UpdateOnly");
            if (ForceUpdateCheck) result.Add("-ForceUpdateCheck");
            if (NoUpdate) result.Add("-NoUpdate");
            if (AcceptUpdate) result.Add("-AcceptUpdate");
            if (SkipBackup) result.Add("-SkipBackup");
            if (DisableAutoUpdate) result.Add("-DisableAutoUpdate");
            if (EnableAutoUpdate) result.Add("-EnableAutoUpdate");
            if (!String.IsNullOrWhiteSpace(RollbackTo)) { result.Add("-RollbackTo"); result.Add(RollbackTo); }
            if (SelfTest) result.Add("-SelfTest");
            result.AddRange(CodexArguments);
            return result.ToArray();
        }
    }

    internal static class LauncherCore
    {
        private const int HashBufferSize = 1024 * 1024;
        private const uint ShellChangeUpdateItem = 0x00002000;
        private const uint ShellNotifyPathUnicode = 0x0005;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

        internal static CurrentInstall LoadCurrent(string root)
        {
            using (InstallOperationLock.Acquire(root)) return LoadCurrentUnlocked(root);
        }

        internal static CurrentInstall LoadCurrentState(string root)
        {
            using (InstallOperationLock.Acquire(root)) return LoadCurrentStateUnlocked(root);
        }

        internal static CurrentInstall LoadCurrentUnlocked(string root)
        {
            return ReadCurrentStateUnlocked(root, true);
        }

        internal static CurrentInstall LoadCurrentStateUnlocked(string root)
        {
            return ReadCurrentStateUnlocked(root, false);
        }

        private static CurrentInstall LoadCurrentForLaunchUnlocked(string root)
        {
            CurrentInstall current = LoadCurrentStateUnlocked(root);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            // This is the explicit 24-hour launch-integrity tradeoff. Install, update,
            // rollback, repair, and manual validation continue to require full hashes.
            if (HasRecentLaunchIntegrity(root, current, now)) return current;

            CurrentInstall verified = LoadCurrentUnlocked(root);
            RecordLaunchIntegrity(root, verified, DateTimeOffset.UtcNow);
            return verified;
        }

        private static bool HasRecentLaunchIntegrity(string root, CurrentInstall current, DateTimeOffset now)
        {
            try
            {
                string activatedAt = JsonStore.OptionalString(current.State, "activatedAt");
                if (String.IsNullOrWhiteSpace(activatedAt)) return false;
                Dictionary<string, object> cache = JsonStore.ReadObject(
                    Path.Combine(root, LauncherConstants.LaunchIntegrityFilename));
                if (cache == null || JsonStore.Integer(cache, "schemaVersion") != 1 ||
                    JsonStore.String(cache, "artifactBase") != current.ArtifactBase ||
                    JsonStore.String(cache, "releaseTag") != current.ReleaseTag ||
                    JsonStore.String(cache, "msixVersion") != current.MsixVersion ||
                    JsonStore.String(cache, "patchVersion") != current.PatchVersion ||
                    JsonStore.String(cache, "installPath") != current.InstallPath ||
                    JsonStore.String(cache, "activatedAt") != activatedAt)
                    return false;

                DateTimeOffset verifiedAt;
                string serialized = JsonStore.String(cache, "verifiedAt");
                if (!DateTimeOffset.TryParse(serialized, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out verifiedAt))
                    return false;
                TimeSpan age = now - verifiedAt;
                return age >= TimeSpan.Zero && age < LauncherConstants.LaunchIntegrityInterval;
            }
            catch { return false; }
        }

        private static void RecordLaunchIntegrity(string root, CurrentInstall current, DateTimeOffset verifiedAt)
        {
            string activatedAt = JsonStore.OptionalString(current.State, "activatedAt");
            if (String.IsNullOrWhiteSpace(activatedAt)) return;
            try
            {
                JsonStore.WriteAtomic(Path.Combine(root, LauncherConstants.LaunchIntegrityFilename),
                    new Dictionary<string, object>
                    {
                        { "schemaVersion", 1 },
                        { "artifactBase", current.ArtifactBase },
                        { "releaseTag", current.ReleaseTag },
                        { "msixVersion", current.MsixVersion },
                        { "patchVersion", current.PatchVersion },
                        { "installPath", current.InstallPath },
                        { "activatedAt", activatedAt },
                        { "verifiedAt", verifiedAt.ToString("o", CultureInfo.InvariantCulture) }
                    });
            }
            catch (Exception error) { WriteLog(root, "Could not persist launch integrity timestamp: " + error.Message); }
        }

        private static CurrentInstall ReadCurrentStateUnlocked(string root, bool verifyIntegrity)
        {
            root = PathSafety.NormalizeRoot(root);
            string statePath = Path.Combine(root, "current.json");
            Dictionary<string, object> state = JsonStore.ReadObject(statePath);
            if (state == null) throw new InvalidDataException("Current installation state was not found: " + statePath);
            if (JsonStore.Integer(state, "schemaVersion") != 1) throw new InvalidDataException("Unsupported current installation state schema.");
            string artifactBase = JsonStore.String(state, "artifactBase");
            string installPath = PathSafety.RequireChild(root, JsonStore.String(state, "installPath"), "Current install path");
            InstalledVersion installed = ReadInstalledVersion(root, installPath, artifactBase, true);
            string releaseTag = JsonStore.String(state, "releaseTag");
            string msix = JsonStore.String(state, "msixVersion");
            string patch = JsonStore.String(state, "patchVersion");
            if (releaseTag != installed.ReleaseTag || msix != installed.MsixVersion || patch != installed.PatchVersion)
                throw new InvalidDataException("Current installation state does not match its installation marker.");
            if (verifyIntegrity) installed = RequireInstalledIntegrity(root, installed, true);
            string appPath = Path.Combine(installPath, "ChatGPT.exe");
            return new CurrentInstall
            {
                State = state,
                InstallPath = installPath,
                AppPath = appPath,
                ArtifactBase = artifactBase,
                ReleaseTag = releaseTag,
                MsixVersion = msix,
                PatchVersion = patch
            };
        }

        internal static bool TryRecoverCurrent(string root, out CurrentInstall current)
        {
            root = PathSafety.NormalizeRoot(root);
            current = null;
            using (InstallOperationLock.Acquire(root))
            {
                InstalledVersion selected = null;
                foreach (InstalledVersion candidate in ListInstalledVersions(root, null))
                {
                    try { selected = RequireInstalledIntegrity(root, candidate, true); break; }
                    catch (Exception error) { WriteLog(root, "Skipped untrusted recovery candidate " + candidate.ArtifactBase + ": " + error.Message); }
                }
                if (selected == null) return false;
                Dictionary<string, object> state = new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "releaseTag", selected.ReleaseTag },
                    { "artifactBase", selected.ArtifactBase },
                    { "msixVersion", selected.MsixVersion },
                    { "patchVersion", selected.PatchVersion },
                    { "installPath", selected.InstallPath },
                    { "activatedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                    { "activationReason", "state-recovery" }
                };
                JsonStore.WriteAtomic(Path.Combine(root, "current.json"), state);
                current = LoadCurrentStateUnlocked(root);
                WriteLog(root, "Recovered current installation state: " + selected.ArtifactBase);
                return true;
            }
        }

        internal static LauncherSettings LoadSettings(string root)
        {
            LauncherSettings settings = new LauncherSettings();
            Dictionary<string, object> saved = JsonStore.ReadObject(Path.Combine(root, "settings.json"));
            if (saved == null) return settings;
            if (JsonStore.Integer(saved, "schemaVersion") != 1) throw new InvalidDataException("Unsupported launcher settings schema.");
            settings.AutoUpdateEnabled = JsonStore.Boolean(saved, "autoUpdateEnabled");
            object keep = JsonStore.Optional(saved, "keepCurrentVersion");
            if (keep != null) settings.KeepCurrentVersion = Convert.ToBoolean(keep, CultureInfo.InvariantCulture);
            object maximum = JsonStore.Optional(saved, "maxRetainedVersions");
            if (maximum != null)
            {
                settings.MaxRetainedVersions = Convert.ToInt32(maximum, CultureInfo.InvariantCulture);
                if (settings.MaxRetainedVersions < 0 || settings.MaxRetainedVersions > 50)
                    throw new InvalidDataException("Maximum retained version count is invalid.");
            }
            return settings;
        }

        internal static void SaveSettings(string root, LauncherSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            using (RootFileMutex.Acquire(root, "Settings"))
                JsonStore.WriteAtomic(Path.Combine(root, "settings.json"), settings.ToJson());
        }

        internal static LauncherSettings UpdateSettings(string root, Action<LauncherSettings> update)
        {
            if (update == null) throw new ArgumentNullException("update");
            using (RootFileMutex.Acquire(root, "Settings"))
            {
                LauncherSettings settings = LoadSettings(root);
                update(settings);
                JsonStore.WriteAtomic(Path.Combine(root, "settings.json"), settings.ToJson());
                return settings;
            }
        }

        internal static Process LaunchCodex(CurrentInstall current, IEnumerable<string> arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = current.AppPath;
            info.WorkingDirectory = current.InstallPath;
            info.UseShellExecute = true;
            info.Arguments = JoinArguments(arguments ?? new string[0]);
            Process process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("Windows did not start Codex.");
            return process;
        }

        internal static Process LaunchCurrent(string root, IEnumerable<string> arguments)
        {
            using (InstallOperationLock.Acquire(root))
                return LaunchCodex(LoadCurrentForLaunchUnlocked(root), arguments);
        }

        internal static Process LaunchCurrentHost(string root, CurrentInstall expected, IEnumerable<string> arguments)
        {
            if (expected == null) throw new ArgumentNullException("expected");
            using (InstallOperationLock.Acquire(root))
            {
                CurrentInstall current = LoadCurrentStateUnlocked(root);
                bool unchanged = current.ArtifactBase == expected.ArtifactBase &&
                    current.ReleaseTag == expected.ReleaseTag &&
                    current.MsixVersion == expected.MsixVersion &&
                    current.PatchVersion == expected.PatchVersion &&
                    String.Equals(current.InstallPath, expected.InstallPath, StringComparison.OrdinalIgnoreCase);
                if (unchanged) VerifyInstalledPayloadHash(current.InstallPath, LauncherConstants.NativeFilename);
                else current = LoadCurrentUnlocked(root);

                string launcherPath = Path.Combine(current.InstallPath, LauncherConstants.NativeFilename);
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    WorkingDirectory = current.InstallPath,
                    UseShellExecute = false,
                    Arguments = JoinArguments(arguments ?? new string[0])
                };
                Process process = Process.Start(info);
                if (process == null) throw new InvalidOperationException("Windows did not start the versioned launcher.");
                return process;
            }
        }

        internal static string JoinArguments(IEnumerable<string> values)
        {
            return String.Join(" ", values.Select(QuoteWindowsArgument).ToArray());
        }

        internal static string QuoteWindowsArgument(string value)
        {
            if (value == null) value = String.Empty;
            if (!Regex.IsMatch(value, "[\\s\"]")) return value;
            StringBuilder builder = new StringBuilder();
            builder.Append('"');
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"')
                {
                    builder.Append('\\', slashes * 2 + 1);
                    builder.Append('"');
                    slashes = 0;
                    continue;
                }
                if (slashes > 0) builder.Append('\\', slashes);
                slashes = 0;
                builder.Append(character);
            }
            if (slashes > 0) builder.Append('\\', slashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        internal static IList<InstalledVersion> ListInstalledVersions(string root, string currentArtifact)
        {
            List<InstalledVersion> versions = new List<InstalledVersion>();
            if (!Directory.Exists(root)) return versions;
            foreach (string directory in Directory.GetDirectories(root, "CX-*-p*"))
            {
                string artifact = Path.GetFileName(directory);
                if (!LauncherConstants.ArtifactPattern.IsMatch(artifact)) continue;
                try
                {
                    InstalledVersion version = ReadInstalledVersion(root, directory, artifact, true);
                    version.IsCurrent = String.Equals(artifact, currentArtifact, StringComparison.Ordinal);
                    versions.Add(version);
                }
                catch { }
            }
            versions.Sort(delegate(InstalledVersion left, InstalledVersion right)
            {
                int upstream = Versioning.Compare(right.MsixVersion, left.MsixVersion);
                return upstream != 0 ? upstream : Versioning.Compare(right.PatchVersion, left.PatchVersion);
            });
            VersionCatalog.Apply(root, versions);
            return versions;
        }

        internal static CurrentInstall Rollback(string root, string artifactBase)
        {
            if (!LauncherConstants.ArtifactPattern.IsMatch(artifactBase ?? String.Empty))
                throw new ArgumentException("RollbackTo must be a complete installed artifact name.");
            using (InstallOperationLock.Acquire(root))
            {
                LoadCurrentStateUnlocked(root);
                string installPath = PathSafety.RequireChild(root, Path.Combine(root, artifactBase), "Rollback install path");
                InstalledVersion installed = RequireInstalledIntegrity(root,
                    ReadInstalledVersion(root, installPath, artifactBase, true), true);
                Dictionary<string, object> current = new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "releaseTag", installed.ReleaseTag },
                    { "artifactBase", artifactBase },
                    { "msixVersion", installed.MsixVersion },
                    { "patchVersion", installed.PatchVersion },
                    { "installPath", installPath },
                    { "activatedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                    { "activationReason", "manual-rollback" }
                };
                JsonStore.WriteAtomic(Path.Combine(root, "current.json"), current);
                UpdateSettings(root, delegate(LauncherSettings settings) { settings.AutoUpdateEnabled = false; });
                return LoadCurrentStateUnlocked(root);
            }
        }

        internal static InstalledVersion ReadInstalledVersion(string root, string directory, string expectedArtifact, bool requireCanonicalPath)
        {
            root = PathSafety.NormalizeRoot(root);
            directory = PathSafety.RequireChild(root, directory, "Installed version path");
            string artifact = expectedArtifact ?? Path.GetFileName(directory);
            if (!LauncherConstants.ArtifactPattern.IsMatch(artifact ?? String.Empty))
                throw new InvalidDataException("Installed artifact name is invalid: " + artifact);
            if (requireCanonicalPath && !String.Equals(directory, Path.Combine(root, artifact), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installed version path does not match its artifact name: " + directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installed version directory must not be a reparse point: " + directory);

            Dictionary<string, object> marker = JsonStore.ReadObject(Path.Combine(directory, ".codex-patch-install.json"));
            if (marker == null || JsonStore.Integer(marker, "schemaVersion") != 1)
                throw new InvalidDataException("Installed version marker is missing or unsupported: " + directory);
            string markerArtifact = JsonStore.String(marker, "artifactBase");
            string releaseTag = JsonStore.String(marker, "releaseTag");
            string msix = JsonStore.String(marker, "msixVersion");
            string patch = JsonStore.String(marker, "patchVersion");
            ReleaseIdentity identity = ReleaseIdentity.Parse(releaseTag);
            Versioning.Compare(msix, "0.0.0");
            Versioning.Compare(patch, "0.0.0");
            if (markerArtifact != artifact || identity.ArtifactBase != artifact ||
                identity.MsixVersion != msix || identity.PatchVersion != patch)
                throw new InvalidDataException("Installed version marker identity is inconsistent: " + directory);
            string zipHash = JsonStore.String(marker, "zipSha256");
            if (!Regex.IsMatch(zipHash, @"^[0-9a-f]{64}$"))
                throw new InvalidDataException("Installed version marker has an invalid ZIP digest: " + directory);
            foreach (string relative in LauncherConstants.RequiredPayloadFiles)
            {
                string required = Path.Combine(directory, relative);
                if (!File.Exists(required)) throw new FileNotFoundException("Installed version is incomplete: " + relative, required);
            }
            InstalledVersion result = new InstalledVersion
            {
                ArtifactBase = artifact,
                ReleaseTag = releaseTag,
                MsixVersion = msix,
                PatchVersion = patch,
                InstallPath = directory,
                InstalledAt = JsonStore.OptionalString(marker, "installedAt")
            };
            object evidence = JsonStore.Optional(marker, "verifiedPayloads");
            if (evidence == null)
            {
                try
                {
                    ReadIntegritySidecar(result, false);
                    result.HasIntegrityEvidence = true;
                    result.IntegrityEvidenceFromSidecar = true;
                }
                catch (Exception error)
                {
                    result.IntegrityIssue = "安装标记缺少关键文件哈希证据，且无法使用迁移证据：" + error.Message +
                        "。请从 Release 修复或重新校验该版本。";
                }
            }
            else
            {
                try
                {
                    Dictionary<string, object> payloads = evidence as Dictionary<string, object>;
                    if (payloads == null) throw new InvalidDataException("verifiedPayloads must be an object.");
                    NormalizeVerifiedPayloads(payloads, "Installed version marker");
                    result.HasIntegrityEvidence = true;
                }
                catch (Exception error) { result.IntegrityIssue = "安装标记的关键文件哈希证据无效：" + error.Message; }
            }
            return result;
        }

        internal static InstalledVersion RequireInstalledIntegrity(string root, InstalledVersion version, bool requireCanonicalPath)
        {
            if (version == null) throw new ArgumentNullException("version");
            InstalledVersion refreshed = ReadInstalledVersion(root, version.InstallPath, version.ArtifactBase, requireCanonicalPath);
            if (refreshed.ReleaseTag != version.ReleaseTag || refreshed.MsixVersion != version.MsixVersion ||
                refreshed.PatchVersion != version.PatchVersion)
                throw new InstalledIntegrityException("安装版本身份在操作期间发生变化，已拒绝激活。请刷新版本列表后重试。");
            if (!refreshed.HasIntegrityEvidence)
                throw new InstalledIntegrityException(refreshed.IntegrityIssue ?? "安装版本缺少完整性证据，已拒绝激活。");
            if (refreshed.IntegrityEvidenceFromSidecar)
            {
                Dictionary<string, object> sidecarPayloads = ReadIntegritySidecar(refreshed, true);
                PersistInstalledIntegrityEvidence(root, refreshed, sidecarPayloads, requireCanonicalPath);
                refreshed.HasIntegrityEvidence = true;
                refreshed.IntegrityEvidenceFromSidecar = false;
            }
            else
            {
                Dictionary<string, object> marker = JsonStore.ReadObject(Path.Combine(refreshed.InstallPath, ".codex-patch-install.json"));
                Dictionary<string, string> payloads = NormalizeVerifiedPayloads(
                    JsonStore.Object(marker, "verifiedPayloads"), "Installed version marker");
                VerifyPayloadHashes(refreshed.InstallPath, payloads);
            }
            return refreshed;
        }

        internal static void PersistInstalledIntegrityEvidence(string root, InstalledVersion version,
            Dictionary<string, object> payloads, bool requireCanonicalPath)
        {
            if (version == null) throw new ArgumentNullException("version");
            InstalledVersion refreshed = ReadInstalledVersion(root, version.InstallPath, version.ArtifactBase, requireCanonicalPath);
            if (refreshed.ReleaseTag != version.ReleaseTag || refreshed.MsixVersion != version.MsixVersion ||
                refreshed.PatchVersion != version.PatchVersion)
                throw new InvalidDataException("Installed version identity changed before integrity evidence could be saved.");
            Dictionary<string, string> normalized = NormalizeVerifiedPayloads(payloads, "Verification report");
            VerifyPayloadHashes(refreshed.InstallPath, normalized);
            Dictionary<string, object> marker = JsonStore.ReadObject(Path.Combine(refreshed.InstallPath, ".codex-patch-install.json"));
            marker["verifiedPayloads"] = normalized.ToDictionary(
                delegate(KeyValuePair<string, string> item) { return item.Key; },
                delegate(KeyValuePair<string, string> item) { return (object)item.Value; }, StringComparer.Ordinal);
            JsonStore.WriteAtomic(Path.Combine(refreshed.InstallPath, ".codex-patch-install.json"), marker);
            version.HasIntegrityEvidence = true;
            version.IntegrityIssue = null;
        }

        internal static Dictionary<string, string> NormalizeVerifiedPayloads(
            Dictionary<string, object> payloads, string label)
        {
            if (payloads == null) throw new InvalidDataException(label + " payload evidence is missing.");
            HashSet<string> expected = new HashSet<string>(
                LauncherConstants.RequiredPayloadFiles.Select(delegate(string value) { return value.Replace('\\', '/'); }),
                StringComparer.Ordinal);
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> item in payloads)
            {
                string name = (item.Key ?? String.Empty).Replace('\\', '/');
                string digest = Convert.ToString(item.Value, CultureInfo.InvariantCulture);
                if (!expected.Contains(name) || result.ContainsKey(name) ||
                    !Regex.IsMatch(digest ?? String.Empty, @"^[0-9a-f]{64}$"))
                    throw new InvalidDataException(label + " contains invalid critical payload evidence: " + item.Key);
                result[name] = digest;
            }
            if (result.Count != expected.Count)
                throw new InvalidDataException(label + " critical payload evidence is incomplete.");
            return result;
        }

        private static void VerifyPayloadHashes(string directory, Dictionary<string, string> payloads)
        {
            foreach (KeyValuePair<string, string> payload in payloads)
            {
                string path = PathSafety.SafeArchivePath(directory, payload.Key);
                if (!File.Exists(path)) throw new InstalledIntegrityException("关键文件缺失，已拒绝激活：" + payload.Key);
                if (!String.Equals(Sha256(path), payload.Value, StringComparison.Ordinal))
                    throw new InstalledIntegrityException("关键文件哈希不匹配，已拒绝激活：" + payload.Key);
            }
        }

        private static void VerifyInstalledPayloadHash(string directory, string relativePath)
        {
            Dictionary<string, object> marker = JsonStore.ReadObject(Path.Combine(directory, ".codex-patch-install.json"));
            Dictionary<string, string> payloads = NormalizeVerifiedPayloads(
                JsonStore.Object(marker, "verifiedPayloads"), "Installed version marker");
            string normalized = relativePath.Replace('\\', '/');
            VerifyPayloadHashes(directory, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { normalized, payloads[normalized] }
            });
        }

        private static Dictionary<string, object> ReadIntegritySidecar(InstalledVersion version, bool verifyHashes)
        {
            string path = Path.Combine(version.InstallPath, LauncherConstants.IntegrityFilename);
            Dictionary<string, object> sidecar = JsonStore.ReadObject(path);
            if (sidecar == null || JsonStore.Integer(sidecar, "schemaVersion") != 1)
                throw new InvalidDataException("完整性迁移文件缺失或版本不受支持");
            if (JsonStore.String(sidecar, "releaseTag") != version.ReleaseTag ||
                JsonStore.String(sidecar, "artifactBase") != version.ArtifactBase ||
                JsonStore.String(sidecar, "msixVersion") != version.MsixVersion ||
                JsonStore.String(sidecar, "patchVersion") != version.PatchVersion)
                throw new InvalidDataException("完整性迁移文件与安装版本身份不一致");
            Dictionary<string, object> payloads = JsonStore.Object(sidecar, "verifiedPayloads");
            Dictionary<string, string> normalized = NormalizeVerifiedPayloads(payloads, "Integrity sidecar");
            if (verifyHashes) VerifyPayloadHashes(version.InstallPath, normalized);
            return normalized.ToDictionary(
                delegate(KeyValuePair<string, string> item) { return item.Key; },
                delegate(KeyValuePair<string, string> item) { return (object)item.Value; }, StringComparer.Ordinal);
        }

        internal static void WriteLog(string root, string message)
        {
            try
            {
                string directory = Path.Combine(root, "logs");
                Directory.CreateDirectory(directory);
                string safe = Regex.Replace(message ?? String.Empty, "[\\r\\n]+", " ");
                File.AppendAllText(Path.Combine(directory, "updater.log"),
                    DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + safe + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        internal static string Sha256(string path)
        {
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize, FileOptions.SequentialScan))
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
        }

        internal static void InstallFileAtomic(string source, string target)
        {
            if (String.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            string parent = Path.GetDirectoryName(target);
            Directory.CreateDirectory(parent);
            string replacement = target + ".new-" + Guid.NewGuid().ToString("N");
            string backup = target + ".backup-" + Guid.NewGuid().ToString("N");
            bool replaced = false;
            File.Copy(source, replacement, false);
            try
            {
                if (File.Exists(target)) { File.Replace(replacement, target, backup, true); replaced = true; }
                else File.Move(replacement, target);
            }
            finally
            {
                if (File.Exists(replacement)) File.Delete(replacement);
                if (replaced && File.Exists(backup)) File.Delete(backup);
            }
            NotifyShellItemChanged(target);
        }

        internal static void NotifyShellItemChanged(string path)
        {
            try { SHChangeNotify(ShellChangeUpdateItem, ShellNotifyPathUnicode, path, IntPtr.Zero); }
            catch { }
        }

        internal static bool IsLightTheme()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
            }
            catch { return true; }
        }

    }
}
