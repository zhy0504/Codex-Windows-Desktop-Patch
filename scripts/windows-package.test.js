const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  WINDOWS_PACKAGE_IDENTITY,
  parseWindowsPackageName,
  selectWindowsPackage,
  validateWindowsPackage,
} = require("./windows-package");

const ARM64_PACKAGE = {
  name: "OpenAI.Codex_26.721.41059.0_arm64__2p2nqsd0c76g0.msix",
};
const X64_PACKAGE = {
  name: "OpenAI.Codex_26.721.41059.0_x64__2p2nqsd0c76g0.msix",
};

function createExtractDir(t, architecture, version = "26.721.41059.0") {
  const extractDir = fs.mkdtempSync(path.join(os.tmpdir(), "codex-msix-test-"));
  t.after(() => fs.rmSync(extractDir, { recursive: true, force: true }));
  fs.writeFileSync(
    path.join(extractDir, "AppxManifest.xml"),
    `<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="OpenAI.Codex" Publisher="${WINDOWS_PACKAGE_IDENTITY.publisher}" ProcessorArchitecture="${architecture}" Version="${version}" />
</Package>\n`,
  );
  return extractDir;
}

test("parses the Codex MSIX package identity", () => {
  assert.deepEqual(parseWindowsPackageName(X64_PACKAGE.name), {
    version: "26.721.41059.0",
    architecture: "x64",
  });
});
test("selects x64 even when arm64 is listed first", () => {
  assert.equal(selectWindowsPackage([ARM64_PACKAGE, X64_PACKAGE]), X64_PACKAGE);
});

test("selects the highest numeric version when multiple x64 packages are listed", () => {
  const older = { name: "OpenAI.Codex_26.99.99999.0_x64__publisher.msix" };
  const latest = { name: "OpenAI.Codex_26.803.1.0_x64__publisher.msix" };
  assert.equal(selectWindowsPackage([older, latest, X64_PACKAGE]), latest);
});

test("does not select an unrelated x64 dependency", () => {
  assert.throws(
    () => selectWindowsPackage([{
      name: "Microsoft.VCLibs_14.0.0.0_x64__8wekyb3d8bbwe.msix",
    }]),
    /No Windows x64 Codex MSIX package found/,
  );
});

test("accepts a matching extracted package manifest", (t) => {
  const extractDir = createExtractDir(t, "x64");
  assert.deepEqual(
    validateWindowsPackage(extractDir, {
      architecture: "x64",
      ...WINDOWS_PACKAGE_IDENTITY,
      version: "26.721.41059.0",
    }),
    {
      architecture: "x64",
      name: "OpenAI.Codex",
      publisher: WINDOWS_PACKAGE_IDENTITY.publisher,
      version: "26.721.41059.0",
    },
  );
});

test("rejects an extracted package with the wrong Store publisher", (t) => {
  const extractDir = createExtractDir(t, "x64");
  const manifestPath = path.join(extractDir, "AppxManifest.xml");
  fs.writeFileSync(
    manifestPath,
    fs.readFileSync(manifestPath, "utf8").replace(WINDOWS_PACKAGE_IDENTITY.publisher, "CN=Untrusted"),
  );
  assert.throws(
    () => validateWindowsPackage(extractDir, {
      architecture: "x64",
      ...WINDOWS_PACKAGE_IDENTITY,
    }),
    /publisher mismatch/,
  );
});

test("rejects an extracted package with the wrong architecture", (t) => {
  const extractDir = createExtractDir(t, "arm64");
  assert.throws(
    () => validateWindowsPackage(extractDir, { architecture: "x64" }),
    /architecture mismatch: expected x64, got arm64/,
  );
});

test("rejects an extracted package with the wrong version", (t) => {
  const extractDir = createExtractDir(t, "x64", "26.721.41060.0");
  assert.throws(
    () => validateWindowsPackage(extractDir, {
      architecture: "x64",
      version: "26.721.41059.0",
    }),
    /version mismatch: expected 26\.721\.41059\.0, got 26\.721\.41060\.0/,
  );
});
