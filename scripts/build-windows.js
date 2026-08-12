"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawn, spawnSync } = require("node:child_process");
const { Worker } = require("node:worker_threads");
const {
  WORKER_ENTRY,
  extractArchive,
  findEntry,
  patchArchive,
  readArchive,
  readEntry,
} = require("./asar-patch");
const {
  HELPER_FILENAME,
  PATCH_VERSION,
  compileHelper,
  findCSharpCompiler,
  sha256File,
  validateHelper,
} = require("./helper");
const { RESOLVER_FILENAME, installResolver } = require("./resolver");
const {
  NATIVE_LAUNCHER_FILENAME,
  compileNativeLauncher,
} = require("./native-launcher");
const {
  LAUNCHER_FILENAME,
  UPDATE_MANIFEST_FILENAME,
  writeUpdateManifest,
} = require("./update-manifest");
const { detectLatestWindowsPackage, downloadPackage } = require("./upstream");
const { createReleaseTag } = require("./release-plan");
const {
  WINDOWS_PACKAGE_IDENTITY,
  parseWindowsPackageName,
  validateWindowsPackage,
} = require("./windows-package");

const PROJECT_ROOT = path.resolve(__dirname, "..");
const DEFAULT_OUT_ROOT = path.join(PROJECT_ROOT, "out");
const LEGACY_WINDOWS_MAX_PATH = 259;
const INTEGRITY_SIDECAR_FILENAME = "CodexPatch-integrity.json";
const BUILD_ARGUMENTS = new Set(["--expected-version", "--keep-work", "--msix", "--out-dir"]);

function createArtifactBase(upstreamVersion, patchVersion = PATCH_VERSION) {
  for (const [label, value] of Object.entries({ patchVersion, upstreamVersion })) {
    if (typeof value !== "string" || !/^\d+(?:\.\d+)+$/.test(value)) {
      throw new Error(`Unsafe ${label} for artifact name: ${value}`);
    }
  }
  return `CX-${upstreamVersion}-p${patchVersion}`;
}

function createBundleName(artifactBase) {
  if (typeof artifactBase !== "string" || !/^CX-\d+(?:\.\d+){2,3}-p\d+(?:\.\d+)+$/.test(artifactBase)) {
    throw new Error(`Unsafe artifact base for bundle name: ${artifactBase}`);
  }
  return `${artifactBase}-bundle.zip`;
}

function parseArguments(args) {
  const values = new Map();
  const seen = new Set();
  let keepWork = false;
  for (let index = 0; index < args.length; index += 1) {
    const name = args[index];
    if (!BUILD_ARGUMENTS.has(name)) {
      throw new Error(`Unknown argument: ${name}`);
    }
    if (seen.has(name)) throw new Error(`Duplicate argument: ${name}`);
    seen.add(name);
    if (name === "--keep-work") {
      keepWork = true;
      continue;
    }
    const value = args[index + 1];
    if (!value || value.startsWith("--")) throw new Error(`${name} requires a value`);
    values.set(name, value);
    index += 1;
  }
  return {
    expectedVersion: values.get("--expected-version") || null,
    keepWork,
    msixPath: values.get("--msix") || null,
    outRoot: path.resolve(values.get("--out-dir") || DEFAULT_OUT_ROOT),
  };
}

function assertSafeChild(parent, target, label) {
  const parentPath = path.resolve(parent);
  const targetPath = path.resolve(target);
  const relative = path.relative(parentPath, targetPath);
  if (!relative || relative.startsWith("..") || path.isAbsolute(relative)) {
    throw new Error(`Refusing unsafe ${label} path: ${targetPath}`);
  }
  return targetPath;
}

function removeGeneratedDirectory(parent, target) {
  const safe = assertSafeChild(parent, target, "generated directory");
  fs.rmSync(safe, { force: true, recursive: true });
}

function findSevenZip() {
  const candidates = [
    path.join(process.env.ProgramFiles || "C:\\Program Files", "7-Zip", "7z.exe"),
    path.join(process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)", "7-Zip", "7z.exe"),
    "7z.exe",
    "7z",
  ];
  for (const candidate of candidates) {
    const result = spawnSync(candidate, ["i"], {
      encoding: "utf8",
      timeout: 10_000,
      windowsHide: true,
    });
    if (!result.error && result.status === 0) return candidate;
  }
  return null;
}

function extractMsix(msixPath, destinationPath) {
  const sevenZip = findSevenZip();
  fs.mkdirSync(destinationPath, { recursive: true });
  const executable = sevenZip || "tar.exe";
  const argumentsList = sevenZip
    ? ["x", "-y", `-o${destinationPath}`, msixPath]
    : ["-xf", msixPath, "-C", destinationPath];
  const result = spawnSync(
    executable,
    argumentsList,
    {
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024,
      timeout: 10 * 60_000,
      windowsHide: true,
    },
  );
  if (result.error || result.status !== 0) {
    const detail =
      result.error?.message || result.stderr?.trim() || result.stdout?.trim() || `exit ${result.status}`;
    throw new Error(`MSIX extraction failed: ${detail}`);
  }
  return {
    decodedNames: decodePercentNames(destinationPath),
    tool: sevenZip || "tar.exe",
  };
}

function decodePercentNames(root) {
  if (!fs.existsSync(root)) return 0;
  let renamed = 0;
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const current = path.join(directory, entry.name);
      if (entry.isDirectory()) walk(current);
      if (!/%[0-9a-fA-F]{2}/.test(entry.name)) continue;
      let decoded;
      try {
        decoded = decodeURIComponent(entry.name);
      } catch {
        throw new Error(`Invalid percent-encoded MSIX path name: ${entry.name}`);
      }
      if (
        !decoded ||
        decoded === "." ||
        decoded === ".." ||
        /[\\/:*?"<>|\0]/.test(decoded)
      ) {
        throw new Error(`Unsafe decoded MSIX path name: ${entry.name}`);
      }
      if (decoded === entry.name) continue;
      const target = path.join(directory, decoded);
      if (fs.existsSync(target)) {
        throw new Error(`Decoded MSIX path collides with an existing entry: ${target}`);
      }
      fs.renameSync(current, target);
      renamed += 1;
    }
  };
  walk(root);
  return renamed;
}

function findSignTool() {
  const configured = process.env.SIGNTOOL_PATH;
  if (configured) {
    const resolved = path.resolve(configured);
    if (!fs.existsSync(resolved)) {
      throw new Error(`SIGNTOOL_PATH does not exist: ${resolved}`);
    }
    return resolved;
  }
  const sdkRoot = path.join(
    process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)",
    "Windows Kits",
    "10",
    "bin",
  );
  if (!fs.existsSync(sdkRoot)) return null;
  const versions = fs
    .readdirSync(sdkRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && /^\d+(?:\.\d+)+$/.test(entry.name))
    .map((entry) => entry.name)
    .sort((left, right) => right.localeCompare(left, undefined, { numeric: true }));
  for (const version of versions) {
    const candidate = path.join(sdkRoot, version, "x64", "signtool.exe");
    if (fs.existsSync(candidate)) return candidate;
  }
  return null;
}

function verifyAuthenticodeSignature(filePath, label) {
  if (process.platform !== "win32") {
    throw new Error(`${label} signature verification requires Windows`);
  }
  const signTool = findSignTool();
  if (!signTool) throw new Error("Windows SDK SignTool was not found");
  const result = spawnSync(signTool, ["verify", "/pa", "/all", "/v", filePath], {
    encoding: "utf8",
    maxBuffer: 8 * 1024 * 1024,
    timeout: 60_000,
    windowsHide: true,
  });
  if (result.error || result.status !== 0) {
    const detail =
      result.error?.message || result.stderr?.trim() || result.stdout?.trim() || `exit ${result.status}`;
    throw new Error(`${label} signature validation failed: ${detail}`);
  }
  return {
    status: "Valid",
    verifier: "Windows SDK SignTool /pa /all",
  };
}

function verifyMsixSignature(msixPath) {
  return verifyAuthenticodeSignature(msixPath, "MSIX");
}

function preflightBuildTools() {
  const sevenZip = findSevenZip();
  const signTool = findSignTool();
  if (!signTool) throw new Error("Windows SDK SignTool was not found");
  const compiler = findCSharpCompiler();
  if (!compiler) throw new Error("The .NET Framework C# compiler was not found");
  const tar = spawnSync("tar.exe", ["--version"], {
    encoding: "utf8",
    timeout: 10_000,
    windowsHide: true,
  });
  if (tar.error || tar.status !== 0) throw new Error("Windows tar.exe was not found");
  return { compiler, extractor: sevenZip || "tar.exe", signTool };
}

function walkFiles(root, directory = root, output = []) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) walkFiles(root, fullPath, output);
    else if (entry.isFile()) output.push(path.relative(root, fullPath).replace(/\\/g, "/"));
    else throw new Error(`Unsupported file type in application tree: ${fullPath}`);
  }
  return output;
}

function createDirectoryManifest(root) {
  const manifest = new Map();
  for (const relativePath of walkFiles(root).sort()) {
    const fullPath = path.join(root, ...relativePath.split("/"));
    manifest.set(relativePath, {
      bytes: fs.statSync(fullPath).size,
      sha256: sha256File(fullPath),
    });
  }
  return manifest;
}

function compareApplicationTrees(upstreamAppDir, outputAppDir) {
  const upstream = createDirectoryManifest(upstreamAppDir);
  const output = createDirectoryManifest(outputAppDir);
  const added = [...output.keys()].filter((file) => !upstream.has(file));
  const removed = [...upstream.keys()].filter((file) => !output.has(file));
  const changed = [...upstream.keys()].filter((file) => {
    const after = output.get(file);
    return after && JSON.stringify(upstream.get(file)) !== JSON.stringify(after);
  });
  const expectedAdded = [
    INTEGRITY_SIDECAR_FILENAME,
    NATIVE_LAUNCHER_FILENAME,
    `resources/${RESOLVER_FILENAME}`,
    `resources/${HELPER_FILENAME}`,
  ];
  const expectedChanged = ["resources/app.asar"];
  if (
    JSON.stringify(added) !== JSON.stringify(expectedAdded) ||
    removed.length !== 0 ||
    JSON.stringify(changed) !== JSON.stringify(expectedChanged)
  ) {
    throw new Error(
      "Generated application scope check failed: " +
        JSON.stringify({ added, changed, removed }),
    );
  }
  return {
    added,
    changed,
    filesAfter: output.size,
    filesBefore: upstream.size,
    removed,
  };
}

function writeIntegritySidecar(outputAppDir, { artifactBase, msixVersion, patchVersion, verifiedPayloads }) {
  const releaseTag = createReleaseTag(msixVersion, patchVersion);
  const expectedArtifact = createArtifactBase(msixVersion, patchVersion);
  if (artifactBase !== expectedArtifact) throw new Error("Integrity sidecar artifact identity mismatch");
  const expectedPaths = [
    "ChatGPT.exe",
    "Codex.exe",
    NATIVE_LAUNCHER_FILENAME,
    "resources/app.asar",
    "resources/codex.exe",
    `resources/${RESOLVER_FILENAME}`,
    `resources/${HELPER_FILENAME}`,
  ];
  if (
    !verifiedPayloads ||
    JSON.stringify(Object.keys(verifiedPayloads)) !== JSON.stringify(expectedPaths) ||
    Object.values(verifiedPayloads).some((digest) => !/^[0-9a-f]{64}$/.test(digest))
  ) {
    throw new Error("Integrity sidecar requires the exact verified critical payload set");
  }
  const sidecar = {
    schemaVersion: 1,
    releaseTag,
    artifactBase,
    msixVersion,
    patchVersion,
    verifiedPayloads,
  };
  const sidecarPath = path.join(outputAppDir, INTEGRITY_SIDECAR_FILENAME);
  fs.writeFileSync(sidecarPath, `${JSON.stringify(sidecar, null, 2)}\n`, "utf8");
  return { path: sidecarPath, value: sidecar };
}

function verifyCriticalBinaries(upstreamAppDir, outputAppDir) {
  const required = [
    "ChatGPT.exe",
    "Codex.exe",
    "resources/codex.exe",
  ];
  const hashes = {};
  for (const relativePath of required) {
    const source = path.join(upstreamAppDir, ...relativePath.split("/"));
    const output = path.join(outputAppDir, ...relativePath.split("/"));
    if (!fs.existsSync(source) || !fs.existsSync(output)) {
      throw new Error(`Required official executable is missing: ${relativePath}`);
    }
    const before = sha256File(source);
    const after = sha256File(output);
    if (before !== after) throw new Error(`Official executable changed: ${relativePath}`);
    hashes[relativePath] = before;
  }
  return hashes;
}

function countEmbeddedHeaderHashes(appDir, headerHash) {
  let matches = 0;
  for (const relativePath of ["ChatGPT.exe", "Codex.exe", "resources/codex.exe"]) {
    const filePath = path.join(appDir, ...relativePath.split("/"));
    if (fs.readFileSync(filePath).includes(Buffer.from(headerHash, "ascii"))) matches += 1;
  }
  return matches;
}

function containsProcessId(value, processId) {
  if (!value || typeof value !== "object") return false;
  if (value.pid === processId || value.ProcessId === processId) return true;
  if (Array.isArray(value)) return value.some((item) => containsProcessId(item, processId));
  return Object.values(value).some((item) => containsProcessId(item, processId));
}

async function runWorkerSmoke(asarPath, resourcesDir, scratchDir) {
  const parsed = readArchive(asarPath);
  const worker = findEntry(parsed.header, WORKER_ENTRY);
  if (!worker) throw new Error(`Worker is missing from patched ASAR: ${WORKER_ENTRY}`);
  const expectedWorker = readEntry(parsed, worker.entry, WORKER_ENTRY);
  const extractRoot = path.join(scratchDir, "extracted");
  const extraction = extractArchive(asarPath, extractRoot, {
    // Unpacked payloads can be Authenticode-signed after ASAR metadata is generated.
    allowUnpackedMetadataMismatch: true,
  });
  const unpackedRoot = `${asarPath}.unpacked`;
  const unpackedPrefix = `${path.resolve(unpackedRoot)}${path.sep}`;
  for (const mismatch of extraction.unpackedMetadataMismatches) {
    const payload = path.resolve(unpackedRoot, ...mismatch.path.split("/"));
    if (!payload.startsWith(unpackedPrefix)) {
      throw new Error(`Unpacked ASAR signature path escaped its root: ${mismatch.path}`);
    }
    mismatch.signature = verifyAuthenticodeSignature(
      payload,
      `Unpacked ASAR payload ${mismatch.path}`,
    );
  }
  const workerPath = path.join(extractRoot, ...WORKER_ENTRY.split("/"));
  if (sha256File(workerPath) !== crypto.createHash("sha256").update(expectedWorker).digest("hex")) {
    throw new Error("Full ASAR extraction produced a different worker payload");
  }
  const wrapperPath = path.join(scratchDir, "worker-smoke-wrapper.cjs");
  fs.writeFileSync(
    wrapperPath,
    `process.resourcesPath=${JSON.stringify(resourcesDir)};require(${JSON.stringify(workerPath)});\n`,
  );

  const dummy = spawn(process.execPath, ["-e", "setTimeout(() => {}, 15000)"], {
    stdio: "ignore",
    windowsHide: true,
  });
  await new Promise((resolve, reject) => {
    dummy.once("spawn", resolve);
    dummy.once("error", reject);
  });
  let workerThread;
  try {
    await new Promise((resolve) => setTimeout(resolve, 100));
    const message = await new Promise((resolve, reject) => {
      workerThread = new Worker(wrapperPath, { workerData: process.pid });
      const timer = setTimeout(
        () => reject(new Error("Patched worker smoke test timed out")),
        20_000,
      );
      workerThread.once("message", (value) => {
        clearTimeout(timer);
        resolve(value);
      });
      workerThread.once("error", (error) => {
        clearTimeout(timer);
        reject(error);
      });
      workerThread.once("exit", (code) => {
        if (code !== 0) {
          clearTimeout(timer);
          reject(new Error(`Patched worker exited before a result: ${code}`));
        }
      });
    });
    if (!containsProcessId(message, dummy.pid)) {
      throw new Error("Patched worker did not report its controlled child process");
    }
    return {
      controlledChildObserved: true,
      extraction,
      status: typeof message.status === "string" ? message.status : null,
    };
  } finally {
    if (workerThread) await workerThread.terminate().catch(() => {});
    dummy.kill();
  }
}

function createZip(appDir, zipPath) {
  fs.rmSync(zipPath, { force: true });
  const topLevelEntries = fs.readdirSync(appDir).sort();
  if (topLevelEntries.length === 0) {
    throw new Error(`Cannot create a ZIP from an empty directory: ${appDir}`);
  }
  for (const entry of topLevelEntries) {
    if (!entry || entry === "." || entry === ".." || /[\r\n]/.test(entry)) {
      throw new Error(`Unsafe top-level ZIP entry: ${JSON.stringify(entry)}`);
    }
  }
  const result = spawnSync(
    "tar.exe",
    ["-a", "-c", "-f", zipPath, "-C", appDir, "--", ...topLevelEntries],
    {
      encoding: "utf8",
      maxBuffer: 8 * 1024 * 1024,
      timeout: 30 * 60_000,
      windowsHide: true,
    },
  );
  if (result.error || result.status !== 0 || !fs.existsSync(zipPath)) {
    const detail =
      result.error?.message || result.stderr?.trim() || result.stdout?.trim() || `exit ${result.status}`;
    throw new Error(`ZIP creation failed: ${detail}`);
  }
}

function createReleaseBundle(bundlePath, assets) {
  const entries = Object.entries(assets);
  if (entries.length === 0) throw new Error("Release bundle requires at least one asset");
  const names = new Set();
  for (const [name, source] of entries) {
    if (!/^[0-9A-Za-z._-]+$/.test(name) || names.has(name)) {
      throw new Error(`Unsafe or duplicate release bundle entry: ${name}`);
    }
    if (!fs.existsSync(source) || !fs.statSync(source).isFile()) {
      throw new Error(`Release bundle source was not found: ${source}`);
    }
    names.add(name);
  }

  const staging = fs.mkdtempSync(path.join(os.tmpdir(), "codex-patch-bundle-"));
  const extraction = fs.mkdtempSync(path.join(os.tmpdir(), "codex-patch-bundle-check-"));
  try {
    for (const [name, source] of entries) fs.copyFileSync(source, path.join(staging, name));
    createZip(staging, bundlePath);
    const listing = spawnSync("tar.exe", ["-tf", bundlePath], {
      encoding: "utf8",
      maxBuffer: 8 * 1024 * 1024,
      timeout: 5 * 60_000,
      windowsHide: true,
    });
    if (listing.error || listing.status !== 0) {
      throw new Error(`Release bundle listing failed: ${listing.error?.message || listing.stderr}`);
    }
    const inspected = inspectArchivePaths(listing.stdout.split(/\r?\n/));
    const listedNames = inspected.paths.map((entry) => entry.normalized).sort();
    const expectedNames = [...names].sort();
    if (JSON.stringify(listedNames) !== JSON.stringify(expectedNames)) {
      throw new Error("Release bundle entries do not match the requested assets");
    }
    const extracted = spawnSync("tar.exe", ["-xf", bundlePath, "-C", extraction], {
      encoding: "utf8",
      maxBuffer: 8 * 1024 * 1024,
      timeout: 30 * 60_000,
      windowsHide: true,
    });
    if (extracted.error || extracted.status !== 0) {
      throw new Error(`Release bundle extraction failed: ${extracted.error?.message || extracted.stderr}`);
    }
    for (const [name, source] of entries) {
      if (sha256File(path.join(extraction, name)) !== sha256File(source)) {
        throw new Error(`Release bundle payload hash mismatch: ${name}`);
      }
    }
    return {
      bytes: fs.statSync(bundlePath).size,
      entries: expectedNames,
      sha256: sha256File(bundlePath),
    };
  } finally {
    fs.rmSync(staging, { force: true, recursive: true });
    fs.rmSync(extraction, { force: true, recursive: true });
  }
}

function inspectArchivePaths(rawPaths) {
  const paths = rawPaths
    .filter(Boolean)
    .map((raw) => ({
      normalized: raw.replace(/\\/g, "/").replace(/^\.\//, ""),
      raw,
    }));
  if (paths.length === 0) throw new Error("ZIP contains no entries");
  for (const entry of paths) {
    if (!entry.normalized || entry.normalized === ".") {
      throw new Error(`ZIP contains an explicit root entry incompatible with WinRAR: ${entry.raw}`);
    }
    const segments = entry.normalized.split("/");
    if (
      entry.normalized.startsWith("/") ||
      /^[A-Za-z]:\//.test(entry.normalized) ||
      segments.includes("..")
    ) {
      throw new Error(`ZIP contains an unsafe path: ${entry.raw}`);
    }
  }
  const longest = paths.reduce((current, entry) =>
    entry.normalized.length > current.normalized.length ? entry : current,
  );
  return {
    entries: paths.length,
    legacyMaxDestinationLength:
      LEGACY_WINDOWS_MAX_PATH - 1 - longest.normalized.length,
    legacyMaxPath: LEGACY_WINDOWS_MAX_PATH,
    longestEntry: longest.normalized,
    longestEntryLength: longest.normalized.length,
    paths,
  };
}

function verifyZip(zipPath, expectedHashes) {
  const listingResult = spawnSync("tar.exe", ["-tf", zipPath], {
    encoding: "utf8",
    maxBuffer: 32 * 1024 * 1024,
    timeout: 5 * 60_000,
    windowsHide: true,
  });
  if (listingResult.error || listingResult.status !== 0) {
    throw new Error(`ZIP listing failed: ${listingResult.error?.message || listingResult.stderr}`);
  }
  const pathInspection = inspectArchivePaths(listingResult.stdout.split(/\r?\n/));

  const extraction = fs.mkdtempSync(path.join(os.tmpdir(), "codex-patch-zip-"));
  const verifiedPayloads = {};
  try {
    const extractResult = spawnSync("tar.exe", ["-xf", zipPath, "-C", extraction], {
      encoding: "utf8",
      maxBuffer: 8 * 1024 * 1024,
      timeout: 30 * 60_000,
      windowsHide: true,
    });
    if (extractResult.error || extractResult.status !== 0) {
      throw new Error(`ZIP extraction failed: ${extractResult.error?.message || extractResult.stderr}`);
    }
    for (const [relativePath, expectedHash] of Object.entries(expectedHashes)) {
      const filePath = path.join(extraction, ...relativePath.split("/"));
      if (!fs.existsSync(filePath)) throw new Error(`ZIP is missing ${relativePath}`);
      const actual = sha256File(filePath);
      if (actual !== expectedHash) throw new Error(`ZIP payload hash mismatch: ${relativePath}`);
      verifiedPayloads[relativePath] = actual;
    }
  } finally {
    fs.rmSync(extraction, { force: true, recursive: true });
  }
  return {
    bytes: fs.statSync(zipPath).size,
    entries: pathInspection.entries,
    pathCompatibility: {
      explicitRootEntries: 0,
      legacyMaxDestinationLength: pathInspection.legacyMaxDestinationLength,
      legacyMaxPath: pathInspection.legacyMaxPath,
      longestEntry: pathInspection.longestEntry,
      longestEntryLength: pathInspection.longestEntryLength,
      recommendedInstaller: NATIVE_LAUNCHER_FILENAME,
    },
    sha256: sha256File(zipPath),
    verifiedPayloads,
  };
}

async function resolveMsix(options) {
  if (options.msixPath) {
    const msixPath = path.resolve(options.msixPath);
    if (!fs.existsSync(msixPath)) throw new Error(`MSIX was not found: ${msixPath}`);
    const packageIdentity = parseWindowsPackageName(path.basename(msixPath));
    if (!packageIdentity) {
      throw new Error("Local MSIX filename does not match the official Codex package pattern");
    }
    if (options.expectedVersion && packageIdentity.version !== options.expectedVersion) {
      throw new Error(
        `Local MSIX version mismatch: expected ${options.expectedVersion}, got ${packageIdentity.version}`,
      );
    }
    return {
      architecture: packageIdentity.architecture,
      packageName: path.basename(msixPath),
      source: "local",
      version: packageIdentity.version,
      msixPath,
    };
  }

  const upstream = await detectLatestWindowsPackage();
  if (options.expectedVersion && upstream.version !== options.expectedVersion) {
    throw new Error(
      `Microsoft Store version changed: expected ${options.expectedVersion}, got ${upstream.version}`,
    );
  }
  const cacheRoot = path.join(options.outRoot, ".cache");
  fs.mkdirSync(cacheRoot, { recursive: true });
  const msixPath = path.join(cacheRoot, upstream.packageName);
  if (fs.existsSync(msixPath) && fs.statSync(msixPath).size !== upstream.size) {
    fs.rmSync(msixPath, { force: true });
  }
  if (!fs.existsSync(msixPath)) {
    await downloadPackage(upstream.url, msixPath, upstream.size);
  }
  return { ...upstream, msixPath, source: "Microsoft Store" };
}

async function build(options) {
  if (process.platform !== "win32") {
    throw new Error("The patched Windows bundle must be built on Windows");
  }
  fs.mkdirSync(options.outRoot, { recursive: true });
  const workParent = path.join(options.outRoot, ".work");
  fs.mkdirSync(workParent, { recursive: true });
  const workRoot = path.join(workParent, `build-${process.pid}-${Date.now()}`);
  assertSafeChild(workParent, workRoot, "work directory");
  fs.mkdirSync(workRoot);

  try {
    preflightBuildTools();
    const upstream = await resolveMsix(options);
    if (upstream.architecture !== "x64") {
      throw new Error(`Only x64 is supported, received ${upstream.architecture}`);
    }
    const signature = verifyMsixSignature(upstream.msixPath);
    const extractRoot = path.join(workRoot, "msix");
    const extractor = extractMsix(upstream.msixPath, extractRoot);
    const identity = validateWindowsPackage(extractRoot, {
      architecture: "x64",
      ...WINDOWS_PACKAGE_IDENTITY,
      version: upstream.version,
    });
    const upstreamAppDir = path.join(extractRoot, "app");
    const upstreamAsar = path.join(upstreamAppDir, "resources", "app.asar");
    if (!fs.existsSync(upstreamAsar)) {
      throw new Error(`Official package has no app/resources/app.asar: ${upstreamAppDir}`);
    }

    const artifactBase = createArtifactBase(identity.version);
    const outputAppDir = path.join(options.outRoot, artifactBase);
    removeGeneratedDirectory(options.outRoot, outputAppDir);
    fs.cpSync(upstreamAppDir, outputAppDir, {
      dereference: false,
      preserveTimestamps: true,
      recursive: true,
    });

    const outputResources = path.join(outputAppDir, "resources");
    const outputAsar = path.join(outputResources, "app.asar");
    const stagedAsar = path.join(workRoot, "app.asar.patched");
    const asarReport = patchArchive(upstreamAsar, stagedAsar);
    const embeddedHeaderHashes = countEmbeddedHeaderHashes(
      upstreamAppDir,
      asarReport.original.headerSha256,
    );
    if (embeddedHeaderHashes !== 0) {
      throw new Error(
        "Official executable embeds the original ASAR header hash; refusing to modify a signed executable",
      );
    }
    fs.rmSync(outputAsar, { force: true });
    fs.renameSync(stagedAsar, outputAsar);

    const helperPath = path.join(outputResources, HELPER_FILENAME);
    const helper = compileHelper(helperPath);
    const verifiedHelper = validateHelper(helperPath);
    if (helper.sha256 !== verifiedHelper.sha256) {
      throw new Error("PowerShell helper hash changed after compilation");
    }
    const resolverPath = path.join(outputResources, RESOLVER_FILENAME);
    const resolver = installResolver(resolverPath);
    if (LAUNCHER_FILENAME !== NATIVE_LAUNCHER_FILENAME) {
      throw new Error("Update manifest and native launcher filenames disagree");
    }
    const nativeLauncherPath = path.join(outputAppDir, NATIVE_LAUNCHER_FILENAME);
    const nativeLauncher = compileNativeLauncher(nativeLauncherPath);

    const officialExecutables = verifyCriticalBinaries(upstreamAppDir, outputAppDir);
    const criticalHashes = {
      "ChatGPT.exe": officialExecutables["ChatGPT.exe"],
      "Codex.exe": officialExecutables["Codex.exe"],
      [NATIVE_LAUNCHER_FILENAME]: nativeLauncher.sha256,
      "resources/app.asar": sha256File(outputAsar),
      "resources/codex.exe": officialExecutables["resources/codex.exe"],
      [`resources/${RESOLVER_FILENAME}`]: resolver.sha256,
      [`resources/${HELPER_FILENAME}`]: helper.sha256,
    };
    const integritySidecar = writeIntegritySidecar(outputAppDir, {
      artifactBase,
      msixVersion: identity.version,
      patchVersion: PATCH_VERSION,
      verifiedPayloads: criticalHashes,
    });
    const applicationScope = compareApplicationTrees(upstreamAppDir, outputAppDir);
    const smokeRoot = path.join(workRoot, "smoke");
    fs.mkdirSync(smokeRoot);
    const workerSmoke = await runWorkerSmoke(outputAsar, outputResources, smokeRoot);

    const zipPath = path.join(options.outRoot, `${artifactBase}.zip`);
    createZip(outputAppDir, zipPath);
    const zip = verifyZip(zipPath, criticalHashes);
    const launcherPath = path.join(options.outRoot, LAUNCHER_FILENAME);
    fs.copyFileSync(nativeLauncherPath, launcherPath);
    const portableLauncher = { ...nativeLauncher, file: path.basename(launcherPath) };

    const bundleFilename = createBundleName(artifactBase);
    const bundlePath = path.join(options.outRoot, bundleFilename);
    const builtAt = new Date().toISOString();
    const report = {
      artifact: {
        bundle: bundleFilename,
        directory: path.basename(outputAppDir),
        launcher: path.basename(launcherPath),
        updateManifest: UPDATE_MANIFEST_FILENAME,
        zip: path.basename(zipPath),
      },
      applicationScope,
      asar: asarReport,
      builtAt,
      helper,
      integritySidecar: { file: path.basename(integritySidecar.path), ...integritySidecar.value },
      nativeLauncher,
      officialExecutables,
      patchVersion: PATCH_VERSION,
      portableLauncher,
      resolver,
      upstream: {
        architecture: identity.architecture,
        extractor,
        identityName: identity.name,
        msixBytes: fs.statSync(upstream.msixPath).size,
        msixSha256: sha256File(upstream.msixPath),
        packageName: upstream.packageName,
        publisher: identity.publisher,
        signature,
        source: upstream.source,
        version: identity.version,
      },
      workerSmoke,
      zip,
    };
    const reportPath = path.join(options.outRoot, `${artifactBase}.verification.json`);
    const checksumPath = `${zipPath}.sha256`;
    fs.writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`);
    fs.writeFileSync(checksumPath, `${zip.sha256}  ${path.basename(zipPath)}\n`);
    const updateManifestPath = path.join(options.outRoot, UPDATE_MANIFEST_FILENAME);
    const updateManifest = writeUpdateManifest(updateManifestPath, {
      builtAt,
      checksumPath,
      launcherPath,
      msixVersion: identity.version,
      patchVersion: PATCH_VERSION,
      reportPath,
      zipPath,
    });
    const bundle = createReleaseBundle(bundlePath, {
      [path.basename(zipPath)]: zipPath,
      [path.basename(checksumPath)]: checksumPath,
      [path.basename(reportPath)]: reportPath,
      [LAUNCHER_FILENAME]: launcherPath,
      [UPDATE_MANIFEST_FILENAME]: updateManifestPath,
    });
    return {
      bundle,
      bundlePath,
      checksumPath,
      launcherPath,
      outputAppDir,
      report,
      reportPath,
      updateManifest,
      updateManifestPath,
      zipPath,
    };
  } finally {
    if (!options.keepWork) removeGeneratedDirectory(workParent, workRoot);
  }
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const result = await build(options);
  process.stdout.write(
    `${JSON.stringify({
      checksum: result.checksumPath,
      bundle: result.bundlePath,
      launcher: result.launcherPath,
      output: result.outputAppDir,
      report: result.reportPath,
      updateManifest: result.updateManifestPath,
      zip: result.zipPath,
    }, null, 2)}\n`,
  );
}

if (require.main === module) {
  main().catch((error) => {
    process.stderr.write(`Windows patch build failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  assertSafeChild,
  build,
  compareApplicationTrees,
  countEmbeddedHeaderHashes,
  createArtifactBase,
  createBundleName,
  createDirectoryManifest,
  createReleaseBundle,
  createZip,
  decodePercentNames,
  findSignTool,
  inspectArchivePaths,
  INTEGRITY_SIDECAR_FILENAME,
  parseArguments,
  preflightBuildTools,
  verifyCriticalBinaries,
  verifyMsixSignature,
  verifyZip,
  writeIntegritySidecar,
};
