const fs = require("fs");
const path = require("path");
const { XMLParser } = require("fast-xml-parser");

const WINDOWS_PACKAGE_RE = /^OpenAI\.Codex_(\d+(?:\.\d+){2,3})_(x64|arm64)__[^/\\]+\.msix$/i;
const WINDOWS_PACKAGE_IDENTITY = {
  name: "OpenAI.Codex",
  publisher: "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B",
};
function parseWindowsPackageName(name) {
  if (typeof name !== "string") return null;
  const match = name.match(WINDOWS_PACKAGE_RE);
  if (!match) return null;
  return { version: match[1], architecture: match[2].toLowerCase() };
}

function selectWindowsPackage(packages, architecture = "x64") {
  const expectedArchitecture = String(architecture).toLowerCase();
  const candidates = packages
    .map((pkg) => ({ identity: parseWindowsPackageName(pkg.name), pkg }))
    .filter(({ identity }) => identity?.architecture === expectedArchitecture);
  const selected = candidates.reduce((latest, candidate) => {
    if (!latest) return candidate;
    const left = candidate.identity.version.split(".").map(BigInt);
    const right = latest.identity.version.split(".").map(BigInt);
    const length = Math.max(left.length, right.length);
    for (let index = 0; index < length; index += 1) {
      const difference = (left[index] || 0n) - (right[index] || 0n);
      if (difference > 0n) return candidate;
      if (difference < 0n) return latest;
    }
    return latest;
  }, null);

  if (selected) return selected.pkg;

  const available = packages.map((pkg) => pkg.name).filter(Boolean).join(", ") || "(none)";
  throw new Error(
    `No Windows ${expectedArchitecture} Codex MSIX package found. Available packages: ${available}`,
  );
}

function readWindowsPackageIdentity(extractDir) {
  const manifestPath = path.join(extractDir, "AppxManifest.xml");
  if (!fs.existsSync(manifestPath)) {
    throw new Error(`Windows package manifest not found: ${manifestPath}`);
  }

  const parser = new XMLParser({
    ignoreAttributes: false,
    attributeNamePrefix: "@_",
    removeNSPrefix: true,
  });
  const parsed = parser.parse(fs.readFileSync(manifestPath, "utf-8"));
  const identity = parsed.Package?.Identity;
  const architecture = identity?.["@_ProcessorArchitecture"];
  const version = identity?.["@_Version"];

  if (!architecture || !version) {
    throw new Error(`Windows package manifest has no architecture or version: ${manifestPath}`);
  }

  return {
    architecture: String(architecture).toLowerCase(),
    name: String(identity["@_Name"] || ""),
    publisher: String(identity["@_Publisher"] || ""),
    version: String(version),
  };
}

function validateWindowsPackage(extractDir, expected) {
  const actual = readWindowsPackageIdentity(extractDir);
  const expectedArchitecture = String(expected.architecture).toLowerCase();

  if (actual.architecture !== expectedArchitecture) {
    throw new Error(
      `Windows package architecture mismatch: expected ${expectedArchitecture}, got ${actual.architecture}`,
    );
  }
  if (expected.version && actual.version !== expected.version) {
    throw new Error(
      `Windows package version mismatch: expected ${expected.version}, got ${actual.version}`,
    );
  }
  if (expected.name && actual.name !== expected.name) {
    throw new Error(
      `Windows package name mismatch: expected ${expected.name}, got ${actual.name || "(missing)"}`,
    );
  }
  if (expected.publisher && actual.publisher !== expected.publisher) {
    throw new Error(
      `Windows package publisher mismatch: expected ${expected.publisher}, got ${actual.publisher || "(missing)"}`,
    );
  }

  return actual;
}

module.exports = {
  WINDOWS_PACKAGE_IDENTITY,
  parseWindowsPackageName,
  readWindowsPackageIdentity,
  selectWindowsPackage,
  validateWindowsPackage,
};
