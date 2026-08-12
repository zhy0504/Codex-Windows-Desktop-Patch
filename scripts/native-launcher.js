"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const { PATCH_VERSION, findCSharpCompiler, sha256File } = require("./helper");

const PROJECT_ROOT = path.resolve(__dirname, "..");
const NATIVE_LAUNCHER_FILENAME = "CodexPatchLauncher.exe";
const NATIVE_LAUNCHER_ROOT = path.join(PROJECT_ROOT, "resources", "launcher");
const NATIVE_LAUNCHER_VERSION_SOURCE = path.join(NATIVE_LAUNCHER_ROOT, "LauncherCore.cs");
const NATIVE_LAUNCHER_SOURCES = [
  "RuntimePrerequisites.cs",
  "LauncherCore.cs",
  "InstallerService.cs",
  "InstallerWindow.cs",
  "LauncherUpdater.cs",
  "LauncherWindow.cs",
  "VersionManager.cs",
  "Program.cs",
].map((name) => path.join(NATIVE_LAUNCHER_ROOT, name));
const NATIVE_LAUNCHER_MANIFEST = path.join(NATIVE_LAUNCHER_ROOT, "CodexPatchLauncher.manifest");
const NATIVE_LAUNCHER_ICON = path.join(NATIVE_LAUNCHER_ROOT, "CodexPatchLauncher.ico");

function assertNativeLauncherSourceVersion() {
  for (const source of NATIVE_LAUNCHER_SOURCES) {
    if (!fs.existsSync(source)) throw new Error(`Native launcher source was not found: ${source}`);
  }
  if (!fs.existsSync(NATIVE_LAUNCHER_MANIFEST)) {
    throw new Error(`Native launcher manifest was not found: ${NATIVE_LAUNCHER_MANIFEST}`);
  }
  if (!fs.existsSync(NATIVE_LAUNCHER_ICON)) {
    throw new Error(`Native launcher icon was not found: ${NATIVE_LAUNCHER_ICON}`);
  }
  const source = fs.readFileSync(NATIVE_LAUNCHER_VERSION_SOURCE, "utf8");
  const match = source.match(/internal const string Version = "([^"]+)"/);
  if (!match || match[1] !== PATCH_VERSION) {
    throw new Error(
      `Native launcher version does not match package.json: ${match?.[1] || "(missing)"} vs ${PATCH_VERSION}`,
    );
  }
  return match[1];
}

function validateNativeLauncher(launcherPath, { runSelfTest = true } = {}) {
  if (!fs.existsSync(launcherPath)) throw new Error(`Native launcher was not found: ${launcherPath}`);
  const stat = fs.statSync(launcherPath);
  if (!stat.isFile() || stat.size < 32 * 1024 || stat.size > 2 * 1024 * 1024) {
    throw new Error(`Native launcher size is outside the lightweight boundary: ${stat.size}`);
  }
  if (fs.readFileSync(launcherPath).subarray(0, 2).toString("ascii") !== "MZ") {
    throw new Error("Native launcher has no PE signature");
  }
  let selfTest = null;
  if (runSelfTest) {
    const result = spawnSync(launcherPath, ["-SelfTest"], {
      encoding: "utf8",
      timeout: 30_000,
      windowsHide: true,
    });
    if (result.error || result.status !== 0) {
      const detail = result.error?.message || result.stderr?.trim() || result.stdout?.trim() || `exit ${result.status}`;
      throw new Error(`Native launcher self-test failed: ${detail}`);
    }
    try {
      selfTest = JSON.parse(result.stdout.trim());
    } catch {
      throw new Error(`Native launcher self-test returned invalid JSON: ${result.stdout.slice(0, 300)}`);
    }
    if (
      selfTest.status !== "Passed" ||
      selfTest.launcherVersion !== PATCH_VERSION ||
      selfTest.nativeLauncher !== true ||
      selfTest.nativeInstaller !== true ||
      selfTest.directShortcut !== true ||
      selfTest.versionCatalog !== true ||
      selfTest.directorySize !== true ||
      selfTest.directLaunchTarget !== true ||
      selfTest.manualDeletion !== true ||
      selfTest.retentionPolicy !== true ||
      selfTest.criticalValidation !== true ||
      selfTest.minimumLauncherCompatibility !== true ||
      selfTest.staleCurrentDeletionBlocked !== true ||
      selfTest.staleCurrentRepairBlocked !== true ||
      selfTest.staleMetadataBlocked !== true ||
      selfTest.managerIcon !== true ||
      selfTest.shortcutRepair !== true ||
      selfTest.runtimePrerequisite !== true ||
      !Number.isInteger(selfTest.dotNetFrameworkRelease) ||
      selfTest.dotNetFrameworkRelease < 528040 ||
      selfTest.pathBoundary !== true ||
      selfTest.archiveBoundary !== true ||
      selfTest.powerShellChildProcesses !== 0
    ) {
      throw new Error("Native launcher self-test returned unexpected evidence");
    }
  }
  return {
    bytes: stat.size,
    file: path.basename(launcherPath),
    selfTest,
    sha256: sha256File(launcherPath),
    version: PATCH_VERSION,
  };
}

function compileNativeLauncher(outputPath) {
  if (process.platform !== "win32") throw new Error("The native launcher must be compiled on Windows");
  assertNativeLauncherSourceVersion();
  const compiler = findCSharpCompiler();
  if (!compiler) throw new Error("The .NET Framework C# compiler was not found");
  const wpfRoot = path.join(path.dirname(compiler), "WPF");
  const wpfReferences = [
    path.join(wpfRoot, "WindowsBase.dll"),
    path.join(wpfRoot, "PresentationCore.dll"),
    path.join(wpfRoot, "PresentationFramework.dll"),
    path.join(path.dirname(compiler), "System.Xaml.dll"),
  ];
  for (const reference of wpfReferences) {
    if (!fs.existsSync(reference)) throw new Error(`The .NET Framework WPF reference was not found: ${reference}`);
  }
  fs.mkdirSync(path.dirname(path.resolve(outputPath)), { recursive: true });
  const temporary = `${outputPath}.${process.pid}.${Date.now()}.tmp.exe`;
  try {
    const result = spawnSync(
      compiler,
      [
        "/nologo",
        "/target:winexe",
        "/optimize+",
        "/warn:4",
        "/warnaserror+",
        "/platform:x64",
        `/win32manifest:${NATIVE_LAUNCHER_MANIFEST}`,
        `/win32icon:${NATIVE_LAUNCHER_ICON}`,
        "/reference:System.Core.dll",
        "/reference:System.dll",
        "/reference:System.Drawing.dll",
        "/reference:System.IO.Compression.dll",
        "/reference:System.IO.Compression.FileSystem.dll",
        "/reference:System.Windows.Forms.dll",
        "/reference:System.Web.Extensions.dll",
        ...wpfReferences.map((reference) => `/reference:${reference}`),
        `/out:${temporary}`,
        ...NATIVE_LAUNCHER_SOURCES,
      ],
      { encoding: "utf8", timeout: 90_000, windowsHide: true },
    );
    if (result.error || result.status !== 0 || !fs.existsSync(temporary)) {
      const detail = result.error?.message || result.stderr?.trim() || result.stdout?.trim() || `exit ${result.status}`;
      throw new Error(`Native launcher compilation failed: ${detail}`);
    }
    const validation = validateNativeLauncher(temporary);
    fs.rmSync(outputPath, { force: true });
    fs.renameSync(temporary, outputPath);
    return { compiler, ...validation, file: path.basename(outputPath) };
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

if (require.main === module) {
  try {
    const index = process.argv.indexOf("--output");
    if (index === -1 || !process.argv[index + 1]) throw new Error("Usage: native-launcher.js --output <CodexPatchLauncher.exe>");
    process.stdout.write(`${JSON.stringify(compileNativeLauncher(path.resolve(process.argv[index + 1])), null, 2)}\n`);
  } catch (error) {
    process.stderr.write(`Native launcher build failed: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  NATIVE_LAUNCHER_FILENAME,
  NATIVE_LAUNCHER_ICON,
  NATIVE_LAUNCHER_ROOT,
  NATIVE_LAUNCHER_MANIFEST,
  NATIVE_LAUNCHER_SOURCES,
  assertNativeLauncherSourceVersion,
  compileNativeLauncher,
  validateNativeLauncher,
};
