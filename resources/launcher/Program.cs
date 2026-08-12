using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;

[assembly: AssemblyTitle("Codex Desktop Patch Launcher")]
[assembly: AssemblyDescription("Native WPF launcher for Codex Windows Desktop Patch")]
[assembly: AssemblyCompany("Codex Windows Desktop Patch")]
[assembly: AssemblyProduct("Codex Desktop Patch Launcher")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace CodexPatch.NativeLauncher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] rawArguments)
        {
            LauncherArguments arguments = null;
            try
            {
                arguments = LauncherArguments.Parse(rawArguments);
                RuntimePrerequisites.EnsureSupported();
                if (arguments.SelfTest) return RunSelfTest();
                string executableDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                bool versionDirectory = File.Exists(Path.Combine(executableDirectory, "ChatGPT.exe"));
                if (arguments.NativeHost || versionDirectory)
                {
                    string root = arguments.InstallRoot;
                    if (String.IsNullOrWhiteSpace(root))
                    {
                        DirectoryInfo parent = Directory.GetParent(executableDirectory);
                        if (parent == null) throw new InvalidOperationException("Could not infer the install root.");
                        root = parent.FullName;
                    }
                    return RunHost(PathSafety.NormalizeRoot(root), arguments);
                }
                string installRoot = PathSafety.NormalizeRoot(
                    String.IsNullOrWhiteSpace(arguments.InstallRoot) ? executableDirectory : arguments.InstallRoot);
                return RunBootstrap(installRoot, arguments);
            }
            catch (Exception error)
            {
                WriteError(error.Message);
                bool interactiveLaunch = arguments == null
                    ? rawArguments == null || rawArguments.Length == 0
                    : !arguments.HasCommandMode;
                if (Environment.UserInteractive && interactiveLaunch)
                {
                    try { MessageBox.Show(error.Message, "Codex Desktop Patch 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
                    catch { }
                }
                return 1;
            }
        }

        private static int RunBootstrap(string root, LauncherArguments arguments)
        {
            UpdateService.RecoverPendingRepair(root);
            string executableDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            if (arguments.InstallOnly)
            {
                if (!BundleInstaller.IsBundleDirectory(executableDirectory))
                    throw new InvalidOperationException("InstallOnly 必须从完整解压的 release bundle 目录运行。");
                return RunUnattendedInstaller(executableDirectory, arguments);
            }
            CurrentInstall current;
            try { current = ResolveCurrentState(root); }
            catch (InvalidOperationException)
            {
                if (!BundleInstaller.IsBundleDirectory(executableDirectory)) throw;
                if (arguments.HasCommandMode) throw;
                return RunInstaller(executableDirectory);
            }
            Process child = LauncherCore.LaunchCurrentHost(root, current, arguments.ForwardedArguments(root));
            if (arguments.HasCommandMode)
            {
                child.WaitForExit();
                int code = child.ExitCode;
                child.Dispose();
                return code;
            }
            child.Dispose();
            return 0;
        }

        private static int RunUnattendedInstaller(string bundleDirectory, LauncherArguments arguments)
        {
            BundlePackage package = BundleInstaller.Inspect(bundleDirectory);
            InstallResult result = BundleInstaller.Install(package, new InstallOptions
            {
                InstallRoot = arguments.InstallRoot,
                AutoUpdateEnabled = !arguments.DisableAutoUpdate,
                CreateDesktopShortcut = false,
                CreateStartMenuShortcut = false,
                LaunchAfterInstall = false
            });
            WriteJson(new Dictionary<string, object>
            {
                { "status", "Installed" }, { "installRoot", result.InstallRoot },
                { "installPath", result.InstallPath }, { "artifactBase", result.ArtifactBase },
                { "warnings", result.Warnings.ToArray() }
            });
            return 0;
        }

        private static int RunInstaller(string bundleDirectory)
        {
            Application application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            InstallerWindow window = new InstallerWindow(bundleDirectory);
            application.Run(window);
            InstallResult result = window.Result;
            if (result == null) return 0;
            if (result.LaunchAfterInstall)
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = Path.Combine(result.InstallRoot, LauncherConstants.NativeFilename),
                    WorkingDirectory = result.InstallRoot,
                    UseShellExecute = false,
                    Arguments = LauncherCore.JoinArguments(new[] { "-InstallRoot", result.InstallRoot, LauncherConstants.DirectLaunchArgument })
                };
                Process process = Process.Start(info);
                if (process == null) throw new InvalidOperationException("Windows did not start the installed launcher.");
                process.Dispose();
            }
            return 0;
        }

        private static int RunHost(string root, LauncherArguments arguments)
        {
            Directory.CreateDirectory(root);
            UpdateService.RecoverPendingRepair(root);
            CurrentInstall current = ResolveCurrentState(root);
            try { BundleInstaller.RefreshExistingShortcuts(root); }
            catch (Exception error) { LauncherCore.WriteLog(root, "Could not refresh shortcut icons: " + error.Message); }
            LauncherSettings settings = LauncherCore.LoadSettings(root);
            UpdateService updates = new UpdateService(root);

            // Opening the manager does not activate application payloads. The bootstrap path
            // verifies this launcher before starting it; defer the full payload check until an
            // action actually launches, rolls back, or repairs.
            if (!arguments.HasCommandMode)
            {
                Application application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                LauncherWindow window = new LauncherWindow(root, current);
                application.Run(window);
                return 0;
            }

            try { updates.CleanupPending(current.InstallPath); }
            catch (Exception error) { LauncherCore.WriteLog(root, "Pending cleanup failed: " + error.Message); }
            try
            {
                foreach (string removed in VersionManager.EnforceRetention(root, current.ArtifactBase, settings.MaxRetainedVersions))
                    LauncherCore.WriteLog(root, "Retention policy removed installed version: " + removed);
            }
            catch (Exception error) { LauncherCore.WriteLog(root, "Retention cleanup failed: " + error.Message); }

            if (arguments.DisableAutoUpdate || arguments.EnableAutoUpdate)
            {
                settings = LauncherCore.UpdateSettings(root, delegate(LauncherSettings latest)
                {
                    if (arguments.DisableAutoUpdate) latest.AutoUpdateEnabled = false;
                    if (arguments.EnableAutoUpdate) latest.AutoUpdateEnabled = true;
                });
            }
            if (!String.IsNullOrWhiteSpace(arguments.RollbackTo))
            {
                current = LauncherCore.Rollback(root, arguments.RollbackTo);
                settings = LauncherCore.LoadSettings(root);
            }
            bool configurationOnly = (arguments.DisableAutoUpdate || arguments.EnableAutoUpdate ||
                !String.IsNullOrWhiteSpace(arguments.RollbackTo)) && !arguments.CheckOnly && !arguments.UpdateOnly;
            if (configurationOnly)
            {
                WriteJson(new Dictionary<string, object>
                {
                    { "status", String.IsNullOrWhiteSpace(arguments.RollbackTo) ? "SettingsUpdated" : "RolledBack" },
                    { "autoUpdateEnabled", settings.AutoUpdateEnabled }, { "releaseTag", current.ReleaseTag }
                });
                return 0;
            }

            bool shouldLaunch = !arguments.CheckOnly && !arguments.UpdateOnly;
            if (shouldLaunch) LauncherCore.LaunchCurrent(root, arguments.CodexArguments);
            if (arguments.NoUpdate || (!settings.AutoUpdateEnabled && shouldLaunch)) return 0;

            UpdateCheckResult check = updates.Check(current, arguments.ForceUpdateCheck);
            if (arguments.CheckOnly)
            {
                WriteJson(check.ToJson());
                return 0;
            }
            if (check.Status != "UpdateAvailable")
            {
                if (arguments.UpdateOnly) WriteJson(check.ToJson());
                return 0;
            }

            bool accept = arguments.AcceptUpdate;
            bool backup = !arguments.SkipBackup;
            if (!accept)
            {
                MessageBoxResult decision = MessageBox.Show(
                    "发现 Codex " + check.Candidate.MsixVersion + "（补丁 " + check.Candidate.PatchVersion + "）。\n\n现在下载并安装吗？",
                    "Codex Desktop Patch 更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                accept = decision == MessageBoxResult.Yes;
                if (accept)
                {
                    backup = MessageBox.Show("保留当前版本作为回退备份吗？", "保留当前版本",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
                }
            }
            if (!accept)
            {
                Dictionary<string, object> declined = new Dictionary<string, object>
                {
                    { "status", "UpdateDeclined" }, { "releaseTag", check.ReleaseTag },
                    { "backupCurrent", false }, { "cleanupScheduled", false }
                };
                if (arguments.UpdateOnly) WriteJson(declined);
                return 0;
            }
            UpdateInstallResult installed = updates.Install(current, check, backup);
            if (arguments.UpdateOnly) WriteJson(installed.ToJson());
            return 0;
        }

        private static CurrentInstall ResolveCurrentState(string root)
        {
            try { return LauncherCore.LoadCurrentState(root); }
            catch (Exception original)
            {
                CurrentInstall recovered;
                if (LauncherCore.TryRecoverCurrent(root, out recovered)) return recovered;
                if (original is InstalledIntegrityException)
                    throw new InvalidOperationException(
                        original.Message + "\n\n当前版本未通过完整性校验。请从对应 GitHub Release 重新安装，" +
                        "或在仍可用的其他版本管理器中执行“从 Release 修复”。",
                        original);
                throw new InvalidOperationException(
                    "尚未找到已安装的 Codex Desktop Patch。\n\n" +
                    "请完整解压 Release 中的 CX-<Codex版本>-p<补丁版本>-bundle.zip，" +
                    "然后双击其中的 CodexPatchLauncher.exe 完成安装。\n\n" +
                    "当前安装根目录：" + root,
                    original);
            }
        }

        private static int RunSelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "codex-native-launcher-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string child = PathSafety.RequireChild(root, Path.Combine(root, "version"), "Self-test path");
                bool unsafePath = false;
                try { PathSafety.RequireChild(root, Path.Combine(Directory.GetParent(root).FullName, "escape"), "Self-test path"); }
                catch { unsafePath = true; }
                if (!unsafePath || Versioning.Compare("26.721.11231.0", "26.721.4979.0") != 1 ||
                    Versioning.Compare("1.1.0", "1.0.5") != 1) throw new InvalidOperationException("Version or path boundary self-test failed.");
                ReleaseIdentity identity = ReleaseIdentity.Parse("windows-msstore-26.721.4979.0-desktop-patch-1.2.0");
                if (identity.BundleName != "CX-26.721.4979.0-p1.2.0-bundle.zip") throw new InvalidOperationException("Release identity self-test failed.");
                bool minimumLauncherRejected = false;
                try
                {
                    Dictionary<string, object> futureManifest = CreateSelfTestManifest("999.0");
                    UpdateService.ValidateManifest(futureManifest, JsonStore.String(futureManifest, "releaseTag"));
                }
                catch (InvalidDataException error) { minimumLauncherRejected = error.Message.IndexOf("too old", StringComparison.OrdinalIgnoreCase) >= 0; }
                if (!minimumLauncherRejected) throw new InvalidOperationException("Minimum launcher compatibility self-test failed.");
                bool unsafeArchive = false;
                try { PathSafety.SafeArchivePath(root, "../escape.txt"); }
                catch { unsafeArchive = true; }
                if (!unsafeArchive) throw new InvalidOperationException("Archive boundary self-test failed.");
                LauncherArguments directArguments = LauncherArguments.Parse(new[] { LauncherConstants.DirectLaunchArgument });
                bool directShortcutPassed = directArguments.NoUpdate && directArguments.HasCommandMode;
                InstalledVersion pinned = CreateSelfTestVersion(root, "1.0.0", false);
                InstalledVersion removable = CreateSelfTestVersion(root, "2.0.0", false);
                InstalledVersion deleteTarget = CreateSelfTestVersion(root, "2.5.0", false);
                InstalledVersion current = CreateSelfTestVersion(root, "3.0.0", true);
                WriteSelfTestCurrent(root, current);
                VersionCatalog.SetNote(root, pinned.ArtifactBase, "保留用于回归测试");
                VersionCatalog.SetPinned(root, pinned.ArtifactBase, true);
                IList<InstalledVersion> installed = LauncherCore.ListInstalledVersions(root, current.ArtifactBase);
                InstalledVersion loadedPinned = installed.Single(delegate(InstalledVersion value) { return value.ArtifactBase == pinned.ArtifactBase; });
                bool catalogPassed = loadedPinned.IsPinned && loadedPinned.Note == "保留用于回归测试";
                bool sizePassed = VersionManager.CalculateSize(removable) > 5;
                InstalledVersion directTarget = LauncherCore.RequireInstalledIntegrity(root,
                    LauncherCore.ReadInstalledVersion(root, pinned.InstallPath, pinned.ArtifactBase, true), true);
                bool directLaunchPassed = directTarget.ArtifactBase == pinned.ArtifactBase &&
                    File.Exists(Path.Combine(directTarget.InstallPath, "ChatGPT.exe"));
                WriteSelfTestCurrent(root, deleteTarget);
                bool staleCurrentDeletionBlocked = false;
                try { VersionManager.Delete(root, new CurrentInstall { ArtifactBase = current.ArtifactBase }, deleteTarget); }
                catch (InvalidOperationException) { staleCurrentDeletionBlocked = Directory.Exists(deleteTarget.InstallPath); }
                WriteSelfTestCurrent(root, current);
                VersionManager.Delete(root, new CurrentInstall { ArtifactBase = deleteTarget.ArtifactBase }, deleteTarget);
                bool staleNoteBlocked = false;
                try { VersionCatalog.SetNote(root, deleteTarget.ArtifactBase, "stale"); }
                catch (DirectoryNotFoundException) { staleNoteBlocked = true; }
                catch (FileNotFoundException) { staleNoteBlocked = true; }
                bool stalePinBlocked = false;
                try { VersionCatalog.SetPinned(root, deleteTarget.ArtifactBase, true); }
                catch (DirectoryNotFoundException) { stalePinBlocked = true; }
                catch (FileNotFoundException) { stalePinBlocked = true; }
                bool staleMetadataBlocked = staleNoteBlocked && stalePinBlocked;
                bool manualDeletionPassed = staleCurrentDeletionBlocked && staleMetadataBlocked &&
                    !Directory.Exists(deleteTarget.InstallPath) && VersionCatalog.Get(root, deleteTarget.ArtifactBase).Note.Length == 0;
                WriteSelfTestCurrent(root, removable);
                bool staleCurrentRepairBlocked = false;
                try { UpdateService.RequireRepairableTargetUnlocked(root, removable); }
                catch (InvalidOperationException) { staleCurrentRepairBlocked = Directory.Exists(removable.InstallPath); }
                WriteSelfTestCurrent(root, current);
                IList<string> removed = VersionManager.EnforceRetention(root, current.ArtifactBase, 1);
                bool retentionPassed = removed.Contains(removable.ArtifactBase) && Directory.Exists(pinned.InstallPath) && !Directory.Exists(removable.InstallPath);
                Dictionary<string, object> criticalPayloads = new Dictionary<string, object>();
                foreach (string relative in LauncherConstants.RequiredPayloadFiles)
                    criticalPayloads[relative.Replace('\\', '/')] = LauncherCore.Sha256(Path.Combine(current.InstallPath, relative));
                VerifiedBundle evidence = new VerifiedBundle
                {
                    Manifest = new Dictionary<string, object>
                    {
                        { "releaseTag", current.ReleaseTag }, { "artifactBase", current.ArtifactBase },
                        { "msixVersion", current.MsixVersion }, { "patchVersion", current.PatchVersion },
                        { "assets", new Dictionary<string, object>
                            {
                                { "zip", new Dictionary<string, object> { { "sha256", new string('a', 64) } } }
                            }
                        }
                    },
                    Report = new Dictionary<string, object>
                    {
                        { "zip", new Dictionary<string, object>
                            {
                                { "verifiedPayloads", criticalPayloads }
                            }
                        }
                    }
                };
                bool validationPassed = UpdateService.CompareInstalled(current, evidence).IsValid;
                UpdateService.ValidateExistingDestination(root, current.InstallPath, evidence.Manifest, evidence.Report);
                File.AppendAllText(Path.Combine(current.InstallPath, "ChatGPT.exe"), "tampered");
                validationPassed = validationPassed && !UpdateService.CompareInstalled(current, evidence).IsValid;
                try { UpdateService.ValidateExistingDestination(root, current.InstallPath, evidence.Manifest, evidence.Report); validationPassed = false; }
                catch (InvalidDataException) { }
                int dotNetFrameworkRelease = RuntimePrerequisites.GetInstalledDotNetFrameworkRelease();
                bool runtimePrerequisitePassed =
                    RuntimePrerequisites.IsSupportedDotNetFrameworkRelease(dotNetFrameworkRelease) &&
                    RuntimePrerequisites.IsSupportedDotNetFrameworkRelease(RuntimePrerequisites.MinimumDotNetFrameworkRelease) &&
                    !RuntimePrerequisites.IsSupportedDotNetFrameworkRelease(RuntimePrerequisites.MinimumDotNetFrameworkRelease - 1);
                bool managerIconPassed = File.Exists(BundleInstaller.EnsureManagerIcon(root)) &&
                    new FileInfo(Path.Combine(root, LauncherConstants.ManagerIconFilename)).Length > 0;
                File.Copy(Process.GetCurrentProcess().MainModule.FileName,
                    Path.Combine(root, LauncherConstants.NativeFilename), true);
                string shortcutDesktop = Path.Combine(root, "shortcut-desktop");
                string shortcutPrograms = Path.Combine(root, "shortcut-start-menu");
                ShortcutRepairResult shortcutFirst = BundleInstaller.CheckAndRepairShortcuts(root,
                    shortcutDesktop, shortcutPrograms);
                ShortcutRepairResult shortcutSecond = BundleInstaller.CheckAndRepairShortcuts(root,
                    shortcutDesktop, shortcutPrograms);
                File.Delete(Path.Combine(shortcutDesktop, "Codex Desktop Patch.lnk"));
                ShortcutRepairResult shortcutThird = BundleInstaller.CheckAndRepairShortcuts(root,
                    shortcutDesktop, shortcutPrograms);
                bool shortcutRepairPassed = shortcutFirst.Created == 4 && shortcutFirst.Healthy == 0 &&
                    shortcutFirst.Failures.Count == 0 && shortcutSecond.Created == 0 && shortcutSecond.Repaired == 0 &&
                    shortcutSecond.Healthy == 4 && shortcutSecond.Failures.Count == 0 &&
                    shortcutThird.Created == 1 && shortcutThird.Healthy == 3 && shortcutThird.Failures.Count == 0;
                List<string> failedChecks = new List<string>();
                if (!directShortcutPassed) failedChecks.Add("directShortcut");
                if (!catalogPassed) failedChecks.Add("versionCatalog");
                if (!sizePassed) failedChecks.Add("directorySize");
                if (!directLaunchPassed) failedChecks.Add("directLaunchTarget");
                if (!manualDeletionPassed) failedChecks.Add("manualDeletion");
                if (!staleCurrentRepairBlocked) failedChecks.Add("staleCurrentRepairBlocked");
                if (!retentionPassed) failedChecks.Add("retentionPolicy");
                if (!validationPassed) failedChecks.Add("criticalValidation");
                if (!runtimePrerequisitePassed) failedChecks.Add("runtimePrerequisite=" + dotNetFrameworkRelease);
                if (!managerIconPassed) failedChecks.Add("managerIcon");
                if (!shortcutRepairPassed)
                    failedChecks.Add("shortcutRepair=" +
                        shortcutFirst.Created + "/" + shortcutFirst.Healthy + "/" + shortcutFirst.Failures.Count + "," +
                        shortcutSecond.Created + "/" + shortcutSecond.Repaired + "/" + shortcutSecond.Healthy + "/" + shortcutSecond.Failures.Count + "," +
                        shortcutThird.Created + "/" + shortcutThird.Healthy + "/" + shortcutThird.Failures.Count + ":" +
                        String.Join("|", shortcutFirst.Failures.ToArray()));
                if (failedChecks.Count > 0)
                    throw new InvalidOperationException("Version management self-test failed: " +
                        String.Join(", ", failedChecks.ToArray()) + ".");
                WriteJson(new Dictionary<string, object>
                {
                    { "status", "Passed" }, { "launcherVersion", LauncherConstants.Version },
                    { "nativeLauncher", true }, { "nativeInstaller", true }, { "pathBoundary", unsafePath },
                    { "archiveBoundary", unsafeArchive }, { "bundleIdentity", true },
                    { "minimumLauncherCompatibility", minimumLauncherRejected },
                    { "directShortcut", directShortcutPassed },
                    { "versionCatalog", catalogPassed }, { "directorySize", sizePassed },
                    { "directLaunchTarget", directLaunchPassed }, { "manualDeletion", manualDeletionPassed },
                    { "staleCurrentDeletionBlocked", staleCurrentDeletionBlocked },
                    { "staleCurrentRepairBlocked", staleCurrentRepairBlocked },
                    { "staleMetadataBlocked", staleMetadataBlocked },
                    { "retentionPolicy", retentionPassed }, { "criticalValidation", validationPassed },
                    { "managerIcon", managerIconPassed },
                    { "shortcutRepair", shortcutRepairPassed },
                    { "runtimePrerequisite", runtimePrerequisitePassed },
                    { "dotNetFrameworkRelease", dotNetFrameworkRelease },
                    { "insidePath", child }, { "powerShellChildProcesses", 0 }
                });
                return 0;
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void WriteSelfTestCurrent(string root, InstalledVersion version)
        {
            JsonStore.WriteAtomic(Path.Combine(root, "current.json"), new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "releaseTag", version.ReleaseTag },
                { "artifactBase", version.ArtifactBase }, { "msixVersion", version.MsixVersion },
                { "patchVersion", version.PatchVersion }, { "installPath", version.InstallPath }
            });
        }

        private static InstalledVersion CreateSelfTestVersion(string root, string patchVersion, bool current)
        {
            string artifact = "CX-1.2.3.4-p" + patchVersion;
            string directory = Path.Combine(root, artifact);
            Directory.CreateDirectory(directory);
            foreach (string relative in LauncherConstants.RequiredPayloadFiles)
            {
                string path = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, relative == "ChatGPT.exe" ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 7, 8, 9 });
            }
            File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[32]);
            string release = "windows-msstore-1.2.3.4-desktop-patch-" + patchVersion;
            Dictionary<string, object> verifiedPayloads = new Dictionary<string, object>();
            foreach (string relative in LauncherConstants.RequiredPayloadFiles)
                verifiedPayloads[relative.Replace('\\', '/')] = LauncherCore.Sha256(Path.Combine(directory, relative));
            JsonStore.WriteAtomic(Path.Combine(directory, ".codex-patch-install.json"), new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "releaseTag", release }, { "artifactBase", artifact },
                { "msixVersion", "1.2.3.4" }, { "patchVersion", patchVersion },
                { "zipSha256", new string('a', 64) }, { "verifiedPayloads", verifiedPayloads },
                { "installedAt", DateTimeOffset.UtcNow.ToString("o") }
            });
            return new InstalledVersion
            {
                ArtifactBase = artifact, ReleaseTag = release, MsixVersion = "1.2.3.4", PatchVersion = patchVersion,
                InstallPath = directory, InstalledAt = DateTimeOffset.UtcNow.ToString("o"), IsCurrent = current
            };
        }

        private static Dictionary<string, object> CreateSelfTestManifest(string minimumLauncherVersion)
        {
            string artifact = "CX-1.2.3.4-p1.0.0";
            string hash = new string('d', 64);
            Dictionary<string, object> assets = new Dictionary<string, object>();
            assets["zip"] = new Dictionary<string, object> { { "name", artifact + ".zip" }, { "size", 1 }, { "sha256", hash } };
            assets["checksum"] = new Dictionary<string, object> { { "name", artifact + ".zip.sha256" }, { "size", 1 }, { "sha256", hash } };
            assets["verification"] = new Dictionary<string, object> { { "name", artifact + ".verification.json" }, { "size", 1 }, { "sha256", hash } };
            assets["launcher"] = new Dictionary<string, object> { { "name", LauncherConstants.NativeFilename }, { "size", 1 }, { "sha256", hash } };
            return new Dictionary<string, object>
            {
                { "schemaVersion", LauncherConstants.UpdateSchemaVersion }, { "channel", "stable" },
                { "repository", LauncherConstants.Repository }, { "msixVersion", "1.2.3.4" },
                { "patchVersion", "1.0.0" }, { "launcherVersion", minimumLauncherVersion },
                { "minimumLauncherVersion", minimumLauncherVersion }, { "artifactBase", artifact },
                { "releaseTag", "windows-msstore-1.2.3.4-desktop-patch-1.0.0" },
                { "publishedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "assets", assets }
            };
        }

        internal static void WriteJson(object value)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonStore.Serialize(value) + Environment.NewLine);
            using (Stream stream = Console.OpenStandardOutput())
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        private static void WriteError(string message)
        {
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes((message ?? "Unknown launcher error") + Environment.NewLine);
                using (Stream stream = Console.OpenStandardError()) { stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
            }
            catch { }
        }
    }
}
