using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexPatch.NativeLauncher
{
    internal sealed class ReleaseIdentity
    {
        internal string ReleaseTag;
        internal string ArtifactBase;
        internal string BundleName;
        internal string MsixVersion;
        internal string PatchVersion;

        internal static ReleaseIdentity Parse(string releaseTag)
        {
            Match match = LauncherConstants.ReleasePattern.Match(releaseTag ?? String.Empty);
            if (!match.Success) throw new InvalidDataException("Unexpected GitHub release tag: " + releaseTag);
            string msix = match.Groups["msix"].Value;
            string patch = match.Groups["patch"].Value;
            return new ReleaseIdentity
            {
                ReleaseTag = releaseTag,
                ArtifactBase = "CX-" + msix + "-p" + patch,
                BundleName = "CX-" + msix + "-p" + patch + "-bundle.zip",
                MsixVersion = msix,
                PatchVersion = patch
            };
        }
    }

    internal sealed class ReleaseAsset
    {
        internal string Name;
        internal string Url;
        internal long Size;
        internal string Digest;

        internal Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "name", Name }, { "url", Url }, { "size", Size }, { "digest", Digest }
            };
        }
    }

    internal sealed class ReleaseInfo
    {
        internal string TagName;
        internal List<ReleaseAsset> Assets = new List<ReleaseAsset>();

        internal Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "tagName", TagName },
                { "assets", Assets.Select(delegate(ReleaseAsset asset) { return (object)asset.ToJson(); }).ToArray() }
            };
        }

        internal static ReleaseInfo FromCached(Dictionary<string, object> value)
        {
            ReleaseInfo result = new ReleaseInfo();
            result.TagName = JsonStore.String(value, "tagName");
            foreach (object item in UpdateService.AsArray(JsonStore.Required(value, "assets")))
            {
                Dictionary<string, object> asset = item as Dictionary<string, object>;
                if (asset == null) throw new InvalidDataException("Cached release asset is invalid.");
                result.Assets.Add(new ReleaseAsset
                {
                    Name = JsonStore.String(asset, "name"),
                    Url = JsonStore.String(asset, "url"),
                    Size = JsonStore.Integer(asset, "size"),
                    Digest = JsonStore.OptionalString(asset, "digest")
                });
            }
            return result;
        }
    }

    internal sealed class UpdateCheckResult
    {
        internal string Status;
        internal string ReleaseTag;
        internal string BundleName;
        internal ReleaseInfo Release;
        internal ReleaseIdentity Candidate;

        internal Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> value = new Dictionary<string, object>();
            value["status"] = Status;
            if (!String.IsNullOrWhiteSpace(ReleaseTag)) value["releaseTag"] = ReleaseTag;
            if (!String.IsNullOrWhiteSpace(BundleName)) value["bundle"] = BundleName;
            return value;
        }
    }

    internal sealed class UpdateInstallResult
    {
        internal string Status;
        internal string ReleaseTag;
        internal string InstallPath;
        internal bool BackupCurrent;
        internal string BackupPath;
        internal bool CleanupScheduled;

        internal Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "status", Status }, { "releaseTag", ReleaseTag }, { "installPath", InstallPath },
                { "backupCurrent", BackupCurrent }, { "backupPath", BackupPath },
                { "cleanupScheduled", CleanupScheduled }
            };
        }
    }

    internal sealed class VersionValidationResult
    {
        internal string ArtifactBase;
        internal int CheckedFiles;
        internal readonly List<string> Issues = new List<string>();
        internal bool IsValid { get { return Issues.Count == 0; } }
    }

    internal sealed class VersionRepairResult
    {
        internal string ArtifactBase;
        internal bool Repaired;
        internal VersionValidationResult Validation;
    }

    internal sealed class VerifiedBundle : IDisposable
    {
        internal string Root;
        internal string ScratchDirectory;
        internal string ZipPath;
        internal string LauncherPath;
        internal Dictionary<string, object> Manifest;
        internal Dictionary<string, object> Report;

        public void Dispose()
        {
            if (String.IsNullOrWhiteSpace(ScratchDirectory) || !Directory.Exists(ScratchDirectory)) return;
            try { LongPathFileSystem.DeleteDirectory(Root, ScratchDirectory); }
            catch { }
            ScratchDirectory = null;
        }
    }

    internal sealed class UpdateService
    {
        private readonly string _root;
        private readonly string _apiUrl;
        private readonly object _stateLock = new object();

        internal UpdateService(string root)
        {
            _root = PathSafety.NormalizeRoot(root);
            _apiUrl = "https://api.github.com/repos/" + LauncherConstants.Repository + "/releases/latest";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        internal UpdateCheckResult Check(CurrentInstall current, bool force)
        {
            Dictionary<string, object> state = LoadState();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!force && !IsCheckDue(state, now))
                return new UpdateCheckResult { Status = "CheckDeferred", ReleaseTag = OptionalText(state, "latestReleaseTag") };
            state["lastAttemptAt"] = now.ToString("o", CultureInfo.InvariantCulture);
            try
            {
                string etag = OptionalText(state, "etag");
                ApiResponse response = RequestLatest(etag);
                ReleaseInfo release;
                if (response.NotModified)
                {
                    Dictionary<string, object> cached = JsonStore.Optional(state, "cachedRelease") as Dictionary<string, object>;
                    if (cached == null) throw new InvalidDataException("GitHub returned 304 without cached release metadata.");
                    release = ReleaseInfo.FromCached(cached);
                }
                else
                {
                    release = NormalizeRelease(response.Json);
                    state["etag"] = response.ETag;
                    state["cachedRelease"] = release.ToJson();
                }
                ReleaseIdentity candidate = ReleaseIdentity.Parse(release.TagName);
                state["latestReleaseTag"] = candidate.ReleaseTag;
                state["lastError"] = null;
                state["lastCheckedAt"] = now.ToString("o", CultureInfo.InvariantCulture);
                SaveState(state);
                if (Versioning.CompareRelease(current, candidate) >= 0)
                    return new UpdateCheckResult { Status = "Current", ReleaseTag = candidate.ReleaseTag, Release = release, Candidate = candidate };
                return new UpdateCheckResult
                {
                    Status = "UpdateAvailable", ReleaseTag = candidate.ReleaseTag,
                    BundleName = candidate.BundleName, Release = release, Candidate = candidate
                };
            }
            catch (Exception error)
            {
                state["lastAttemptAt"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                state["lastError"] = error.Message;
                try { SaveState(state); } catch { }
                LauncherCore.WriteLog(_root, "Update check failed: " + error.Message);
                throw;
            }
        }

        internal UpdateInstallResult Install(CurrentInstall current, UpdateCheckResult check, bool backupCurrent)
        {
            if (check == null || check.Status != "UpdateAvailable" || check.Release == null || check.Candidate == null)
                throw new InvalidOperationException("No verified update candidate is available.");
            using (VerifiedBundle verified = DownloadVerifiedBundle(check.Release, check.Candidate))
            {
                string ready = PathSafety.RequireChild(_root,
                    Path.Combine(_root, ".update-ready-" + check.Candidate.ArtifactBase + "-" + Guid.NewGuid().ToString("N")),
                    "Update staging path");
                try
                {
                    ExtractApplication(_root, verified.ZipPath, ready, verified.Manifest, verified.Report);
                    using (InstallOperationLock operation = InstallOperationLock.Acquire(_root))
                    {
                        CurrentInstall latest = LauncherCore.LoadCurrentStateUnlocked(_root);
                        if (Versioning.CompareRelease(latest, check.Candidate) >= 0)
                            throw new InvalidOperationException("The current installation changed and no longer needs this update.");
                        InstalledPayload installed = ActivatePreparedPayload(verified, check.Candidate, ready);
                        bool cleanup = false;
                        string backupPath = null;
                        if (backupCurrent) backupPath = latest.InstallPath;
                        else
                        {
                            try { cleanup = RegisterPendingCleanup(latest); }
                            catch (Exception error)
                            {
                                LauncherCore.WriteLog(_root, "Could not schedule old version cleanup: " + error.Message);
                            }
                        }
                        UpdateInstallResult result = new UpdateInstallResult
                        {
                            Status = "InstalledForNextLaunch",
                            ReleaseTag = installed.ReleaseTag,
                            InstallPath = installed.Destination,
                            BackupCurrent = backupCurrent,
                            BackupPath = backupPath,
                            CleanupScheduled = cleanup
                        };
                        LauncherCore.WriteLog(_root, result.Status + ": " + result.ReleaseTag + "; backupCurrent=" + backupCurrent);
                        return result;
                    }
                }
                finally
                {
                    if (Directory.Exists(ready))
                    {
                        try { LongPathFileSystem.DeleteDirectory(_root, ready); }
                        catch (Exception error) { LauncherCore.WriteLog(_root, "Could not remove update staging directory: " + error.Message); }
                    }
                }
            }
        }

        internal void CleanupPending(string currentPath)
        {
            string cleanupPath = Path.Combine(_root, LauncherConstants.CleanupFilename);
            if (!File.Exists(cleanupPath)) return;
            using (InstallOperationLock operation = InstallOperationLock.Acquire(_root))
            {
                if (!File.Exists(cleanupPath)) return;
                currentPath = LauncherCore.LoadCurrentStateUnlocked(_root).InstallPath;
                Dictionary<string, object> pending;
                try { pending = JsonStore.ReadObject(cleanupPath); }
                catch (Exception error) { LauncherCore.WriteLog(_root, "Ignoring invalid cleanup state: " + error.Message); return; }
                if (pending == null) return;
                List<object> remaining = new List<object>();
                object entriesValue = JsonStore.Optional(pending, "entries");
                if (entriesValue == null) return;
                foreach (object item in AsArray(entriesValue))
                {
                    Dictionary<string, object> entry = item as Dictionary<string, object>;
                    if (entry == null) continue;
                    string path = JsonStore.OptionalString(entry, "path");
                    string artifact = JsonStore.OptionalString(entry, "artifactBase");
                    try
                    {
                        if (String.IsNullOrWhiteSpace(path) || String.IsNullOrWhiteSpace(artifact) || !LauncherConstants.ArtifactPattern.IsMatch(artifact)) continue;
                        path = PathSafety.RequireDeletionTarget(_root, path, "Cleanup path");
                        if (String.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) || ProcessUsesPath(path)) { remaining.Add(entry); continue; }
                        if (VersionCatalog.Get(_root, artifact).IsPinned) { remaining.Add(entry); continue; }
                        Dictionary<string, object> marker = JsonStore.ReadObject(Path.Combine(path, ".codex-patch-install.json"));
                        if (marker == null || JsonStore.String(marker, "artifactBase") != artifact) { remaining.Add(entry); continue; }
                        if (Directory.Exists(path)) LongPathFileSystem.DeleteDirectory(_root, path);
                        VersionCatalog.Remove(_root, artifact);
                        LauncherCore.WriteLog(_root, "Removed unneeded previous version: " + path);
                    }
                    catch (Exception error)
                    {
                        LauncherCore.WriteLog(_root, "Previous version cleanup deferred: " + error.Message);
                        remaining.Add(entry);
                    }
                }
                if (remaining.Count == 0) { if (File.Exists(cleanupPath)) File.Delete(cleanupPath); }
                else
                {
                    pending["entries"] = remaining.ToArray();
                    JsonStore.WriteAtomic(cleanupPath, pending);
                }
            }
        }

        private bool RegisterPendingCleanup(CurrentInstall current)
        {
            string cleanupPath = Path.Combine(_root, LauncherConstants.CleanupFilename);
            Dictionary<string, object> pending = JsonStore.ReadObject(cleanupPath) ?? new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "entries", new object[0] }
            };
            List<object> entries = new List<object>();
            object existing = JsonStore.Optional(pending, "entries");
            if (existing != null) entries.AddRange(AsArray(existing));
            bool found = entries.OfType<Dictionary<string, object>>().Any(delegate(Dictionary<string, object> value)
            {
                return String.Equals(JsonStore.OptionalString(value, "path"), current.InstallPath, StringComparison.OrdinalIgnoreCase);
            });
            if (!found)
            {
                entries.Add(new Dictionary<string, object>
                {
                    { "path", current.InstallPath }, { "artifactBase", current.ArtifactBase },
                    { "queuedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                });
            }
            pending["entries"] = entries.ToArray();
            JsonStore.WriteAtomic(cleanupPath, pending);
            return true;
        }

        private InstalledPayload ActivatePreparedPayload(VerifiedBundle verified, ReleaseIdentity candidate, string ready)
        {
            string destination = PathSafety.RequireChild(_root, Path.Combine(_root, candidate.ArtifactBase), "Update destination");
            if (Directory.Exists(destination))
                ValidateExistingDestination(_root, destination, verified.Manifest, verified.Report);
            else
                Directory.Move(ready, destination);

            string nativeSource = Path.Combine(destination, LauncherConstants.NativeFilename);
            string reportNativeHash = JsonStore.String(JsonStore.Object(verified.Report, "nativeLauncher"), "sha256");
            if (LauncherCore.Sha256(verified.LauncherPath) != reportNativeHash || LauncherCore.Sha256(nativeSource) != reportNativeHash)
                throw new InvalidDataException("Bundled and versioned native launchers do not match verification evidence.");
            LauncherCore.InstallFileAtomic(verified.LauncherPath, Path.Combine(_root, LauncherConstants.NativeFilename));
            Dictionary<string, object> current = new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "releaseTag", JsonStore.String(verified.Manifest, "releaseTag") },
                { "artifactBase", JsonStore.String(verified.Manifest, "artifactBase") },
                { "msixVersion", JsonStore.String(verified.Manifest, "msixVersion") },
                { "patchVersion", JsonStore.String(verified.Manifest, "patchVersion") },
                { "installPath", destination },
                { "activatedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
            JsonStore.WriteAtomic(Path.Combine(_root, "current.json"), current);
            return new InstalledPayload { Destination = destination, ReleaseTag = JsonStore.String(verified.Manifest, "releaseTag") };
        }

        internal VersionValidationResult ValidateInstalled(InstalledVersion version)
        {
            if (version == null) throw new ArgumentNullException("version");
            string canonical = Path.Combine(_root, version.ArtifactBase);
            InstalledVersion requested = LauncherCore.ReadInstalledVersion(_root, canonical, version.ArtifactBase, true);
            using (VerifiedBundle verified = DownloadVersionBundle(requested))
            using (InstallOperationLock operation = InstallOperationLock.Acquire(_root))
            {
                InstalledVersion refreshed = LauncherCore.ReadInstalledVersion(_root, canonical, version.ArtifactBase, true);
                RequireSameVersion(requested, refreshed);
                VersionValidationResult result = CompareInstalled(refreshed, verified);
                if (result.IsValid && result.CheckedFiles == LauncherConstants.RequiredPayloadFiles.Length)
                    LauncherCore.PersistInstalledIntegrityEvidence(_root, refreshed,
                        JsonStore.Object(JsonStore.Object(verified.Report, "zip"), "verifiedPayloads"), true);
                return result;
            }
        }

        internal VersionRepairResult RepairInstalled(InstalledVersion version, string currentArtifact)
        {
            if (version == null) throw new ArgumentNullException("version");
            string canonical = Path.Combine(_root, version.ArtifactBase);
            InstalledVersion requested = LauncherCore.ReadInstalledVersion(_root, canonical, version.ArtifactBase, true);
            using (VerifiedBundle verified = DownloadVersionBundle(requested))
            {
                string ready = PathSafety.RequireChild(_root,
                    Path.Combine(_root, ".repair-ready-" + requested.ArtifactBase + "-" + Guid.NewGuid().ToString("N")), "Repair staging path");
                string backup = PathSafety.RequireChild(_root,
                    Path.Combine(_root, ".repair-backup-" + requested.ArtifactBase + "-" + Guid.NewGuid().ToString("N")), "Repair backup path");
                string repairStatePath = Path.Combine(_root, LauncherConstants.RepairStateFilename);
                try
                {
                    ExtractApplication(_root, verified.ZipPath, ready, verified.Manifest, verified.Report);
                    InstalledVersion staged = new InstalledVersion
                    {
                        ArtifactBase = requested.ArtifactBase, ReleaseTag = requested.ReleaseTag,
                        MsixVersion = requested.MsixVersion, PatchVersion = requested.PatchVersion,
                        InstallPath = ready, Note = requested.Note, IsPinned = requested.IsPinned
                    };
                    VersionValidationResult validation = CompareInstalled(staged, verified);
                    if (!validation.IsValid || validation.CheckedFiles != LauncherConstants.RequiredPayloadFiles.Length)
                        throw new InvalidDataException("修复候选目录的关键文件校验失败：" + String.Join("；", validation.Issues.ToArray()));

                    VersionRepairResult result;
                    using (InstallOperationLock operation = InstallOperationLock.Acquire(_root))
                    {
                        InstalledVersion refreshed = RequireRepairableTargetUnlocked(_root, requested);
                        string destination = PathSafety.RequireChild(_root, refreshed.InstallPath, "Repair destination");
                        if (File.Exists(repairStatePath))
                            throw new InvalidOperationException("存在尚未恢复的版本修复事务，请重新启动管理器后再试。");

                        try
                        {
                            JsonStore.WriteAtomic(repairStatePath, new Dictionary<string, object>
                            {
                                { "schemaVersion", 1 }, { "artifactBase", refreshed.ArtifactBase },
                                { "destination", destination }, { "ready", ready }, { "backup", backup },
                                { "createdAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                            });
                            Directory.Move(destination, backup);
                            Directory.Move(ready, destination);
                        }
                        catch
                        {
                            try { RecoverPendingRepairUnlocked(_root); }
                            catch (Exception error) { LauncherCore.WriteLog(_root, "Could not recover interrupted repair: " + error.Message); }
                            throw;
                        }

                        if (File.Exists(repairStatePath)) File.Delete(repairStatePath);
                        result = new VersionRepairResult
                        {
                            ArtifactBase = refreshed.ArtifactBase, Repaired = true, Validation = validation
                        };
                    }
                    try
                    {
                        if (Directory.Exists(backup)) LongPathFileSystem.DeleteDirectory(_root, backup);
                    }
                    catch (Exception error) { LauncherCore.WriteLog(_root, "Could not remove repair backup directory: " + error.Message); }
                    LauncherCore.WriteLog(_root, "Repaired installed version: " + result.ArtifactBase);
                    return result;
                }
                finally
                {
                    if (Directory.Exists(ready))
                    {
                        try { LongPathFileSystem.DeleteDirectory(_root, ready); }
                        catch (Exception error) { LauncherCore.WriteLog(_root, "Could not remove repair staging directory: " + error.Message); }
                    }
                }
            }
        }

        internal static void RecoverPendingRepair(string root)
        {
            root = PathSafety.NormalizeRoot(root);
            if (!File.Exists(Path.Combine(root, LauncherConstants.RepairStateFilename))) return;
            using (InstallOperationLock operation = InstallOperationLock.Acquire(root, 30000)) RecoverPendingRepairUnlocked(root);
        }

        private static void RequireSameVersion(InstalledVersion expected, InstalledVersion actual)
        {
            if (expected.ArtifactBase != actual.ArtifactBase || expected.ReleaseTag != actual.ReleaseTag ||
                expected.MsixVersion != actual.MsixVersion || expected.PatchVersion != actual.PatchVersion)
                throw new InvalidOperationException("The installed version changed while its release bundle was being prepared.");
        }

        internal static InstalledVersion RequireRepairableTargetUnlocked(string root, InstalledVersion requested)
        {
            if (requested == null) throw new ArgumentNullException("requested");
            root = PathSafety.NormalizeRoot(root);
            CurrentInstall latest = LauncherCore.LoadCurrentStateUnlocked(root);
            if (String.Equals(requested.ArtifactBase, latest.ArtifactBase, StringComparison.Ordinal))
                throw new InvalidOperationException("当前版本正在承载启动器，请先回退或切换到其他版本后再修复。");
            string canonical = Path.Combine(root, requested.ArtifactBase);
            InstalledVersion refreshed = LauncherCore.ReadInstalledVersion(root, canonical, requested.ArtifactBase, true);
            RequireSameVersion(requested, refreshed);
            if (ProcessUsesPath(refreshed.InstallPath))
                throw new InvalidOperationException("该版本仍有进程正在运行，无法修复。");
            return refreshed;
        }

        private static void RecoverPendingRepairUnlocked(string root)
        {
            root = PathSafety.NormalizeRoot(root);
            string statePath = Path.Combine(root, LauncherConstants.RepairStateFilename);
            Dictionary<string, object> state = JsonStore.ReadObject(statePath);
            if (state == null) return;
            if (JsonStore.Integer(state, "schemaVersion") != 1)
                throw new InvalidDataException("Unsupported pending repair state schema.");
            string artifact = JsonStore.String(state, "artifactBase");
            if (!LauncherConstants.ArtifactPattern.IsMatch(artifact))
                throw new InvalidDataException("Pending repair artifact name is invalid.");
            string destination = PathSafety.RequireChild(root, JsonStore.String(state, "destination"), "Repair destination");
            if (!String.Equals(destination, Path.Combine(root, artifact), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pending repair destination does not match its artifact.");
            string ready = ValidateRepairAuxiliaryPath(root, JsonStore.String(state, "ready"), ".repair-ready-" + artifact + "-", "Repair staging path");
            string backup = ValidateRepairAuxiliaryPath(root, JsonStore.String(state, "backup"), ".repair-backup-" + artifact + "-", "Repair backup path");

            bool destinationValid = IsRecoverableRepairDirectory(root, destination, artifact, true);
            bool backupValid = IsRecoverableRepairDirectory(root, backup, artifact, false);
            bool readyValid = IsRecoverableRepairDirectory(root, ready, artifact, false);
            if (backupValid)
            {
                if (Directory.Exists(destination)) LongPathFileSystem.DeleteDirectory(root, destination);
                Directory.Move(backup, destination);
                destinationValid = true;
            }
            else if (!destinationValid)
            {
                if (!readyValid) throw new InvalidDataException("Pending repair has no complete directory to restore.");
                if (Directory.Exists(destination)) LongPathFileSystem.DeleteDirectory(root, destination);
                Directory.Move(ready, destination);
                destinationValid = true;
            }

            if (Directory.Exists(ready))
            {
                try { LongPathFileSystem.DeleteDirectory(root, ready); }
                catch (Exception error) { LauncherCore.WriteLog(root, "Could not remove recovered repair staging directory: " + error.Message); }
            }
            if (Directory.Exists(backup))
            {
                try { LongPathFileSystem.DeleteDirectory(root, backup); }
                catch (Exception error) { LauncherCore.WriteLog(root, "Could not remove recovered repair backup directory: " + error.Message); }
            }
            File.Delete(statePath);
            LauncherCore.WriteLog(root, "Recovered interrupted repair transaction: " + artifact);
        }

        private static bool IsRecoverableRepairDirectory(string root, string path, string artifact, bool canonical)
        {
            if (!Directory.Exists(path)) return false;
            try
            {
                InstalledVersion version = LauncherCore.ReadInstalledVersion(root, path, artifact, canonical);
                LauncherCore.RequireInstalledIntegrity(root, version, canonical);
                return true;
            }
            catch { return false; }
        }

        private static string ValidateRepairAuxiliaryPath(string root, string path, string prefix, string label)
        {
            path = PathSafety.RequireChild(root, path, label);
            string name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Regex.IsMatch(name.Substring(prefix.Length), @"^[0-9a-f]{32}$"))
                throw new InvalidDataException(label + " has an invalid name: " + path);
            return path;
        }

        private VerifiedBundle DownloadVersionBundle(InstalledVersion version)
        {
            ReleaseIdentity identity = ReleaseIdentity.Parse(version.ReleaseTag);
            if (identity.ArtifactBase != version.ArtifactBase || identity.MsixVersion != version.MsixVersion || identity.PatchVersion != version.PatchVersion)
                throw new InvalidDataException("安装版本标记与 release tag 不一致。");
            ReleaseInfo release = RequestReleaseByTag(version.ReleaseTag);
            return DownloadVerifiedBundle(release, identity);
        }

        private VerifiedBundle DownloadVerifiedBundle(ReleaseInfo release, ReleaseIdentity candidate)
        {
            if (release.Assets.Count != 1) throw new InvalidDataException("Stable releases must contain exactly one bundle asset.");
            ReleaseAsset bundle = release.Assets.SingleOrDefault(delegate(ReleaseAsset value) { return value.Name == candidate.BundleName; });
            if (bundle == null) throw new InvalidDataException("Release bundle asset is missing: " + candidate.BundleName);
            if (!Regex.IsMatch(bundle.Digest ?? String.Empty, @"^sha256:[0-9a-f]{64}$"))
                throw new InvalidDataException("GitHub did not provide a valid SHA-256 bundle digest.");
            string downloadRoot = Path.Combine(_root, ".downloads");
            Directory.CreateDirectory(downloadRoot);
            VerifiedBundle verified = new VerifiedBundle
            {
                Root = _root,
                ScratchDirectory = PathSafety.RequireChild(_root,
                    Path.Combine(downloadRoot, candidate.ArtifactBase + "-" + Guid.NewGuid().ToString("N")), "Download scratch path")
            };
            Directory.CreateDirectory(verified.ScratchDirectory);
            try
            {
                string bundlePath = Path.Combine(verified.ScratchDirectory, candidate.BundleName);
                Download(bundle.Url, bundlePath, bundle.Size);
                if (!String.Equals(bundle.Digest, "sha256:" + LauncherCore.Sha256(bundlePath), StringComparison.Ordinal))
                    throw new InvalidDataException("Downloaded bundle digest does not match GitHub metadata.");
                string bundleDirectory = Path.Combine(verified.ScratchDirectory, "bundle");
                ExtractZip(bundlePath, bundleDirectory);
                verified.Manifest = JsonStore.ReadObject(Path.Combine(bundleDirectory, LauncherConstants.ManifestFilename));
                ValidateManifest(verified.Manifest, release.TagName);
                if (JsonStore.String(verified.Manifest, "artifactBase") != candidate.ArtifactBase)
                    throw new InvalidDataException("Manifest and GitHub release identity disagree.");
                Dictionary<string, object> assets = JsonStore.Object(verified.Manifest, "assets");
                verified.ZipPath = VerifiedAsset(JsonStore.Object(assets, "zip"), bundleDirectory);
                string checksumPath = VerifiedAsset(JsonStore.Object(assets, "checksum"), bundleDirectory);
                string reportPath = VerifiedAsset(JsonStore.Object(assets, "verification"), bundleDirectory);
                verified.LauncherPath = VerifiedAsset(JsonStore.Object(assets, "launcher"), bundleDirectory);
                ValidateChecksum(checksumPath, JsonStore.Object(assets, "zip"));
                verified.Report = ValidateReport(reportPath, verified.Manifest);
                return verified;
            }
            catch { verified.Dispose(); throw; }
        }

        internal static VersionValidationResult CompareInstalled(InstalledVersion version, VerifiedBundle verified)
        {
            VersionValidationResult result = new VersionValidationResult { ArtifactBase = version.ArtifactBase };
            try
            {
                Dictionary<string, object> marker = JsonStore.ReadObject(Path.Combine(version.InstallPath, ".codex-patch-install.json"));
                if (marker == null || JsonStore.Integer(marker, "schemaVersion") != 1 ||
                    JsonStore.String(marker, "artifactBase") != version.ArtifactBase ||
                    JsonStore.String(marker, "releaseTag") != version.ReleaseTag ||
                    JsonStore.String(marker, "msixVersion") != version.MsixVersion ||
                    JsonStore.String(marker, "patchVersion") != version.PatchVersion ||
                    JsonStore.String(marker, "zipSha256") != JsonStore.String(JsonStore.Object(JsonStore.Object(verified.Manifest, "assets"), "zip"), "sha256"))
                    result.Issues.Add("安装标记与 release 不一致");
            }
            catch (Exception error) { result.Issues.Add("安装标记无效：" + error.Message); }
            Dictionary<string, object> payloads = JsonStore.Object(JsonStore.Object(verified.Report, "zip"), "verifiedPayloads");
            foreach (KeyValuePair<string, object> item in payloads)
            {
                result.CheckedFiles++;
                string path;
                try { path = PathSafety.SafeArchivePath(version.InstallPath, item.Key); }
                catch (Exception error) { result.Issues.Add(item.Key + " 路径无效：" + error.Message); continue; }
                if (!File.Exists(path)) { result.Issues.Add(item.Key + " 缺失"); continue; }
                string expected = Convert.ToString(item.Value, CultureInfo.InvariantCulture);
                if (!Regex.IsMatch(expected ?? String.Empty, @"^[0-9a-f]{64}$") || LauncherCore.Sha256(path) != expected)
                    result.Issues.Add(item.Key + " 哈希不匹配");
            }
            return result;
        }

        private ReleaseInfo RequestReleaseByTag(string releaseTag)
        {
            ReleaseIdentity.Parse(releaseTag);
            Uri uri = TrustedUri("https://api.github.com/repos/" + LauncherConstants.Repository +
                "/releases/tags/" + Uri.EscapeDataString(releaseTag), UriKindType.Api);
            HttpWebRequest request = CreateRequest(uri);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("GitHub API returned HTTP " + (int)response.StatusCode + ".");
                string body = ReadLimited(response, 2 * 1024 * 1024);
                Dictionary<string, object> json = new System.Web.Script.Serialization.JavaScriptSerializer
                {
                    MaxJsonLength = 2 * 1024 * 1024
                }.DeserializeObject(body) as Dictionary<string, object>;
                if (json == null) throw new InvalidDataException("GitHub API returned invalid JSON.");
                ReleaseInfo release = NormalizeRelease(json);
                if (release.TagName != releaseTag) throw new InvalidDataException("GitHub release tag mismatch.");
                return release;
            }
        }

        internal static void ExtractApplication(string root, string zipPath, string destination, Dictionary<string, object> manifest, Dictionary<string, object> report)
        {
            string staging = PathSafety.RequireChild(root,
                Path.Combine(root, "." + JsonStore.String(manifest, "artifactBase") + ".extracting-" + Guid.NewGuid().ToString("N")),
                "Extraction staging path");
            bool moved = false;
            try
            {
                PrepareApplication(root, zipPath, staging, manifest, report);
                Directory.Move(staging, destination);
                moved = true;
            }
            finally
            {
                if (!moved && Directory.Exists(staging)) LongPathFileSystem.DeleteDirectory(root, staging);
            }
        }

        internal static void PrepareApplication(string root, string zipPath, string staging,
            Dictionary<string, object> manifest, Dictionary<string, object> report)
        {
            staging = PathSafety.RequireChild(root, staging, "Extraction staging path");
            if (Directory.Exists(staging)) throw new IOException("Extraction staging directory already exists: " + staging);
            bool prepared = false;
            try
            {
                ExtractZip(zipPath, staging);
                foreach (string relative in LauncherConstants.RequiredPayloadFiles)
                    if (!File.Exists(Path.Combine(staging, relative))) throw new InvalidDataException("Downloaded update is missing " + relative + ".");
                Dictionary<string, object> native = JsonStore.Object(report, "nativeLauncher");
                string expectedNativeHash = JsonStore.String(native, "sha256");
                string actualNativeHash = LauncherCore.Sha256(Path.Combine(staging, LauncherConstants.NativeFilename));
                if (!String.Equals(expectedNativeHash, actualNativeHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Native launcher hash does not match verification evidence.");
                Dictionary<string, object> zipDefinition = JsonStore.Object(JsonStore.Object(manifest, "assets"), "zip");
                Dictionary<string, object> verifiedPayloads = JsonStore.Object(JsonStore.Object(report, "zip"), "verifiedPayloads");
                Dictionary<string, string> normalizedPayloads = LauncherCore.NormalizeVerifiedPayloads(
                    verifiedPayloads, "Verification report");
                Dictionary<string, object> marker = new Dictionary<string, object>
                {
                    { "schemaVersion", 1 }, { "releaseTag", JsonStore.String(manifest, "releaseTag") },
                    { "artifactBase", JsonStore.String(manifest, "artifactBase") },
                    { "msixVersion", JsonStore.String(manifest, "msixVersion") },
                    { "patchVersion", JsonStore.String(manifest, "patchVersion") },
                    { "zipSha256", JsonStore.String(zipDefinition, "sha256") },
                    { "verifiedPayloads", normalizedPayloads.ToDictionary(
                        delegate(KeyValuePair<string, string> item) { return item.Key; },
                        delegate(KeyValuePair<string, string> item) { return (object)item.Value; }, StringComparer.Ordinal) },
                    { "installedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                };
                JsonStore.WriteAtomic(Path.Combine(staging, ".codex-patch-install.json"), marker);
                InstalledVersion staged = new InstalledVersion
                {
                    ArtifactBase = JsonStore.String(manifest, "artifactBase"),
                    ReleaseTag = JsonStore.String(manifest, "releaseTag"),
                    MsixVersion = JsonStore.String(manifest, "msixVersion"),
                    PatchVersion = JsonStore.String(manifest, "patchVersion"),
                    InstallPath = staging
                };
                LauncherCore.RequireInstalledIntegrity(root, staged, false);
                prepared = true;
            }
            finally
            {
                if (!prepared && Directory.Exists(staging)) LongPathFileSystem.DeleteDirectory(root, staging);
            }
        }

        internal static void ValidateExistingDestination(string root, string destination,
            Dictionary<string, object> manifest, Dictionary<string, object> report)
        {
            string artifact = JsonStore.String(manifest, "artifactBase");
            InstalledVersion version = LauncherCore.ReadInstalledVersion(root, destination, artifact, true);
            if (version.ReleaseTag != JsonStore.String(manifest, "releaseTag") ||
                version.MsixVersion != JsonStore.String(manifest, "msixVersion") ||
                version.PatchVersion != JsonStore.String(manifest, "patchVersion"))
                throw new InvalidDataException("Existing update destination identity does not match the verified release: " + destination);
            VersionValidationResult validation = CompareInstalled(version, new VerifiedBundle { Manifest = manifest, Report = report });
            if (!validation.IsValid || validation.CheckedFiles != LauncherConstants.RequiredPayloadFiles.Length)
                throw new InvalidDataException("Existing update destination failed critical-file verification: " +
                    String.Join("; ", validation.Issues.ToArray()));
            LauncherCore.PersistInstalledIntegrityEvidence(root, version,
                JsonStore.Object(JsonStore.Object(report, "zip"), "verifiedPayloads"), true);
        }

        internal static void ValidateManifest(Dictionary<string, object> manifest, string releaseTag)
        {
            if (manifest == null) throw new InvalidDataException("Update manifest is missing.");
            long schema = JsonStore.Integer(manifest, "schemaVersion");
            if (schema != LauncherConstants.UpdateSchemaVersion) throw new InvalidDataException("Unsupported update manifest schema.");
            if (JsonStore.String(manifest, "channel") != "stable") throw new InvalidDataException("Update manifest channel mismatch.");
            ReleaseIdentity.Parse(releaseTag);
            if (JsonStore.String(manifest, "repository") != LauncherConstants.Repository)
                throw new InvalidDataException("Update manifest repository mismatch.");
            string msix = JsonStore.String(manifest, "msixVersion");
            string patch = JsonStore.String(manifest, "patchVersion");
            string launcher = JsonStore.String(manifest, "launcherVersion");
            string minimum = JsonStore.String(manifest, "minimumLauncherVersion");
            Versioning.Compare(msix, "0.0.0"); Versioning.Compare(patch, "0.0.0");
            if (Versioning.Compare(launcher, minimum) < 0)
                throw new InvalidDataException("Update manifest launcher version is below its minimum version.");
            if (Versioning.Compare(LauncherConstants.Version, minimum) < 0)
                throw new InvalidDataException("This launcher is too old for the update manifest. Required: " + minimum +
                    "; current: " + LauncherConstants.Version + ".");
            string artifact = "CX-" + msix + "-p" + patch;
            if (JsonStore.String(manifest, "artifactBase") != artifact || JsonStore.String(manifest, "releaseTag") != releaseTag)
                throw new InvalidDataException("Update manifest release identity mismatch.");
            DateTimeOffset.Parse(JsonStore.String(manifest, "publishedAt"), CultureInfo.InvariantCulture);
            Dictionary<string, object> assets = JsonStore.Object(manifest, "assets");
            Dictionary<string, string> expected = new Dictionary<string, string>
            {
                { "zip", artifact + ".zip" }, { "checksum", artifact + ".zip.sha256" },
                { "verification", artifact + ".verification.json" },
                { "launcher", LauncherConstants.NativeFilename }
            };
            foreach (KeyValuePair<string, string> item in expected)
            {
                Dictionary<string, object> asset = JsonStore.Object(assets, item.Key);
                if (JsonStore.String(asset, "name") != item.Value || JsonStore.Integer(asset, "size") <= 0 ||
                    !Regex.IsMatch(JsonStore.String(asset, "sha256"), @"^[0-9a-f]{64}$"))
                    throw new InvalidDataException("Update manifest asset metadata is invalid: " + item.Key);
            }
        }

        internal static Dictionary<string, object> ValidateReport(string path, Dictionary<string, object> manifest)
        {
            Dictionary<string, object> report = JsonStore.ReadObject(path);
            if (report == null) throw new InvalidDataException("Verification report is missing.");
            if (JsonStore.String(report, "patchVersion") != JsonStore.String(manifest, "patchVersion"))
                throw new InvalidDataException("Verification report patch version mismatch.");
            Dictionary<string, object> upstream = JsonStore.Object(report, "upstream");
            if (JsonStore.String(upstream, "version") != JsonStore.String(manifest, "msixVersion") ||
                JsonStore.String(JsonStore.Object(upstream, "signature"), "status") != "Valid")
                throw new InvalidDataException("Verification report upstream evidence is invalid.");
            Dictionary<string, object> patch = JsonStore.Object(JsonStore.Object(report, "asar"), "patch");
            if (JsonStore.Integer(patch, "totalInternalPowerShellEliminatedTargets") != 7)
                throw new InvalidDataException("Verification report patch target evidence is invalid.");
            Dictionary<string, object> zip = JsonStore.Object(report, "zip");
            string expectedZip = JsonStore.String(JsonStore.Object(JsonStore.Object(manifest, "assets"), "zip"), "sha256");
            if (JsonStore.String(zip, "sha256") != expectedZip) throw new InvalidDataException("Verification report ZIP hash mismatch.");
            Dictionary<string, object> payloads = JsonStore.Object(zip, "verifiedPayloads");
            Dictionary<string, string> normalizedPayloads = LauncherCore.NormalizeVerifiedPayloads(payloads, "Verification report");
            if (Versioning.Compare(JsonStore.String(manifest, "launcherVersion"), "1.0.0") >= 0)
            {
                Dictionary<string, object> sidecar = JsonStore.Object(report, "integritySidecar");
                if (JsonStore.String(sidecar, "file") != LauncherConstants.IntegrityFilename ||
                    JsonStore.Integer(sidecar, "schemaVersion") != 1 ||
                    JsonStore.String(sidecar, "releaseTag") != JsonStore.String(manifest, "releaseTag") ||
                    JsonStore.String(sidecar, "artifactBase") != JsonStore.String(manifest, "artifactBase") ||
                    JsonStore.String(sidecar, "msixVersion") != JsonStore.String(manifest, "msixVersion") ||
                    JsonStore.String(sidecar, "patchVersion") != JsonStore.String(manifest, "patchVersion"))
                    throw new InvalidDataException("Verification report integrity sidecar identity is invalid.");
                Dictionary<string, string> sidecarPayloads = LauncherCore.NormalizeVerifiedPayloads(
                    JsonStore.Object(sidecar, "verifiedPayloads"), "Verification report integrity sidecar");
                if (sidecarPayloads.Any(delegate(KeyValuePair<string, string> item)
                    { return normalizedPayloads[item.Key] != item.Value; }))
                    throw new InvalidDataException("Verification report integrity sidecar payloads do not match ZIP evidence.");
            }
            Dictionary<string, object> native = JsonStore.Object(report, "nativeLauncher");
            if (JsonStore.String(native, "file") != LauncherConstants.NativeFilename ||
                JsonStore.String(native, "version") != JsonStore.String(manifest, "launcherVersion") ||
                !Regex.IsMatch(JsonStore.String(native, "sha256"), @"^[0-9a-f]{64}$"))
                throw new InvalidDataException("Verification report native launcher evidence is invalid.");
            return report;
        }

        internal static void ValidateChecksum(string path, Dictionary<string, object> zip)
        {
            Match match = Regex.Match(File.ReadAllText(path).Trim(), @"(?i)^([0-9a-f]{64})\s+\*?([0-9A-Za-z._-]+)$");
            if (!match.Success || match.Groups[1].Value.ToLowerInvariant() != JsonStore.String(zip, "sha256") ||
                match.Groups[2].Value != JsonStore.String(zip, "name"))
                throw new InvalidDataException("Checksum sidecar does not match the update manifest.");
        }

        internal static string VerifiedAsset(Dictionary<string, object> definition, string directory)
        {
            string name = JsonStore.String(definition, "name");
            if (!Regex.IsMatch(name, @"^[0-9A-Za-z._-]+$") || JsonStore.Integer(definition, "size") <= 0 ||
                !Regex.IsMatch(JsonStore.String(definition, "sha256"), @"^[0-9a-f]{64}$"))
                throw new InvalidDataException("Manifest contains invalid asset metadata: " + name);
            string path = Path.Combine(directory, name);
            if (!File.Exists(path) || new FileInfo(path).Length != JsonStore.Integer(definition, "size") ||
                LauncherCore.Sha256(path) != JsonStore.String(definition, "sha256"))
                throw new InvalidDataException("Release bundle verification failed for " + name + ".");
            return path;
        }

        private ApiResponse RequestLatest(string etag)
        {
            Uri uri = TrustedUri(_apiUrl, UriKindType.Api);
            HttpWebRequest request = CreateRequest(uri);
            if (!String.IsNullOrWhiteSpace(etag)) request.Headers[HttpRequestHeader.IfNoneMatch] = etag;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.NotModified) return new ApiResponse { NotModified = true, ETag = etag };
                    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("GitHub API returned HTTP " + (int)response.StatusCode + ".");
                    string body = ReadLimited(response, 2 * 1024 * 1024);
                    Dictionary<string, object> json = new System.Web.Script.Serialization.JavaScriptSerializer
                    {
                        MaxJsonLength = 2 * 1024 * 1024
                    }.DeserializeObject(body) as Dictionary<string, object>;
                    if (json == null) throw new InvalidDataException("GitHub API returned invalid JSON.");
                    return new ApiResponse { Json = json, ETag = response.Headers[HttpResponseHeader.ETag] };
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotModified)
                {
                    response.Dispose();
                    return new ApiResponse { NotModified = true, ETag = etag };
                }
                if (response != null) response.Dispose();
                throw;
            }
        }

        private static ReleaseInfo NormalizeRelease(Dictionary<string, object> json)
        {
            if (Convert.ToBoolean(JsonStore.Required(json, "draft"), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(JsonStore.Required(json, "prerelease"), CultureInfo.InvariantCulture))
                throw new InvalidDataException("Latest GitHub release is not stable.");
            ReleaseInfo result = new ReleaseInfo();
            result.TagName = JsonStore.String(json, "tag_name");
            ReleaseIdentity.Parse(result.TagName);
            foreach (object item in AsArray(JsonStore.Required(json, "assets")))
            {
                Dictionary<string, object> value = item as Dictionary<string, object>;
                if (value == null) throw new InvalidDataException("GitHub release asset is invalid.");
                string name = JsonStore.String(value, "name");
                string url = JsonStore.String(value, "browser_download_url");
                long size = JsonStore.Integer(value, "size");
                string digest = JsonStore.OptionalString(value, "digest");
                if (!Regex.IsMatch(name, @"^[0-9A-Za-z._-]+$") || size <= 0) throw new InvalidDataException("GitHub release asset metadata is invalid.");
                TrustedUri(url, UriKindType.Asset);
                if (!String.IsNullOrWhiteSpace(digest) && !Regex.IsMatch(digest, @"^sha256:[0-9a-f]{64}$"))
                    throw new InvalidDataException("GitHub release asset digest is invalid.");
                result.Assets.Add(new ReleaseAsset { Name = name, Url = url, Size = size, Digest = digest });
            }
            if (result.Assets.Count == 0) throw new InvalidDataException("GitHub release contains no assets.");
            return result;
        }

        private static void Download(string url, string destination, long expectedSize)
        {
            Uri uri = TrustedUri(url, UriKindType.Asset);
            for (int redirect = 0; redirect <= 8; redirect++)
            {
                HttpWebRequest request = CreateRequest(uri);
                HttpWebResponse response = null;
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        string location = response.Headers[HttpResponseHeader.Location];
                        if (String.IsNullOrWhiteSpace(location)) throw new InvalidDataException("Download redirect has no Location header.");
                        uri = TrustedUri(new Uri(uri, location).AbsoluteUri, UriKindType.Redirect);
                        continue;
                    }
                    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Asset download returned HTTP " + status + ".");
                    if (response.ContentLength >= 0 && response.ContentLength != expectedSize)
                        throw new InvalidDataException("Asset size differs from GitHub metadata.");
                    long total = 0;
                    using (Stream input = response.GetResponseStream())
                    using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read;
                            if (total > expectedSize) throw new InvalidDataException("Downloaded asset exceeded its expected size.");
                            output.Write(buffer, 0, read);
                        }
                    }
                    if (total != expectedSize) throw new InvalidDataException("Downloaded asset size mismatch.");
                    return;
                }
                finally { if (response != null) response.Dispose(); }
            }
            throw new InvalidDataException("Asset download exceeded the redirect limit.");
        }

        internal static void ExtractZip(string archive, string destination)
        {
            long total = 0;
            int count = 0;
            using (FileStream stream = File.OpenRead(archive))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    count++;
                    if (count > 200000) throw new InvalidDataException("ZIP contains too many entries.");
                    total += entry.Length;
                    if (total > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIP expanded size exceeds the safety limit.");
                    PathSafety.SafeArchivePath(destination, entry.FullName);
                }
            }
            if (count == 0) throw new InvalidDataException("ZIP contains no entries.");
            Directory.CreateDirectory(destination);
            string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (!File.Exists(tar)) throw new FileNotFoundException("Windows tar.exe was not found.", tar);
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = tar,
                Arguments = LauncherCore.JoinArguments(new[] { "-xf", Path.GetFullPath(archive), "-C", Path.GetFullPath(destination) }),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (Process process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("Windows did not start tar.exe.");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidDataException("tar.exe could not extract the ZIP (exit " + process.ExitCode + "): " +
                        (String.IsNullOrWhiteSpace(error) ? output : error).Trim());
            }
        }

        private static Uri TrustedUri(string value, UriKindType kind)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("Untrusted update URL: " + value);
            string host = uri.DnsSafeHost.ToLowerInvariant();
            if (kind == UriKindType.Api && host != "api.github.com") throw new InvalidDataException("Untrusted GitHub API host: " + host);
            if (kind == UriKindType.Asset)
            {
                string currentPrefix = "/" + LauncherConstants.Repository + "/releases/download/";
                if (host != "github.com" ||
                    !uri.AbsolutePath.StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Untrusted release asset URL: " + value);
            }
            if (kind == UriKindType.Redirect)
            {
                string[] allowed = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com" };
                if (!allowed.Contains(host)) throw new InvalidDataException("Untrusted download redirect host: " + host);
            }
            return uri;
        }

        private static HttpWebRequest CreateRequest(Uri uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "CodexDesktopPatchNativeLauncher/" + LauncherConstants.Version;
            request.Accept = "application/vnd.github+json";
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            return request;
        }

        private static string ReadLimited(HttpWebResponse response, int limit)
        {
            if (response.ContentLength > limit) throw new InvalidDataException("HTTP response exceeds the safety limit.");
            using (Stream input = response.GetResponseStream())
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > limit) throw new InvalidDataException("HTTP response exceeds the safety limit.");
                    output.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private Dictionary<string, object> LoadState()
        {
            try
            {
                Dictionary<string, object> saved = JsonStore.ReadObject(Path.Combine(_root, "updater-state.json"));
                if (saved != null) return saved;
            }
            catch (Exception error) { LauncherCore.WriteLog(_root, "Ignoring invalid updater state: " + error.Message); }
            return new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "lastAttemptAt", null }, { "lastCheckedAt", null },
                { "latestReleaseTag", null }, { "etag", null }, { "cachedRelease", null }, { "lastError", null }
            };
        }

        private void SaveState(Dictionary<string, object> state)
        {
            lock (_stateLock) JsonStore.WriteAtomic(Path.Combine(_root, "updater-state.json"), state);
        }

        private static bool IsCheckDue(Dictionary<string, object> state, DateTimeOffset now)
        {
            string error = OptionalText(state, "lastError");
            string attempt = OptionalText(state, "lastAttemptAt");
            if (!String.IsNullOrWhiteSpace(error) && !String.IsNullOrWhiteSpace(attempt) &&
                now - DateTimeOffset.Parse(attempt, CultureInfo.InvariantCulture) < LauncherConstants.FailureRetryInterval) return false;
            string checkedAt = OptionalText(state, "lastCheckedAt");
            if (!String.IsNullOrWhiteSpace(checkedAt) &&
                now - DateTimeOffset.Parse(checkedAt, CultureInfo.InvariantCulture) < LauncherConstants.CheckInterval) return false;
            return true;
        }

        private static string OptionalText(Dictionary<string, object> value, string name)
        {
            object result = JsonStore.Optional(value, name);
            return result == null ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
        }

        internal static object[] AsArray(object value)
        {
            object[] array = value as object[];
            if (array != null) return array;
            System.Collections.ArrayList list = value as System.Collections.ArrayList;
            if (list != null) return list.ToArray();
            throw new InvalidDataException("JSON property must be an array.");
        }

        internal static bool ProcessUsesPath(string path)
        {
            string prefix = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string executable = process.MainModule.FileName;
                    if (executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }

        private sealed class ApiResponse
        {
            internal bool NotModified;
            internal string ETag;
            internal Dictionary<string, object> Json;
        }

        private sealed class InstalledPayload
        {
            internal string Destination;
            internal string ReleaseTag;
        }

        private enum UriKindType { Api, Asset, Redirect }

    }
}
