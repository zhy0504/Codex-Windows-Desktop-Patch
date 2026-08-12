using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexPatch.NativeLauncher
{
    internal sealed class BundlePackage
    {
        internal string Directory;
        internal string ArtifactBase;
        internal string ReleaseTag;
        internal string MsixVersion;
        internal string PatchVersion;
        internal Dictionary<string, object> Manifest;
    }

    internal sealed class InstallOptions
    {
        internal string InstallRoot;
        internal bool AutoUpdateEnabled = true;
        internal bool CreateDesktopShortcut = true;
        internal bool CreateStartMenuShortcut = true;
        internal bool LaunchAfterInstall = true;
    }

    internal sealed class InstallResult
    {
        internal string InstallRoot;
        internal string InstallPath;
        internal string ArtifactBase;
        internal bool LaunchAfterInstall;
        internal readonly List<string> Warnings = new List<string>();
    }

    internal sealed class ShortcutRepairResult
    {
        internal int Checked;
        internal int Healthy;
        internal int Repaired;
        internal int Created;
        internal readonly List<string> Details = new List<string>();
        internal readonly List<string> Failures = new List<string>();

        internal bool IsSuccessful { get { return Failures.Count == 0; } }
    }

    internal static class BundleInstaller
    {
        internal static bool IsBundleDirectory(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) return false;
            return File.Exists(Path.Combine(directory, LauncherConstants.ManifestFilename)) &&
                File.Exists(Path.Combine(directory, LauncherConstants.NativeFilename));
        }

        internal static BundlePackage Inspect(string directory)
        {
            directory = Path.GetFullPath(directory);
            Dictionary<string, object> manifest = JsonStore.ReadObject(Path.Combine(directory, LauncherConstants.ManifestFilename));
            if (manifest == null) throw new InvalidDataException("安装包缺少 " + LauncherConstants.ManifestFilename + "。");
            string releaseTag = JsonStore.String(manifest, "releaseTag");
            UpdateService.ValidateManifest(manifest, releaseTag);
            Dictionary<string, object> assets = JsonStore.Object(manifest, "assets");
            foreach (string name in new[] { "zip", "checksum", "verification", "launcher" })
            {
                Dictionary<string, object> definition = JsonStore.Object(assets, name);
                string file = JsonStore.String(definition, "name");
                string path = Path.Combine(directory, file);
                if (!File.Exists(path) || new FileInfo(path).Length != JsonStore.Integer(definition, "size"))
                    throw new InvalidDataException("安装包文件缺失或大小不符：" + file);
            }
            return new BundlePackage
            {
                Directory = directory,
                ArtifactBase = JsonStore.String(manifest, "artifactBase"),
                ReleaseTag = releaseTag,
                MsixVersion = JsonStore.String(manifest, "msixVersion"),
                PatchVersion = JsonStore.String(manifest, "patchVersion"),
                Manifest = manifest
            };
        }

        internal static string EnsureManagerIcon(string root)
        {
            root = PathSafety.NormalizeRoot(root);
            string path = Path.Combine(root, LauncherConstants.ManagerIconFilename);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Generate a small gear badge at install time so the bundle needs no extra icon asset.
                using (Bitmap bitmap = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    using (Brush background = new SolidBrush(Color.FromArgb(255, 35, 49, 59)))
                        graphics.FillEllipse(background, 18, 18, 220, 220);
                    using (Pen ring = new Pen(Color.FromArgb(255, 53, 190, 169), 15))
                        graphics.DrawEllipse(ring, 43, 43, 170, 170);
                    using (Pen gear = new Pen(Color.White, 18))
                    {
                        gear.StartCap = LineCap.Round;
                        gear.EndCap = LineCap.Round;
                        PointF center = new PointF(128, 128);
                        for (int index = 0; index < 8; index++)
                        {
                            double angle = index * Math.PI / 4.0;
                            float inner = 47;
                            float outer = 77;
                            graphics.DrawLine(
                                gear,
                                center.X + (float)Math.Cos(angle) * inner,
                                center.Y + (float)Math.Sin(angle) * inner,
                                center.X + (float)Math.Cos(angle) * outer,
                                center.Y + (float)Math.Sin(angle) * outer);
                        }
                    }
                    using (Brush center = new SolidBrush(Color.White)) graphics.FillEllipse(center, 83, 83, 90, 90);
                    using (Brush hole = new SolidBrush(Color.FromArgb(255, 35, 49, 59))) graphics.FillEllipse(hole, 106, 106, 44, 44);

                    IntPtr handle = bitmap.GetHicon();
                    try
                    {
                        using (Icon icon = Icon.FromHandle(handle))
                        using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                            icon.Save(stream);
                    }
                    finally { DestroyIcon(handle); }
                }

                try { File.Move(temporary, path); }
                catch (IOException)
                {
                    if (!File.Exists(path)) throw;
                }
                return path;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch { }
                }
            }
        }

        internal static void RefreshExistingShortcuts(string root)
        {
            root = PathSafety.NormalizeRoot(root);
            string launcher = Path.Combine(root, LauncherConstants.NativeFilename);
            if (!File.Exists(launcher)) return;
            string managerIcon = launcher;
            List<string> warnings = new List<string>();
            try { managerIcon = EnsureManagerIcon(root); }
            catch (Exception error) { warnings.Add("无法生成管理器图标：" + error.Message); }

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");
            foreach (ShortcutDefinition definition in GetShortcutDefinitions(desktop, programs, launcher, root, managerIcon))
                RefreshShortcutIfPresent(definition, warnings);
            foreach (string warning in warnings) LauncherCore.WriteLog(root, warning);
        }

        internal static ShortcutRepairResult CheckAndRepairShortcuts(string root)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");
            return CheckAndRepairShortcuts(root, desktop, programs);
        }

        internal static ShortcutRepairResult CheckAndRepairShortcuts(string root, string desktop, string programs)
        {
            root = PathSafety.NormalizeRoot(root);
            string launcher = Path.Combine(root, LauncherConstants.NativeFilename);
            if (!File.Exists(launcher)) throw new FileNotFoundException("安装根目录中缺少启动器。", launcher);

            ShortcutRepairResult result = new ShortcutRepairResult();
            using (InstallOperationLock operation = InstallOperationLock.Acquire(root))
            {
                string managerIcon = launcher;
                try { managerIcon = EnsureManagerIcon(root); }
                catch (Exception error) { result.Failures.Add("无法生成管理器图标：" + error.Message); }

                foreach (ShortcutDefinition definition in GetShortcutDefinitions(desktop, programs, launcher, root, managerIcon))
                {
                    result.Checked++;
                    bool existed = File.Exists(definition.Path);
                    if (IsShortcutHealthy(definition))
                    {
                        result.Healthy++;
                        result.Details.Add("正常：" + definition.Label);
                        continue;
                    }

                    List<string> warnings = new List<string>();
                    if (TryCreateShortcut(definition.Path, definition.Target, definition.WorkingDirectory,
                        definition.Arguments, definition.Description, definition.IconPath, warnings))
                    {
                        if (existed) result.Repaired++;
                        else result.Created++;
                        result.Details.Add((existed ? "已修复：" : "已创建：") + definition.Label);
                    }
                    else
                    {
                        result.Failures.AddRange(warnings);
                    }
                }
            }

            foreach (string failure in result.Failures) LauncherCore.WriteLog(root, "Shortcut repair failed: " + failure);
            return result;
        }

        internal static InstallResult Install(BundlePackage package, InstallOptions options)
        {
            if (package == null) throw new ArgumentNullException("package");
            if (options == null) throw new ArgumentNullException("options");
            string root = PathSafety.NormalizeRoot(options.InstallRoot);
            if (Path.GetPathRoot(root).TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("不能将磁盘根目录作为安装目录。");

            Directory.CreateDirectory(root);
            BundlePackage verifiedPackage = Inspect(package.Directory);
            Dictionary<string, object> manifest = verifiedPackage.Manifest;
            Dictionary<string, object> assets = JsonStore.Object(manifest, "assets");
            string zipPath = UpdateService.VerifiedAsset(JsonStore.Object(assets, "zip"), package.Directory);
            string checksumPath = UpdateService.VerifiedAsset(JsonStore.Object(assets, "checksum"), package.Directory);
            string reportPath = UpdateService.VerifiedAsset(JsonStore.Object(assets, "verification"), package.Directory);
            string launcherPath = UpdateService.VerifiedAsset(JsonStore.Object(assets, "launcher"), package.Directory);
            UpdateService.ValidateChecksum(checksumPath, JsonStore.Object(assets, "zip"));
            Dictionary<string, object> report = UpdateService.ValidateReport(reportPath, manifest);

            string ready = PathSafety.RequireChild(root,
                Path.Combine(root, ".install-ready-" + verifiedPackage.ArtifactBase + "-" + Guid.NewGuid().ToString("N")),
                "安装候选目录");
            try
            {
                UpdateService.ExtractApplication(root, zipPath, ready, manifest, report);
                InstallResult result;
                using (InstallOperationLock operation = InstallOperationLock.Acquire(root))
                {
                    string destination = PathSafety.RequireChild(root, Path.Combine(root, verifiedPackage.ArtifactBase), "安装目标");
                    if (Directory.Exists(destination))
                        UpdateService.ValidateExistingDestination(root, destination, manifest, report);
                    else
                        Directory.Move(ready, destination);

                    string versionLauncher = Path.Combine(destination, LauncherConstants.NativeFilename);
                    string expectedLauncherHash = JsonStore.String(JsonStore.Object(report, "nativeLauncher"), "sha256");
                    if (LauncherCore.Sha256(launcherPath) != expectedLauncherHash ||
                        LauncherCore.Sha256(versionLauncher) != expectedLauncherHash)
                        throw new InvalidDataException("安装包内外的原生启动器不一致。");

                    LauncherCore.InstallFileAtomic(launcherPath, Path.Combine(root, LauncherConstants.NativeFilename));
                    Dictionary<string, object> current = new Dictionary<string, object>
                    {
                        { "schemaVersion", 1 }, { "releaseTag", verifiedPackage.ReleaseTag },
                        { "artifactBase", verifiedPackage.ArtifactBase }, { "msixVersion", verifiedPackage.MsixVersion },
                        { "patchVersion", verifiedPackage.PatchVersion }, { "installPath", destination },
                        { "activatedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                        { "activationReason", "native-installer" }
                    };
                    JsonStore.WriteAtomic(Path.Combine(root, "current.json"), current);
                    LauncherCore.UpdateSettings(root, delegate(LauncherSettings settings)
                    {
                        settings.AutoUpdateEnabled = options.AutoUpdateEnabled;
                        settings.KeepCurrentVersion = true;
                    });

                    result = new InstallResult
                    {
                        InstallRoot = root,
                        InstallPath = destination,
                        ArtifactBase = verifiedPackage.ArtifactBase,
                        LaunchAfterInstall = options.LaunchAfterInstall
                    };
                }
                string launcher = Path.Combine(root, LauncherConstants.NativeFilename);
                string managerIcon = launcher;
                try { managerIcon = EnsureManagerIcon(root); }
                catch (Exception error) { result.Warnings.Add("无法生成管理器图标：" + error.Message); }
                if (options.CreateDesktopShortcut)
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    TryCreateShortcut(Path.Combine(desktop, "Codex Desktop Patch.lnk"), launcher, root,
                        LauncherConstants.DirectLaunchArgument, "直接启动 Codex Desktop Patch", launcher, result.Warnings);
                    TryCreateShortcut(Path.Combine(desktop, "Codex Desktop Patch 管理器.lnk"), launcher, root,
                        String.Empty, "打开 Codex Desktop Patch 管理器", managerIcon, result.Warnings);
                }
                if (options.CreateStartMenuShortcut)
                {
                    string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Microsoft\Windows\Start Menu\Programs");
                    TryCreateShortcut(Path.Combine(programs, "Codex Desktop Patch.lnk"), launcher, root,
                        LauncherConstants.DirectLaunchArgument, "直接启动 Codex Desktop Patch", launcher, result.Warnings);
                    TryCreateShortcut(Path.Combine(programs, "Codex Desktop Patch 管理器.lnk"), launcher, root,
                        String.Empty, "打开 Codex Desktop Patch 管理器", managerIcon, result.Warnings);
                }
                LauncherCore.WriteLog(root, "Installed with native installer: " + verifiedPackage.ArtifactBase);
                return result;
            }
            finally
            {
                if (Directory.Exists(ready))
                {
                    try { LongPathFileSystem.DeleteDirectory(root, ready); }
                    catch (Exception error) { LauncherCore.WriteLog(root, "Could not remove installer staging directory: " + error.Message); }
                }
            }
        }

        private sealed class ShortcutDefinition
        {
            internal string Path;
            internal string Label;
            internal string Target;
            internal string WorkingDirectory;
            internal string Arguments;
            internal string Description;
            internal string IconPath;
        }

        private static List<ShortcutDefinition> GetShortcutDefinitions(string desktop, string programs, string launcher,
            string workingDirectory, string managerIcon)
        {
            return new List<ShortcutDefinition>
            {
                new ShortcutDefinition
                {
                    Path = Path.Combine(desktop, "Codex Desktop Patch.lnk"),
                    Label = "桌面 / Codex Desktop Patch",
                    Target = launcher,
                    WorkingDirectory = workingDirectory,
                    Arguments = LauncherConstants.DirectLaunchArgument,
                    Description = "直接启动 Codex Desktop Patch",
                    IconPath = launcher
                },
                new ShortcutDefinition
                {
                    Path = Path.Combine(desktop, "Codex Desktop Patch 管理器.lnk"),
                    Label = "桌面 / Codex Desktop Patch 管理器",
                    Target = launcher,
                    WorkingDirectory = workingDirectory,
                    Arguments = String.Empty,
                    Description = "打开 Codex Desktop Patch 管理器",
                    IconPath = managerIcon
                },
                new ShortcutDefinition
                {
                    Path = Path.Combine(programs, "Codex Desktop Patch.lnk"),
                    Label = "开始菜单 / Codex Desktop Patch",
                    Target = launcher,
                    WorkingDirectory = workingDirectory,
                    Arguments = LauncherConstants.DirectLaunchArgument,
                    Description = "直接启动 Codex Desktop Patch",
                    IconPath = launcher
                },
                new ShortcutDefinition
                {
                    Path = Path.Combine(programs, "Codex Desktop Patch 管理器.lnk"),
                    Label = "开始菜单 / Codex Desktop Patch 管理器",
                    Target = launcher,
                    WorkingDirectory = workingDirectory,
                    Arguments = String.Empty,
                    Description = "打开 Codex Desktop Patch 管理器",
                    IconPath = managerIcon
                }
            };
        }

        private static void RefreshShortcutIfPresent(ShortcutDefinition definition, IList<string> warnings)
        {
            if (!File.Exists(definition.Path)) return;
            TryCreateShortcut(definition.Path, definition.Target, definition.WorkingDirectory,
                definition.Arguments, definition.Description, definition.IconPath, warnings);
        }

        private static bool IsShortcutHealthy(ShortcutDefinition definition)
        {
            if (!File.Exists(definition.Path)) return false;
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { definition.Path });
                Type shortcutType = shortcut.GetType();
                string target = Convert.ToString(shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty,
                    null, shortcut, null), CultureInfo.InvariantCulture);
                string workingDirectory = Convert.ToString(shortcutType.InvokeMember("WorkingDirectory",
                    BindingFlags.GetProperty, null, shortcut, null), CultureInfo.InvariantCulture);
                string arguments = Convert.ToString(shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty,
                    null, shortcut, null), CultureInfo.InvariantCulture);
                string iconLocation = Convert.ToString(shortcutType.InvokeMember("IconLocation",
                    BindingFlags.GetProperty, null, shortcut, null), CultureInfo.InvariantCulture);
                return ShortcutPathEquals(definition.Target, target) &&
                    ShortcutPathEquals(definition.WorkingDirectory, workingDirectory) &&
                    String.Equals(definition.Arguments ?? String.Empty, arguments ?? String.Empty,
                        StringComparison.Ordinal) && ShortcutPathEquals(definition.IconPath, StripIconIndex(iconLocation));
            }
            catch { return false; }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static bool ShortcutPathEquals(string expected, string actual)
        {
            if (String.IsNullOrWhiteSpace(expected) || String.IsNullOrWhiteSpace(actual)) return false;
            try
            {
                string left = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string right = Path.GetFullPath(actual.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                return String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
            catch { return String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase); }
        }

        private static string StripIconIndex(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            string normalized = value.Trim().Trim('"');
            int comma = normalized.LastIndexOf(',');
            int iconIndex;
            if (comma > 0 && Int32.TryParse(normalized.Substring(comma + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out iconIndex)) return normalized.Substring(0, comma);
            return normalized;
        }

        private static bool TryCreateShortcut(string path, string target, string workingDirectory,
            string arguments, string description, string iconPath, IList<string> warnings)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用。");
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments ?? String.Empty });
                string icon = String.IsNullOrWhiteSpace(iconPath) ? target : iconPath;
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon + ",0" });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                LauncherCore.NotifyShellItemChanged(path);
                return true;
            }
            catch (Exception error) { warnings.Add("无法创建快捷方式 " + path + "：" + error.Message); return false; }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

    }
}
