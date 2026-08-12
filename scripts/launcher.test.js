"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const { version } = require("../package.json");
const {
  NATIVE_LAUNCHER_FILENAME,
  NATIVE_LAUNCHER_ICON,
  NATIVE_LAUNCHER_SOURCES,
  compileNativeLauncher,
  validateNativeLauncher,
} = require("./native-launcher");

test("native launcher and installer replace PowerShell entry points", () => {
  assert.equal(fs.existsSync(path.join(__dirname, "..", "resources", "CodexPatchLauncher.ps1")), false);
  assert.equal(fs.existsSync(path.join(__dirname, "..", "resources", "Extract-CodexPatch.ps1")), false);
  assert.equal(fs.existsSync(path.join(__dirname, "..", "resources", "Install-CodexPatch.cmd")), false);
  for (const source of NATIVE_LAUNCHER_SOURCES) assert.equal(fs.existsSync(source), true);
  const runtimeSource = NATIVE_LAUNCHER_SOURCES.map((file) => fs.readFileSync(file, "utf8")).join("\n");
  assert.doesNotMatch(runtimeSource, /ProcessStartInfo[^}]+powershell\.exe/is);
  assert.match(runtimeSource, /class LauncherWindow/);
  assert.match(runtimeSource, /class InstallerWindow/);
  assert.match(runtimeSource, /class BundleInstaller/);
  assert.match(runtimeSource, /class UpdateService/);
  assert.match(runtimeSource, /class VersionManager/);
  assert.match(runtimeSource, /ValidateInstalled/);
  assert.match(runtimeSource, /RepairInstalled/);
  assert.match(runtimeSource, /Codex Desktop Patch 管理器\.lnk/);
  assert.match(runtimeSource, /ManagerIconFilename/);
  assert.match(runtimeSource, /RefreshExistingShortcuts/);
  assert.match(runtimeSource, /CheckAndRepairShortcuts/);
  assert.match(runtimeSource, /IsShortcutHealthy/);
  assert.match(runtimeSource, /桌面和开始菜单中的 4 个 Codex Desktop Patch 快捷方式/);
  assert.match(runtimeSource, /DirectLaunchArgument/);
  assert.match(runtimeSource, /RuntimePrerequisites\.EnsureSupported\(\)/);
  const programSource = fs.readFileSync(path.join(__dirname, "..", "resources", "launcher", "Program.cs"), "utf8");
  assert.ok(
    programSource.indexOf("LauncherArguments.Parse(rawArguments)") <
      programSource.indexOf("RuntimePrerequisites.EnsureSupported()"),
    "arguments must be parsed before the runtime prerequisite check",
  );
  const coreSource = fs.readFileSync(path.join(__dirname, "..", "resources", "launcher", "LauncherCore.cs"), "utf8");
  assert.match(coreSource, /HasCommandMode[\s\S]+SelfTest \|\| CodexArguments\.Count/);
  assert.match(coreSource, /HashBufferSize = 1024 \* 1024/);
  assert.match(coreSource, /FileOptions\.SequentialScan/);
  assert.match(coreSource, /LaunchIntegrityFilename = "launch-integrity\.json"/);
  assert.match(coreSource, /LoadCurrentForLaunchUnlocked[\s\S]+HasRecentLaunchIntegrity/);
  assert.match(coreSource, /LaunchCurrent[\s\S]+LoadCurrentUnlocked\(root\)/);
  assert.match(coreSource, /SHChangeNotify\(ShellChangeUpdateItem, ShellNotifyPathUnicode/);
  assert.doesNotMatch(coreSource, /Local\\CodexPatchUpdater-zhy0504/);
  assert.match(coreSource, /Local\\CodexDesktopPatchUpdater-zhy0504/);
  assert.match(
    coreSource,
    /AcquireNamed\(DesktopBaselineMutexName[\s\S]+RootFileMutex\.Acquire/,
  );
  assert.match(programSource, /RunBootstrap[\s\S]+ResolveCurrentState\(root\)/);
  assert.match(programSource, /RunHost[\s\S]+ResolveCurrentState\(root\)/);
  assert.match(programSource, /new LauncherWindow\(root, current\)/);
  const windowSource = fs.readFileSync(path.join(__dirname, "..", "resources", "launcher", "LauncherWindow.cs"), "utf8");
  assert.doesNotMatch(windowSource, /LauncherWindow\(string root[^)]*\)[\s\S]{0,300}LoadCurrent\(/);
  const icon = fs.readFileSync(NATIVE_LAUNCHER_ICON);
  assert.equal(icon.readUInt16LE(0), 0);
  assert.equal(icon.readUInt16LE(2), 1);
  assert.ok(icon.readUInt16LE(4) >= 9);
});

test(
  "native WPF launcher compiles within the lightweight boundary and passes self-test",
  { skip: process.platform !== "win32" },
  (t) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-native-launcher-build-"));
    t.after(() => fs.rmSync(root, { force: true, recursive: true, maxRetries: 30, retryDelay: 200 }));
    const launcher = path.join(root, NATIVE_LAUNCHER_FILENAME);
    const built = compileNativeLauncher(launcher);
    assert.equal(built.version, version);
    assert.ok(built.bytes < 2 * 1024 * 1024);
    const verified = validateNativeLauncher(launcher);
    assert.equal(verified.selfTest.status, "Passed");
    assert.equal(verified.selfTest.nativeLauncher, true);
    assert.equal(verified.selfTest.nativeInstaller, true);
    assert.equal(verified.selfTest.directShortcut, true);
    assert.equal(verified.selfTest.versionCatalog, true);
    assert.equal(verified.selfTest.directorySize, true);
    assert.equal(verified.selfTest.directLaunchTarget, true);
    assert.equal(verified.selfTest.manualDeletion, true);
    assert.equal(verified.selfTest.staleCurrentDeletionBlocked, true);
    assert.equal(verified.selfTest.staleCurrentRepairBlocked, true);
    assert.equal(verified.selfTest.staleMetadataBlocked, true);
    assert.equal(verified.selfTest.shortcutRepair, true);
    assert.equal(verified.selfTest.retentionPolicy, true);
    assert.equal(verified.selfTest.criticalValidation, true);
    assert.equal(verified.selfTest.minimumLauncherCompatibility, true);
    assert.equal(verified.selfTest.runtimePrerequisite, true);
    assert.ok(verified.selfTest.dotNetFrameworkRelease >= 528040);
    assert.equal(verified.selfTest.powerShellChildProcesses, 0);
  },
);

test(
  "native launcher persists settings and rolls back only to marked installs",
  { skip: process.platform !== "win32" },
  (t) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-native-launcher-state-"));
    t.after(() => fs.rmSync(root, { force: true, recursive: true, maxRetries: 30, retryDelay: 200 }));
    const launcher = path.join(root, NATIVE_LAUNCHER_FILENAME);
    compileNativeLauncher(launcher);
    const isolatedRoot = path.join(root, "isolated");
    fs.mkdirSync(isolatedRoot);
    const isolatedLauncher = path.join(isolatedRoot, NATIVE_LAUNCHER_FILENAME);
    fs.copyFileSync(launcher, isolatedLauncher);
    const missing = spawnSync(
      isolatedLauncher,
      ["-CheckOnly"],
      { encoding: "utf8", timeout: 30_000, windowsHide: true },
    );
    assert.equal(missing.status, 1);
    assert.match(missing.stderr, /尚未找到已安装的 Codex Desktop Patch/);
    assert.doesNotMatch(missing.stderr, /current\.json/);
    const missingInstallRoot = spawnSync(
      isolatedLauncher,
      ["-InstallRoot", "-CheckOnly"],
      { encoding: "utf8", timeout: 30_000, windowsHide: true },
    );
    assert.equal(missingInstallRoot.status, 1);
    assert.match(missingInstallRoot.stderr, /-InstallRoot requires a value/);
    const versions = [
      { artifactBase: "CX-1.2.3.4-p1.0.5", patchVersion: "1.0.5" },
      { artifactBase: "CX-1.2.3.4-p1.1.0", patchVersion: "1.1.0", sidecarEvidence: true },
    ];
    const requiredPayloads = [
      "Codex.exe",
      path.join("resources", "app.asar"),
      path.join("resources", "codex.exe"),
      path.join("resources", "codex-powershell-resolver.js"),
      path.join("resources", "codex-powershell-shim.exe"),
    ];
    const directLaunchProbe = path.join(process.env.SystemRoot, "System32", "where.exe");
    for (const item of versions) {
      const installPath = path.join(root, item.artifactBase);
      fs.mkdirSync(installPath);
      fs.copyFileSync(directLaunchProbe, path.join(installPath, "ChatGPT.exe"));
      for (const relative of requiredPayloads) {
        const target = path.join(installPath, relative);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(target, "fixture");
      }
      fs.copyFileSync(launcher, path.join(installPath, NATIVE_LAUNCHER_FILENAME));
      const verifiedPayloads = {};
      for (const relative of ["ChatGPT.exe", ...requiredPayloads, NATIVE_LAUNCHER_FILENAME]) {
        const normalized = relative.split(path.sep).join("/");
        verifiedPayloads[normalized] = crypto
          .createHash("sha256")
          .update(fs.readFileSync(path.join(installPath, relative)))
          .digest("hex");
      }
      const marker = {
        schemaVersion: 1,
        releaseTag: `windows-msstore-1.2.3.4-desktop-patch-${item.patchVersion}`,
        artifactBase: item.artifactBase,
        msixVersion: "1.2.3.4",
        patchVersion: item.patchVersion,
        zipSha256: "a".repeat(64),
      };
      if (item.sidecarEvidence) {
        fs.writeFileSync(
          path.join(installPath, "CodexPatch-integrity.json"),
          `${JSON.stringify({ ...marker, verifiedPayloads })}\n`,
        );
      } else if (!item.noEvidence) {
        marker.verifiedPayloads = verifiedPayloads;
      }
      fs.writeFileSync(
        path.join(installPath, ".codex-patch-install.json"),
        `${JSON.stringify(marker)}\n`,
      );
    }
    const malformedPath = path.join(root, "CX-9.9.9.9-p9.9.9");
    fs.mkdirSync(malformedPath);
    fs.writeFileSync(
      path.join(malformedPath, ".codex-patch-install.json"),
      `${JSON.stringify({
        schemaVersion: 1,
        releaseTag: "windows-msstore-9.9.9.9-desktop-patch-9.9.9",
        artifactBase: "CX-9.9.9.9-p9.9.9",
        msixVersion: "not-a-version",
        patchVersion: "9.9.9",
        zipSha256: "b".repeat(64),
      })}\n`,
    );
    const active = versions[1];
    fs.writeFileSync(
      path.join(root, "current.json"),
      `${JSON.stringify({
        schemaVersion: 1,
        releaseTag: `windows-msstore-1.2.3.4-desktop-patch-${active.patchVersion}`,
        artifactBase: active.artifactBase,
        msixVersion: "1.2.3.4",
        patchVersion: active.patchVersion,
        installPath: path.join(root, active.artifactBase),
      })}\n`,
    );
    const run = (...args) =>
      spawnSync(
        launcher,
        ["-InstallRoot", root, ...args],
        { encoding: "utf8", timeout: 30_000, windowsHide: true },
      );

    fs.writeFileSync(
      path.join(root, "settings.json"),
      `${JSON.stringify({ schemaVersion: 1, autoUpdateEnabled: true, keepCurrentVersion: true, maxRetainedVersions: 3 })}\n`,
    );
    fs.rmSync(path.join(root, "current.json"));
    const disabled = run("-DisableAutoUpdate");
    assert.equal(disabled.status, 0, disabled.stderr || disabled.stdout);
    assert.equal(JSON.parse(disabled.stdout).autoUpdateEnabled, false);
    assert.equal(JSON.parse(fs.readFileSync(path.join(root, "settings.json"))).maxRetainedVersions, 3);
    const migratedMarker = JSON.parse(fs.readFileSync(
      path.join(root, versions[1].artifactBase, ".codex-patch-install.json"),
      "utf8",
    ));
    assert.equal(Object.keys(migratedMarker.verifiedPayloads).length, 7);
    const recovered = JSON.parse(fs.readFileSync(path.join(root, "current.json")));
    assert.equal(recovered.artifactBase, versions[1].artifactBase);
    assert.equal(recovered.activationReason, "state-recovery");
    const rollback = run("-RollbackTo", versions[0].artifactBase);
    assert.equal(rollback.status, 0, rollback.stderr || rollback.stdout);
    assert.equal(JSON.parse(rollback.stdout).status, "RolledBack");
    assert.equal(JSON.parse(fs.readFileSync(path.join(root, "current.json"))).artifactBase, versions[0].artifactBase);
    const direct = run("-NoUpdate");
    assert.equal(direct.status, 0, direct.stderr || direct.stdout || direct.error?.message);

    const launchIntegrityPath = path.join(root, "launch-integrity.json");
    const launchIntegrity = JSON.parse(fs.readFileSync(launchIntegrityPath, "utf8"));
    assert.equal(launchIntegrity.schemaVersion, 1);
    assert.equal(launchIntegrity.artifactBase, versions[0].artifactBase);

    const activePayloadPath = path.join(root, versions[0].artifactBase, "Codex.exe");
    const activePayload = fs.readFileSync(activePayloadPath);
    fs.writeFileSync(activePayloadPath, "CORRUPTED-CURRENT-PAYLOAD");
    const stateOnlyCheck = run("-CheckOnly", "-NoUpdate");
    assert.equal(stateOnlyCheck.status, 0, stateOnlyCheck.stderr || stateOnlyCheck.stdout);
    const cachedLaunch = run("-NoUpdate");
    assert.equal(cachedLaunch.status, 0, cachedLaunch.stderr || cachedLaunch.stdout);
    fs.writeFileSync(
      launchIntegrityPath,
      `${JSON.stringify({ ...launchIntegrity, verifiedAt: new Date(Date.now() + 2 * 86400000).toISOString() })}\n`,
    );
    const rejectedLaunch = run("-NoUpdate");
    assert.equal(rejectedLaunch.status, 1);
    assert.match(rejectedLaunch.stderr, /关键文件哈希不匹配/);
    fs.writeFileSync(activePayloadPath, activePayload);

    const enabled = run("-EnableAutoUpdate");
    assert.equal(enabled.status, 0, enabled.stderr || enabled.stdout);
    assert.equal(JSON.parse(enabled.stdout).autoUpdateEnabled, true);

    const incomplete = { artifactBase: "CX-1.2.3.4-p1.2.0", patchVersion: "1.2.0" };
    const incompletePath = path.join(root, incomplete.artifactBase);
    fs.mkdirSync(incompletePath);
    fs.copyFileSync(directLaunchProbe, path.join(incompletePath, "ChatGPT.exe"));
    fs.writeFileSync(
      path.join(incompletePath, ".codex-patch-install.json"),
      `${JSON.stringify({
        schemaVersion: 1,
        releaseTag: `windows-msstore-1.2.3.4-desktop-patch-${incomplete.patchVersion}`,
        artifactBase: incomplete.artifactBase,
        msixVersion: "1.2.3.4",
        patchVersion: incomplete.patchVersion,
        zipSha256: "c".repeat(64),
      })}\n`,
    );
    const rejectedRollback = run("-RollbackTo", incomplete.artifactBase);
    assert.equal(rejectedRollback.status, 1);
    assert.match(rejectedRollback.stderr, /Installed version is incomplete/);
    assert.equal(JSON.parse(fs.readFileSync(path.join(root, "current.json"))).artifactBase, versions[0].artifactBase);

    const corruptedRollbackPath = path.join(root, versions[1].artifactBase, "Codex.exe");
    const originalRollbackPayload = fs.readFileSync(corruptedRollbackPath);
    fs.writeFileSync(corruptedRollbackPath, "CORRUPTED-AFTER-INSTALL");
    const corruptedRollback = run("-RollbackTo", versions[1].artifactBase);
    assert.equal(corruptedRollback.status, 1);
    assert.match(corruptedRollback.stderr, /关键文件哈希不匹配/);
    assert.equal(JSON.parse(fs.readFileSync(path.join(root, "current.json"))).artifactBase, versions[0].artifactBase);
    fs.writeFileSync(corruptedRollbackPath, originalRollbackPayload);

    const repairId = "0123456789abcdef0123456789abcdef";
    const repairBackup = path.join(root, `.repair-backup-${versions[1].artifactBase}-${repairId}`);
    const repairReady = path.join(root, `.repair-ready-${versions[1].artifactBase}-${repairId}`);
    fs.renameSync(path.join(root, versions[1].artifactBase), repairBackup);
    fs.writeFileSync(
      path.join(root, "pending-repair.json"),
      `${JSON.stringify({
        schemaVersion: 1,
        artifactBase: versions[1].artifactBase,
        destination: path.join(root, versions[1].artifactBase),
        ready: repairReady,
        backup: repairBackup,
      })}\n`,
    );
    const repairedAfterInterruption = run("-EnableAutoUpdate");
    assert.equal(repairedAfterInterruption.status, 0, repairedAfterInterruption.stderr || repairedAfterInterruption.stdout);
    assert.equal(fs.existsSync(path.join(root, versions[1].artifactBase, "ChatGPT.exe")), true);
    assert.equal(fs.existsSync(repairBackup), false);
    assert.equal(fs.existsSync(path.join(root, "pending-repair.json")), false);

    const secondRepairId = "fedcba9876543210fedcba9876543210";
    const secondDestination = path.join(root, versions[1].artifactBase);
    const secondBackup = path.join(root, `.repair-backup-${versions[1].artifactBase}-${secondRepairId}`);
    const secondReady = path.join(root, `.repair-ready-${versions[1].artifactBase}-${secondRepairId}`);
    fs.cpSync(secondDestination, secondBackup, { recursive: true });
    fs.writeFileSync(path.join(secondBackup, "repair-state.txt"), "committed-old-version");
    fs.writeFileSync(path.join(secondDestination, "repair-state.txt"), "uncommitted-replacement");
    fs.writeFileSync(
      path.join(root, "pending-repair.json"),
      `${JSON.stringify({
        schemaVersion: 1,
        artifactBase: versions[1].artifactBase,
        destination: secondDestination,
        ready: secondReady,
        backup: secondBackup,
      })}\n`,
    );
    const rolledBackUncommittedRepair = run("-EnableAutoUpdate");
    assert.equal(rolledBackUncommittedRepair.status, 0, rolledBackUncommittedRepair.stderr || rolledBackUncommittedRepair.stdout);
    assert.equal(fs.readFileSync(path.join(secondDestination, "repair-state.txt"), "utf8"), "committed-old-version");
    assert.equal(fs.existsSync(secondBackup), false);
    assert.equal(fs.existsSync(path.join(root, "pending-repair.json")), false);

    const external = fs.mkdtempSync(path.join(os.tmpdir(), "codex-native-launcher-external-"));
    t.after(() => fs.rmSync(external, { force: true, recursive: true, maxRetries: 30, retryDelay: 200 }));
    const sentinel = path.join(external, "must-survive.txt");
    fs.writeFileSync(sentinel, "outside install root");
    const linkedArtifact = "CX-1.2.3.4-p9.9.9";
    const linkedPath = path.join(root, linkedArtifact);
    fs.symlinkSync(external, linkedPath, "junction");
    fs.writeFileSync(
      path.join(root, "pending-cleanup.json"),
      `${JSON.stringify({
        schemaVersion: 1,
        entries: [{ path: linkedPath, artifactBase: linkedArtifact }],
      })}\n`,
    );
    const guardedCleanup = run("-EnableAutoUpdate");
    assert.equal(guardedCleanup.status, 0, guardedCleanup.stderr || guardedCleanup.stdout);
    assert.equal(fs.readFileSync(sentinel, "utf8"), "outside install root");
    assert.equal(fs.existsSync(linkedPath), true);
    fs.rmdirSync(linkedPath);
  },
);
