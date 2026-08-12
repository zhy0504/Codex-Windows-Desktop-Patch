"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const {
  LAUNCHER_FILENAME,
  UPDATE_MANIFEST_FILENAME,
  createUpdateManifest,
  validateUpdateManifest,
  writeUpdateManifest,
} = require("./update-manifest");

function write(root, name, content) {
  const target = path.join(root, name);
  fs.writeFileSync(target, content);
  return target;
}

test("creates a self-consistent GitHub update manifest", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-update-manifest-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const base = "CX-26.721.4979.0-p2.3.0";
  const values = {
    builtAt: "2026-07-29T12:00:00.000Z",
    checksumPath: write(root, `${base}.zip.sha256`, `${"a".repeat(64)}  ${base}.zip\n`),
    launcherPath: write(root, LAUNCHER_FILENAME, "launcher"),
    msixVersion: "26.721.4979.0",
    patchVersion: "2.3.0",
    reportPath: write(root, `${base}.verification.json`, "{}\n"),
    zipPath: write(root, `${base}.zip`, "zip"),
  };
  const manifest = createUpdateManifest(values);
  assert.equal(manifest.releaseTag, "windows-msstore-26.721.4979.0-desktop-patch-2.3.0");
  assert.equal(manifest.assets.zip.name, `${base}.zip`);
  assert.equal(manifest.assets.launcher.name, LAUNCHER_FILENAME);
  assert.match(manifest.assets.zip.sha256, /^[0-9a-f]{64}$/);
  const output = path.join(root, UPDATE_MANIFEST_FILENAME);
  writeUpdateManifest(output, values);
  assert.deepEqual(JSON.parse(fs.readFileSync(output, "utf8")), manifest);
});

test("rejects manifest identity, filename, and hash tampering", () => {
  const base = {
    schemaVersion: 2,
    channel: "stable",
    repository: "zhy0504/Codex-Windows-Desktop-Patch",
    releaseTag: "windows-msstore-26.721.4979.0-desktop-patch-2.3.0",
    artifactBase: "CX-26.721.4979.0-p2.3.0",
    msixVersion: "26.721.4979.0",
    patchVersion: "2.3.0",
    launcherVersion: "2.3.0",
    minimumLauncherVersion: "2.3.0",
    publishedAt: "2026-07-29T12:00:00.000Z",
    assets: Object.fromEntries(
      Object.entries({
        zip: "CX-26.721.4979.0-p2.3.0.zip",
        checksum: "CX-26.721.4979.0-p2.3.0.zip.sha256",
        verification: "CX-26.721.4979.0-p2.3.0.verification.json",
        launcher: "CodexPatchLauncher.exe",
      }).map(([key, name]) => [key, { name, sha256: "a".repeat(64), size: 1 }]),
    ),
  };
  assert.equal(validateUpdateManifest(base), base);
  assert.throws(
    () => validateUpdateManifest({ ...base, repository: "attacker/repository" }),
    /repository mismatch/,
  );
  const badName = structuredClone(base);
  badName.assets.zip.name = "payload.zip";
  assert.throws(() => validateUpdateManifest(badName), /zip name mismatch/);
  const badHash = structuredClone(base);
  badHash.assets.launcher.sha256 = "not-a-hash";
  assert.throws(() => validateUpdateManifest(badHash), /launcher SHA-256/);
  assert.throws(
    () => validateUpdateManifest({ ...base, launcherVersion: "1.9.9" }),
    /below its minimum/,
  );
});
