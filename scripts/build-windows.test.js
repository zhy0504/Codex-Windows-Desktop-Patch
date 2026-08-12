"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const {
  assertSafeChild,
  compareApplicationTrees,
  createArtifactBase,
  createBundleName,
  createReleaseBundle,
  createZip,
  decodePercentNames,
  inspectArchivePaths,
  INTEGRITY_SIDECAR_FILENAME,
  parseArguments,
  verifyCriticalBinaries,
  writeIntegritySidecar,
} = require("./build-windows");
const { HELPER_FILENAME } = require("./helper");
const {
  NATIVE_LAUNCHER_FILENAME,
  compileNativeLauncher,
} = require("./native-launcher");
const { RESOLVER_FILENAME } = require("./resolver");
const { writeUpdateManifest } = require("./update-manifest");

function writeFile(root, relativePath, content) {
  const target = path.join(root, ...relativePath.split("/"));
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, content);
}

test("parses build arguments and rejects invalid argument lists", () => {
  const parsed = parseArguments([
    "--expected-version",
    "26.721.4979.0",
    "--msix",
    "official.msix",
    "--keep-work",
  ]);
  assert.equal(parsed.expectedVersion, "26.721.4979.0");
  assert.equal(parsed.msixPath, "official.msix");
  assert.equal(parsed.keepWork, true);
  assert.throws(() => parseArguments(["--msix"]), /requires a value/);
  assert.throws(
    () => parseArguments(["--expected-versoin", "26.721.4979.0"]),
    /Unknown argument/,
  );
  assert.throws(
    () => parseArguments(["--expected-version", "--keep-work"]),
    /requires a value/,
  );
  assert.throws(
    () => parseArguments(["--msix", "first.msix", "--msix", "second.msix"]),
    /Duplicate argument/,
  );
});

test("generated cleanup boundaries require a strict child path", () => {
  const root = path.resolve("C:\\safe-root");
  assert.equal(assertSafeChild(root, path.join(root, "child"), "test"), path.join(root, "child"));
  assert.throws(() => assertSafeChild(root, root, "test"), /unsafe/);
  assert.throws(() => assertSafeChild(root, path.dirname(root), "test"), /unsafe/);
});

test("uses a compact, path-safe portable artifact name", () => {
  const base = createArtifactBase("26.721.4979.0", "1.1.0");
  assert.equal(base, "CX-26.721.4979.0-p1.1.0");
  assert.ok(base.length <= 26);
  assert.throws(() => createArtifactBase("../latest", "1.1.0"), /Unsafe upstreamVersion/);
  assert.throws(() => createArtifactBase("26.721.4979.0", "next"), /Unsafe patchVersion/);
});

test("uses a deterministic release bundle name", () => {
  assert.equal(
    createBundleName("CX-26.721.4979.0-p1.2.0"),
    "CX-26.721.4979.0-p1.2.0-bundle.zip",
  );
  assert.throws(() => createBundleName("../latest"), /Unsafe artifact base/);
});

test("release bundle contains exactly the requested verified assets", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-release-bundle-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const first = path.join(root, "source-one");
  const second = path.join(root, "source-two");
  fs.writeFileSync(first, "one");
  fs.writeFileSync(second, "two");
  const bundlePath = path.join(root, "bundle.zip");
  const result = createReleaseBundle(bundlePath, {
    "CodexPatchLauncher.exe": first,
    "payload.json": second,
  });
  assert.deepEqual(result.entries, ["CodexPatchLauncher.exe", "payload.json"]);
  assert.match(result.sha256, /^[0-9a-f]{64}$/);
  assert.ok(result.bytes > 0);
});

test("reports the legacy Windows destination-path budget", () => {
  const longest = `resources/${"a".repeat(180)}`;
  const result = inspectArchivePaths(["Codex.exe", longest]);
  assert.equal(result.entries, 2);
  assert.equal(result.longestEntry, longest);
  assert.equal(result.longestEntryLength, longest.length);
  assert.equal(result.legacyMaxDestinationLength, 258 - longest.length);
  assert.throws(() => inspectArchivePaths(["./", "./Codex.exe"]), /explicit root entry/);
  assert.throws(() => inspectArchivePaths([".", "Codex.exe"]), /explicit root entry/);
  assert.throws(() => inspectArchivePaths(["./../escape.txt"]), /unsafe path/);
  assert.throws(() => inspectArchivePaths(["C:/escape.txt"]), /unsafe path/);
});

test("created ZIP omits the explicit root entry rejected by WinRAR", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-zip-root-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const source = path.join(root, "source");
  const archive = path.join(root, "portable.zip");
  writeFile(source, "Codex.exe", "codex");
  writeFile(source, "resources/app.asar", "asar");
  createZip(source, archive);
  const listed = spawnSync("tar.exe", ["-tf", archive], {
    encoding: "utf8",
    windowsHide: true,
  });
  assert.equal(listed.status, 0, listed.stderr || listed.error?.message);
  const entries = listed.stdout.split(/\r?\n/).filter(Boolean);
  assert.equal(entries.includes("."), false);
  assert.equal(entries.includes("./"), false);
  assert.ok(entries.includes("Codex.exe"));
  assert.ok(entries.includes("resources/app.asar"));

  const winRar = [
    path.join(process.env.ProgramFiles || "C:\\Program Files", "WinRAR", "WinRAR.exe"),
    path.join(process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)", "WinRAR", "WinRAR.exe"),
  ].find((candidate) => fs.existsSync(candidate));
  if (winRar) {
    const destination = path.join(root, "winrar");
    fs.mkdirSync(destination);
    const extracted = spawnSync(
      winRar,
      ["x", "-idq", "-o+", "-y", archive, `${destination}${path.sep}`],
      { encoding: "utf8", timeout: 60_000, windowsHide: true },
    );
    assert.equal(extracted.status, 0, extracted.stderr || extracted.stdout || extracted.error?.message);
    assert.equal(fs.readFileSync(path.join(destination, "Codex.exe"), "utf8"), "codex");
  }
});

test("application scope permits only the declared patch payloads", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-app-scope-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const upstream = path.join(root, "upstream");
  const output = path.join(root, "output");
  for (const directory of [upstream, output]) {
    writeFile(directory, "ChatGPT.exe", "chatgpt");
    writeFile(directory, "Codex.exe", "codex");
    writeFile(directory, "resources/codex.exe", "cli");
    writeFile(directory, "resources/app.asar", directory === upstream ? "old" : "patched");
  }
  writeFile(output, `resources/${RESOLVER_FILENAME}`, "resolver");
  writeFile(output, `resources/${HELPER_FILENAME}`, "helper");
  writeFile(output, NATIVE_LAUNCHER_FILENAME, "native-launcher");
  writeFile(output, INTEGRITY_SIDECAR_FILENAME, "integrity");
  const result = compareApplicationTrees(upstream, output);
  assert.deepEqual(result.added, [
    INTEGRITY_SIDECAR_FILENAME,
    NATIVE_LAUNCHER_FILENAME,
    `resources/${RESOLVER_FILENAME}`,
    `resources/${HELPER_FILENAME}`,
  ]);
  assert.deepEqual(result.changed, ["resources/app.asar"]);
  assert.deepEqual(result.removed, []);
  assert.equal(Object.keys(verifyCriticalBinaries(upstream, output)).length, 3);
  writeFile(output, "unexpected.txt", "bad");
  assert.throws(() => compareApplicationTrees(upstream, output), /scope check failed/);
});

test("decodes percent-encoded MSIX names and rejects collisions", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-msix-name-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  writeFile(root, "node_modules/%40scope/package.json", "{}");
  assert.equal(decodePercentNames(root), 1);
  assert.equal(fs.existsSync(path.join(root, "node_modules", "@scope", "package.json")), true);

  const collision = path.join(root, "collision");
  writeFile(collision, "%40scope/a.txt", "a");
  writeFile(collision, "@scope/b.txt", "b");
  assert.throws(() => decodePercentNames(collision), /collides/);
});

test(
  "native installer verifies and installs a release bundle without PowerShell",
  { skip: process.platform !== "win32" },
  (t) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-native-installer-test-"));
    t.after(() => fs.rmSync(root, { force: true, recursive: true }));
    const source = path.join(root, "source");
    const release = path.join(root, "release");
    const destination = path.join(root, "installed");
    fs.mkdirSync(release);
    writeFile(source, "Codex.exe", "codex");
    writeFile(source, "ChatGPT.exe", "chatgpt");
    writeFile(source, "resources/app.asar", "asar");
    writeFile(source, "resources/codex.exe", "worker");
    writeFile(source, `resources/${RESOLVER_FILENAME}`, "resolver");
    writeFile(source, "resources/codex-powershell-shim.exe", "shim");
    const versionLauncher = path.join(source, NATIVE_LAUNCHER_FILENAME);
    compileNativeLauncher(versionLauncher);

    const launcher = path.join(release, NATIVE_LAUNCHER_FILENAME);
    fs.copyFileSync(versionLauncher, launcher);
    const launcherHash = crypto.createHash("sha256").update(fs.readFileSync(launcher)).digest("hex");
    const verifiedPayloads = {};
    for (const relative of [
      "ChatGPT.exe",
      "Codex.exe",
      NATIVE_LAUNCHER_FILENAME,
      "resources/app.asar",
      "resources/codex.exe",
      `resources/${RESOLVER_FILENAME}`,
      "resources/codex-powershell-shim.exe",
    ]) {
      verifiedPayloads[relative] = crypto
        .createHash("sha256")
        .update(fs.readFileSync(path.join(source, ...relative.split("/"))))
        .digest("hex");
    }
    writeIntegritySidecar(source, {
      artifactBase: "CX-1.2.3.4-p2.3.0",
      msixVersion: "1.2.3.4",
      patchVersion: "2.3.0",
      verifiedPayloads,
    });
    const archive = path.join(release, "CX-1.2.3.4-p2.3.0.zip");
    createZip(source, archive);
    const hash = crypto.createHash("sha256").update(fs.readFileSync(archive)).digest("hex");
    fs.writeFileSync(`${archive}.sha256`, `${hash}  ${path.basename(archive)}\n`);
    const report = path.join(release, "CX-1.2.3.4-p2.3.0.verification.json");
    fs.writeFileSync(report, `${JSON.stringify({
      patchVersion: "2.3.0",
      upstream: { version: "1.2.3.4", signature: { status: "Valid" } },
      asar: { patch: { totalInternalPowerShellEliminatedTargets: 7 } },
      zip: { sha256: hash, verifiedPayloads },
      integritySidecar: {
        file: INTEGRITY_SIDECAR_FILENAME,
        schemaVersion: 1,
        releaseTag: "windows-msstore-1.2.3.4-desktop-patch-2.3.0",
        artifactBase: "CX-1.2.3.4-p2.3.0",
        msixVersion: "1.2.3.4",
        patchVersion: "2.3.0",
        verifiedPayloads,
      },
      nativeLauncher: { file: NATIVE_LAUNCHER_FILENAME, version: "2.3.0", sha256: launcherHash },
    })}\n`);
    writeUpdateManifest(path.join(release, "CodexPatch-update.json"), {
      builtAt: "2026-08-10T00:00:00.000Z",
      checksumPath: `${archive}.sha256`,
      launcherPath: launcher,
      msixVersion: "1.2.3.4",
      patchVersion: "2.3.0",
      reportPath: report,
      zipPath: archive,
    });

    const installed = spawnSync(
      launcher,
      ["-InstallOnly", "-InstallRoot", destination],
      { encoding: "utf8", timeout: 60_000, windowsHide: true },
    );
    assert.equal(installed.status, 0, installed.stderr || installed.stdout || installed.error?.message);
    const output = JSON.parse(installed.stdout);
    assert.equal(output.status, "Installed");
    assert.equal(fs.readFileSync(path.join(output.installPath, "Codex.exe"), "utf8"), "codex");
    assert.equal(fs.readFileSync(path.join(output.installPath, "ChatGPT.exe"), "utf8"), "chatgpt");
    assert.equal(
      fs.readFileSync(path.join(output.installPath, "resources", "codex-powershell-shim.exe"), "utf8"),
      "shim",
    );
    assert.equal(
      fs.readFileSync(path.join(output.installPath, "resources", RESOLVER_FILENAME), "utf8"),
      "resolver",
    );
    const current = JSON.parse(fs.readFileSync(path.join(destination, "current.json"), "utf8"));
    assert.equal(current.schemaVersion, 1);
    assert.equal(current.releaseTag, "windows-msstore-1.2.3.4-desktop-patch-2.3.0");
    assert.equal(current.activationReason, "native-installer");
    assert.equal(fs.existsSync(path.join(destination, NATIVE_LAUNCHER_FILENAME)), true);
    assert.equal(fs.existsSync(path.join(destination, "Start-CodexPatch.cmd")), false);
    assert.equal(fs.existsSync(path.join(destination, "Extract-CodexPatch.ps1")), false);
    const launcherValidation = spawnSync(
      path.join(destination, NATIVE_LAUNCHER_FILENAME),
      ["-SelfTest"],
      { encoding: "utf8", timeout: 30_000, windowsHide: true },
    );
    assert.equal(
      launcherValidation.status,
      0,
      launcherValidation.stderr || launcherValidation.stdout || launcherValidation.error?.message,
    );
    assert.equal(JSON.parse(launcherValidation.stdout).nativeInstaller, true);
    const marker = JSON.parse(
      fs.readFileSync(path.join(output.installPath, ".codex-patch-install.json"), "utf8"),
    );
    assert.equal(marker.releaseTag, current.releaseTag);
    assert.equal(marker.zipSha256, hash);

    fs.writeFileSync(path.join(output.installPath, "Codex.exe"), "corrupted-existing-install");
    const rejectedExisting = spawnSync(
      launcher,
      ["-InstallOnly", "-InstallRoot", destination],
      { encoding: "utf8", timeout: 60_000, windowsHide: true },
    );
    assert.equal(rejectedExisting.status, 1);
    assert.match(rejectedExisting.stderr, /failed critical-file verification/);
    assert.equal(
      fs.readFileSync(path.join(output.installPath, "Codex.exe"), "utf8"),
      "corrupted-existing-install",
    );
  },
);
