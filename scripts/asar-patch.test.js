"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const {
  DESKTOP_METADATA_ARGUMENT,
  ENVIRONMENT_PATCH_MARKER,
  MAX_INTEGRITY_BLOCKS,
  POWERSHELL_RESOLVER_EXPRESSION,
  REPLACEMENT_EXPRESSION,
  RUNTIME_INTERNAL_HELPER_EXPRESSION,
  WORKER_ENTRY,
  applyRuntimePatch,
  applyWorkerPatch,
  extractArchive,
  inspectArchive,
  inspectRuntimeSource,
  inspectWorkerSource,
  patchArchive,
  readArchive,
  readEntry,
  serializeHeader,
  sha256,
  verifyArchive,
  wrapRuntimeEnvironment,
} = require("./asar-patch");
const { RESOLVER_FILENAME, installResolver } = require("./resolver");
const { clearPowerShellResolutionCache } = require("../resources/codex-powershell-resolver");

const RUNTIME_ENTRY = ".vite/build/src-fixture.js";
const FAST_MODE_ENTRY = "webview/assets/app-initial-fixture.js";

function workerFixture() {
  return [
    "const exec = require('node:util').promisify(require('node:child_process').execFile);",
    "async function tree() {",
    "  return exec(`powershell.exe`, [`-NoProfile`, `-NonInteractive`, `-Command`, commandTree()], { windowsHide: !0 });",
    "}",
    "async function details() {",
    "  return exec('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', commandDetails()], { windowsHide: true });",
    "}",
    "function commandTree() {",
    "  return `Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId | ConvertTo-Json -Depth 2`;",
    "}",
    "function commandDetails(useProcessName = false) {",
    "  return `Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,${useProcessName ? `@{Name='CommandLine';Expression={$_.Name}}` : `CommandLine`},WorkingSetSize,@{Name='CpuPercent';Expression={$cpuByPid[[int]$_.ProcessId]}},@{Name='AgeSeconds';Expression={[int]((Get-Date) - $_.CreationDate).TotalSeconds}}`;",
    "}",
    "",
  ].join("\n");
}

function runtimeFixture() {
  return [
    "const exec = require('node:util').promisify(require('node:child_process').execFile);",
    "function runtimeConfig(e) {",
    "  const cli = { executablePath: 'codex.exe', args: ['app-server'] };",
    "  const environment = { ...process.env, LOG_FORMAT: 'json', RUST_LOG: 'warn', CODEX_INTERNAL_ORIGINATOR_OVERRIDE: 'desktop' };",
    "  if (e.hostConfig && e.resourcesPath) environment.RUNTIME_READY = '1';",
    "  return { executablePath: cli.executablePath, args: cli.args, env: normalize(environment) };",
    "}",
    "async function tree() {",
    "  return exec(`powershell.exe`, [`-NoProfile`, `-NonInteractive`, `-Command`, commandTree()], { windowsHide: !0 });",
    "}",
    "async function details() {",
    "  return exec('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', commandDetails()], { windowsHide: true });",
    "}",
    "async function executablePathLookup() {",
    "  return exec('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', `Get-CimInstance Win32_Process | Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Depth 2`], { windowsHide: true });",
    "}",
    "async function extractPrimaryRuntime(runCommand, command, args) {",
    "  return runCommand([`powershell.exe`, `-NoProfile`, `-NonInteractive`, `-ExecutionPolicy`, `Bypass`, `-Command`, command, ...args]);",
    "}",
    "const desktopMetadataScript = `function Decode-Rot13 {}\nfunction Get-UserAssistEntries {}\nfunction Get-AppProcessKeys {}\nfunction Find-BestUserAssistMatch {}\nGet-Process -ErrorAction SilentlyContinue\nGet-StartApps\nbundleId displayName appPath processKeys useCount\nConvertTo-Json -Compress -Depth 3`;",
    "async function readDesktopMetadata(runCommand) {",
    "  return runCommand([`powershell.exe`, `-NoProfile`, `-NonInteractive`, `-ExecutionPolicy`, `Bypass`, `-EncodedCommand`, Buffer.from(desktopMetadataScript, `utf16le`).toString(`base64`)]);",
    "}",
    "const recognizedShells = [`pwsh.exe`, `powershell.exe`];",
    "function commandTree() {",
    "  return `Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId | ConvertTo-Json -Depth 2`;",
    "}",
    "function commandDetails(useProcessName = false) {",
    "  return `Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,${useProcessName ? `@{Name='CommandLine';Expression={$_.Name}}` : `CommandLine`},WorkingSetSize,@{Name='CpuPercent';Expression={$cpuByPid[[int]$_.ProcessId]}},@{Name='AgeSeconds';Expression={[int]((Get-Date) - $_.CreationDate).TotalSeconds}}`;",
    "}",
    "function normalize(value) { return value; }",
    "",
  ].join("\n");
}

function fastModeUiFixture() {
  return [
    "function useServiceTierSettings(enabled, account, requirements) {",
    "  return enabled && account?.authMethod === `chatgpt` && requirements.featureRequirements.fast_mode !== false;",
    "}",
    "",
  ].join("\n");
}

function fastModeRequestFixture() {
  return [
    "function readServiceTierForRequest(authMethod, configuration) {",
    "  if (configuration.featureRequirements.fast_mode === false) return null;",
    '  return authMethod === "chatgpt" ? configuration.service_tier : null;',
    "}",
    "",
  ].join("\n");
}

function integrity(content, blockSize = 64) {
  const blocks = [];
  if (content.length === 0) {
    blocks.push(sha256(content));
  } else {
    for (let offset = 0; offset < content.length; offset += blockSize) {
      blocks.push(sha256(content.subarray(offset, Math.min(offset + blockSize, content.length))));
    }
  }
  return {
    algorithm: "SHA256",
    blockSize,
    blocks,
    hash: sha256(content),
  };
}

function createFixtureAsar(filePath) {
  const packageJson = Buffer.from('{"name":"fixture"}\n');
  const worker = Buffer.from(workerFixture());
  const runtime = Buffer.from(runtimeFixture());
  const fastMode = Buffer.from(
    `${fastModeUiFixture()}\n${fastModeRequestFixture()}`,
  );
  const tail = Buffer.from("tail-content-that-must-not-change");
  let offset = 0;
  const packed = (content) => {
    const entry = {
      integrity: integrity(content),
      offset: String(offset),
      size: content.length,
    };
    offset += content.length;
    return entry;
  };
  const header = {
    files: {
      ".vite": {
        files: {
          build: {
            files: {
              "child-process-snapshot-worker.js": packed(worker),
              "src-fixture.js": packed(runtime),
            },
          },
        },
      },
      webview: {
        files: {
          assets: {
            files: {
              "app-initial-fixture.js": packed(fastMode),
            },
          },
        },
      },
      "package.json": packed(packageJson),
      "tail.bin": packed(tail),
      "empty.txt": packed(Buffer.alloc(0)),
      "native.node": { size: 123, unpacked: true },
    },
  };
  fs.writeFileSync(
    filePath,
    Buffer.concat([
      serializeHeader(header),
      worker,
      runtime,
      fastMode,
      packageJson,
      tail,
    ]),
  );
  return { fastMode, packageJson, runtime, tail, worker };
}

test("patches exactly two structurally validated worker launch targets", () => {
  const original = workerFixture();
  assert.equal(inspectWorkerSource(original).state, "unpatched");
  const patched = applyWorkerPatch(original);
  assert.equal(patched.changed, true);
  assert.equal(patched.source.includes("powershell.exe"), false);
  assert.equal(patched.source.split(REPLACEMENT_EXPRESSION).length - 1, 2);
  assert.equal(inspectWorkerSource(patched.source).state, "patched");
  assert.equal(applyWorkerPatch(patched.source).changed, false);
});

test("rejects extra PowerShell launch literals and changed query markers", () => {
  assert.throws(
    () => applyWorkerPatch(`${workerFixture()}\nconst extra = "powershell.exe";\n`),
    /expected exactly two/,
  );
  assert.throws(
    () =>
      applyWorkerPatch(
        workerFixture().replace(
          "Win32_PerfFormattedData_PerfProc_Process",
          "Win32_PerfRawData_PerfProc_Process",
        ),
      ),
    /missing marker/,
  );
});

test("patches runtime queries and routes internal operations to the helper", () => {
  const original = runtimeFixture();
  assert.equal(inspectRuntimeSource(original).state, "unpatched");
  const patched = applyRuntimePatch(original);
  assert.equal(patched.changed, true);
  assert.equal(patched.source.split(REPLACEMENT_EXPRESSION).length - 1, 2);
  assert.equal(patched.source.split(RUNTIME_INTERNAL_HELPER_EXPRESSION).length - 1, 3);
  assert.equal(patched.source.split(POWERSHELL_RESOLVER_EXPRESSION).length - 1, 1);
  assert.equal(patched.source.split(DESKTOP_METADATA_ARGUMENT).length - 1, 1);
  assert.equal(patched.source.split(ENVIRONMENT_PATCH_MARKER).length - 1, 1);
  assert.equal(patched.source.split("powershell.exe").length - 1, 1);
  assert.equal(patched.source.includes("recognizedShells"), true);
  assert.equal(inspectRuntimeSource(patched.source).state, "patched");
  assert.equal(applyRuntimePatch(patched.source).changed, false);
});

test("rejects unknown or structurally changed runtime PowerShell launch targets", () => {
  assert.throws(
    () => applyRuntimePatch(`${runtimeFixture()}\nexec('powershell.exe', ['-NoProfile']);\n`),
    /unsupported executable-position/,
  );
  assert.throws(
    () =>
      applyRuntimePatch(
        runtimeFixture().replace(
          "Buffer.from(desktopMetadataScript, `utf16le`).toString(`base64`)",
          "desktopMetadataScript",
        ),
      ),
    /unsupported executable-position/,
  );
});

test("runtime environment patch keeps one PATH key and prefixes the resolved shell once", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-runtime-environment-"));
  const resourcesPath = path.join(root, "resources");
  const powerShell = path.join(root, "PowerShell", "7", "pwsh.exe");
  fs.mkdirSync(path.dirname(powerShell), { recursive: true });
  fs.writeFileSync(powerShell, "fixture");
  installResolver(path.join(resourcesPath, RESOLVER_FILENAME));
  clearPowerShellResolutionCache();
  t.after(() => {
    clearPowerShellResolutionCache();
    fs.rmSync(root, { force: true, recursive: true });
  });
  const environment = {
    Path: path.join(root, "system32"),
    PATH: path.join(root, "duplicate"),
    RUST_LOG: "warn",
  };
  const runtimeProcess = {
    env: { CODEX_PWSH_PATH: powerShell },
    resourcesPath,
  };
  const apply = new Function(
    "environment",
    "process",
    "require",
    `return ${wrapRuntimeEnvironment("environment")};`,
  );
  const first = apply(environment, runtimeProcess, require);
  const shellDirectory = path.dirname(powerShell);
  assert.deepEqual(
    Object.keys(first).filter((key) => key.toLowerCase() === "path"),
    ["Path"],
  );
  assert.equal(first.Path.split(path.delimiter)[0], shellDirectory);
  assert.equal(first.CODEX_PWSH_PATH, path.resolve(powerShell));

  const second = apply(first, runtimeProcess, require);
  assert.equal(
    second.Path.split(path.delimiter).filter((entry) => entry === shellDirectory).length,
    1,
  );
});

test("direct ASAR patch changes only validated PowerShell and Fast mode entries", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-asar-patch-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const sourcePath = path.join(root, "source.asar");
  const patchedPath = path.join(root, "patched.asar");
  const fixture = createFixtureAsar(sourcePath);

  const report = patchArchive(sourcePath, patchedPath);
  assert.deepEqual(report.logicalComparison.changed, [
    WORKER_ENTRY,
    RUNTIME_ENTRY,
    FAST_MODE_ENTRY,
  ]);
  assert.equal(report.logicalComparison.contentChanges, 3);
  assert.deepEqual(report.logicalComparison.added, []);
  assert.deepEqual(report.logicalComparison.removed, []);
  assert.deepEqual(report.logicalComparison.packingChanged, []);
  assert.equal(report.patch.workerTargets, 2);
  assert.equal(report.patch.runtimeQueryTargets, 2);
  assert.equal(report.patch.runtimeInternalHelperTargets, 3);
  assert.equal(report.patch.runtimeInternalPowerShellEliminatedTargets, 3);
  assert.equal(report.patch.runtimePowerShellDirectTargets, 0);
  assert.equal(report.patch.runtimePowerShellResolutionTargets, 1);
  assert.equal(report.patch.totalInternalPowerShellEliminatedTargets, 7);
  assert.match(report.patch.runtimeMetadataScriptSha256, /^[0-9a-f]{64}$/);
  assert.equal(report.patch.runtimeEnvironmentTargets, 1);
  assert.equal(report.patch.fastMode.authGateTargets, 2);
  assert.equal(report.patch.fastMode.expectedAuthGates, 2);
  assert.equal(report.patch.fastMode.changedEntries, 1);
  assert.deepEqual(report.patch.fastMode.changedPaths, [FAST_MODE_ENTRY]);
  assert.equal(report.patch.fastMode.effectiveApiKeyGates, 2);
  assert.equal(report.patch.fastMode.markerTargets, 2);
  assert.equal(report.patch.fastMode.sourceState, "unpatched");
  assert.equal(report.patch.fastMode.state, "patched");
  assert.equal(report.patch.fastMode.totalAuthGates, 2);
  const inspection = inspectArchive(patchedPath);
  assert.equal(inspection.workerState, "patched");
  assert.equal(inspection.runtimeState, "patched");
  assert.equal(inspection.fastMode.authGateTargets, 0);
  assert.equal(inspection.fastMode.effectiveApiKeyGates, 2);
  assert.equal(inspection.fastMode.state, "patched");

  const parsed = readArchive(patchedPath);
  const packageRecord = parsed.header.files["package.json"];
  const tailRecord = parsed.header.files["tail.bin"];
  const readPayload = (entry) =>
    parsed.archive.subarray(
      parsed.dataOffset + Number(entry.offset),
      parsed.dataOffset + Number(entry.offset) + Number(entry.size),
    );
  assert.deepEqual(readPayload(packageRecord), fixture.packageJson);
  assert.deepEqual(readPayload(tailRecord), fixture.tail);
  assert.equal(verifyArchive(parsed).packedEntries, 6);
});

test("refuses a source archive with stale integrity metadata", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-asar-integrity-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const sourcePath = path.join(root, "source.asar");
  const patchedPath = path.join(root, "patched.asar");
  createFixtureAsar(sourcePath);
  const bytes = fs.readFileSync(sourcePath);
  bytes[bytes.length - 1] ^= 0xff;
  fs.writeFileSync(sourcePath, bytes);
  assert.throws(() => patchArchive(sourcePath, patchedPath), /integrity mismatch/);
  assert.equal(fs.existsSync(patchedPath), false);
});

test("rejects invalid ASAR integrity block sizes without coercion", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-asar-block-size-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));

  const invalidBlockSizes = [
    0,
    -1,
    1.5,
    Number.MAX_SAFE_INTEGER + 1,
    true,
    [1],
    "1",
  ];
  for (const [index, blockSize] of invalidBlockSizes.entries()) {
    const sourcePath = path.join(root, `source-${index}.asar`);
    const patchedPath = path.join(root, `patched-${index}.asar`);
    createFixtureAsar(sourcePath);
    const parsed = readArchive(sourcePath);
    parsed.header.files.webview.files.assets.files["app-initial-fixture.js"].integrity.blockSize =
      blockSize;
    fs.writeFileSync(
      sourcePath,
      Buffer.concat([
        serializeHeader(parsed.header),
        parsed.archive.subarray(parsed.dataOffset),
      ]),
    );

    assert.throws(() => patchArchive(sourcePath, patchedPath), /Invalid ASAR integrity block size/);
    assert.equal(fs.existsSync(patchedPath), false);
  }
});

test("rejects integrity metadata that would generate too many blocks", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-asar-block-count-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const sourcePath = path.join(root, "source.asar");
  const patchedPath = path.join(root, "patched.asar");
  createFixtureAsar(sourcePath);
  const parsed = readArchive(sourcePath);
  const fastModeEntry = parsed.header.files.webview.files.assets.files["app-initial-fixture.js"];
  const originalContent = readEntry(parsed, fastModeEntry, FAST_MODE_ENTRY);
  const oversizedContent = Buffer.concat([
    originalContent,
    Buffer.alloc(MAX_INTEGRITY_BLOCKS + 1 - originalContent.length, 0x20),
  ]);
  fastModeEntry.size = oversizedContent.length;
  fastModeEntry.integrity = {
    algorithm: "SHA256",
    blockSize: 1,
    blocks: [],
    hash: sha256(oversizedContent),
  };
  const trailingContent = parsed.archive.subarray(
    parsed.dataOffset + Number(fastModeEntry.offset) + originalContent.length,
  );
  fs.writeFileSync(
    sourcePath,
    Buffer.concat([
      serializeHeader(parsed.header),
      parsed.archive.subarray(parsed.dataOffset, parsed.dataOffset + Number(fastModeEntry.offset)),
      oversizedContent,
      trailingContent,
    ]),
  );

  assert.throws(() => patchArchive(sourcePath, patchedPath), /block count exceeds limit/);
  assert.equal(fs.existsSync(patchedPath), false);
});

test("extracts all packed and unpacked fixture payloads", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-asar-extract-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const sourcePath = path.join(root, "source.asar");
  const fixture = createFixtureAsar(sourcePath);
  const unpackedPath = path.join(`${sourcePath}.unpacked`, "native.node");
  fs.mkdirSync(path.dirname(unpackedPath), { recursive: true });
  fs.writeFileSync(unpackedPath, Buffer.alloc(123, 7));
  const destination = path.join(root, "extracted");
  const result = extractArchive(sourcePath, destination);
  assert.equal(result.extractedFiles, 7);
  assert.equal(result.unpackedFiles, 1);
  assert.deepEqual(result.unpackedMetadataMismatches, []);
  assert.deepEqual(fs.readFileSync(path.join(destination, "tail.bin")), fixture.tail);
  assert.equal(fs.statSync(path.join(destination, "native.node")).size, 123);
});

test("records explicitly allowed metadata drift in an unpacked native module", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-asar-unpacked-metadata-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const sourcePath = path.join(root, "source.asar");
  createFixtureAsar(sourcePath);
  const unpackedPath = path.join(`${sourcePath}.unpacked`, "native.node");
  fs.mkdirSync(path.dirname(unpackedPath), { recursive: true });
  fs.writeFileSync(unpackedPath, Buffer.alloc(124, 9));

  assert.throws(
    () => extractArchive(sourcePath, path.join(root, "strict")),
    /Unpacked ASAR payload size mismatch/,
  );
  const result = extractArchive(sourcePath, path.join(root, "allowed"), {
    allowUnpackedMetadataMismatch: true,
  });
  assert.equal(result.unpackedFiles, 1);
  assert.deepEqual(result.unpackedMetadataMismatches, [
    {
      actualBytes: 124,
      actualSha256: sha256(Buffer.alloc(124, 9)),
      expectedBytes: 123,
      expectedSha256: null,
      path: "native.node",
    },
  ]);
});
