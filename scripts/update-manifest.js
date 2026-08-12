"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { createReleaseTag } = require("./release-plan");
const { sha256File } = require("./helper");

const UPDATE_CHANNEL = "stable";
const UPDATE_MANIFEST_FILENAME = "CodexPatch-update.json";
const UPDATE_REPOSITORY = "zhy0504/Codex-Windows-Desktop-Patch";
const UPDATE_SCHEMA_VERSION = 2;
const LAUNCHER_FILENAME = "CodexPatchLauncher.exe";
const MINIMUM_LAUNCHER_VERSION = "1.0.0";

function assertVersion(value, label) {
  if (typeof value !== "string" || !/^\d+(?:\.\d+)+$/.test(value)) {
    throw new Error(`Invalid ${label}: ${value}`);
  }
  return value;
}

function describeAsset(filePath, expectedName) {
  const resolved = path.resolve(filePath);
  if (!fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) {
    throw new Error(`Update asset was not found: ${resolved}`);
  }
  const name = path.basename(resolved);
  if (name !== expectedName) {
    throw new Error(`Unexpected update asset name: expected ${expectedName}, got ${name}`);
  }
  return {
    name,
    sha256: sha256File(resolved),
    size: fs.statSync(resolved).size,
  };
}

function validateAsset(asset, expectedName, label) {
  if (!asset || typeof asset !== "object" || Array.isArray(asset)) {
    throw new Error(`Update manifest ${label} asset is missing`);
  }
  if (asset.name !== expectedName) {
    throw new Error(`Update manifest ${label} name mismatch`);
  }
  if (!Number.isSafeInteger(asset.size) || asset.size <= 0) {
    throw new Error(`Update manifest ${label} size is invalid`);
  }
  if (typeof asset.sha256 !== "string" || !/^[0-9a-f]{64}$/.test(asset.sha256)) {
    throw new Error(`Update manifest ${label} SHA-256 is invalid`);
  }
}

function validateUpdateManifest(manifest) {
  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
    throw new Error("Update manifest must be an object");
  }
  if (manifest.schemaVersion !== UPDATE_SCHEMA_VERSION) {
    throw new Error(`Unsupported update manifest schema: ${manifest.schemaVersion}`);
  }
  if (manifest.channel !== UPDATE_CHANNEL) throw new Error("Update manifest channel mismatch");
  if (manifest.repository !== UPDATE_REPOSITORY) {
    throw new Error("Update manifest repository mismatch");
  }
  const msixVersion = assertVersion(manifest.msixVersion, "MSIX version");
  const patchVersion = assertVersion(manifest.patchVersion, "patch version");
  const launcherVersion = assertVersion(manifest.launcherVersion, "launcher version");
  const minimumLauncherVersion = assertVersion(manifest.minimumLauncherVersion, "minimum launcher version");
  const numeric = (value) => value.split(".").map((part) => BigInt(part));
  const left = numeric(launcherVersion);
  const right = numeric(minimumLauncherVersion);
  const length = Math.max(left.length, right.length);
  for (let index = 0; index < length; index += 1) {
    const launcherPart = left[index] || 0n;
    const minimumPart = right[index] || 0n;
    if (launcherPart > minimumPart) break;
    if (launcherPart < minimumPart) {
      throw new Error("Update manifest launcher version is below its minimum version");
    }
  }
  const artifactBase = `CX-${msixVersion}-p${patchVersion}`;
  if (manifest.artifactBase !== artifactBase) {
    throw new Error("Update manifest artifact base mismatch");
  }
  if (manifest.releaseTag !== createReleaseTag(msixVersion, patchVersion)) {
    throw new Error("Update manifest release tag mismatch");
  }
  if (typeof manifest.publishedAt !== "string" || !Number.isFinite(Date.parse(manifest.publishedAt))) {
    throw new Error("Update manifest publication time is invalid");
  }
  const expectedNames = {
    checksum: `${artifactBase}.zip.sha256`,
    launcher: LAUNCHER_FILENAME,
    verification: `${artifactBase}.verification.json`,
    zip: `${artifactBase}.zip`,
  };
  for (const [label, expectedName] of Object.entries(expectedNames)) {
    validateAsset(manifest.assets?.[label], expectedName, label);
  }
  return manifest;
}

function createUpdateManifest({
  builtAt,
  checksumPath,
  launcherPath,
  msixVersion,
  patchVersion,
  reportPath,
  zipPath,
}) {
  const safeMsixVersion = assertVersion(msixVersion, "MSIX version");
  const safePatchVersion = assertVersion(patchVersion, "patch version");
  const artifactBase = `CX-${safeMsixVersion}-p${safePatchVersion}`;
  const manifest = {
    schemaVersion: UPDATE_SCHEMA_VERSION,
    channel: UPDATE_CHANNEL,
    repository: UPDATE_REPOSITORY,
    releaseTag: createReleaseTag(safeMsixVersion, safePatchVersion),
    artifactBase,
    msixVersion: safeMsixVersion,
    patchVersion: safePatchVersion,
    launcherVersion: safePatchVersion,
    minimumLauncherVersion: MINIMUM_LAUNCHER_VERSION,
    publishedAt: new Date(builtAt).toISOString(),
    assets: {
      zip: describeAsset(zipPath, `${artifactBase}.zip`),
      checksum: describeAsset(checksumPath, `${artifactBase}.zip.sha256`),
      verification: describeAsset(reportPath, `${artifactBase}.verification.json`),
      launcher: describeAsset(launcherPath, LAUNCHER_FILENAME),
    },
  };
  return validateUpdateManifest(manifest);
}

function writeUpdateManifest(filePath, values) {
  const manifest = createUpdateManifest(values);
  fs.writeFileSync(filePath, `${JSON.stringify(manifest, null, 2)}\n`);
  return manifest;
}

module.exports = {
  LAUNCHER_FILENAME,
  MINIMUM_LAUNCHER_VERSION,
  UPDATE_CHANNEL,
  UPDATE_MANIFEST_FILENAME,
  UPDATE_REPOSITORY,
  UPDATE_SCHEMA_VERSION,
  createUpdateManifest,
  describeAsset,
  validateUpdateManifest,
  writeUpdateManifest,
};
