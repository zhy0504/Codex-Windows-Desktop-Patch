"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { parse } = require("acorn");
const {
  FAST_MODE_PATCH_MARKER,
  applyFastModePatch,
  inspectFastModeSource,
} = require("./fast-mode");
const { RESOLVER_FILENAME } = require("./resolver");

const HELPER_FILENAME = "codex-powershell-shim.exe";
const LEGACY_LAUNCHER_DIRECTORY_NAME = "codex-pwsh";
const WORKER_ENTRY = ".vite/build/child-process-snapshot-worker.js";
const RUNTIME_ENTRY_PATTERN = /^\.vite\/build\/src-[^/]+\.js$/;
const FAST_MODE_ENTRY_PATTERN = /^webview\/assets\/[^/]+\.js$/;
const EXPECTED_FAST_MODE_AUTH_GATES = 2;
const MAX_INTEGRITY_BLOCKS = 65_536;
const REPLACEMENT_EXPRESSION =
  `require("node:path").join(process.resourcesPath,"${HELPER_FILENAME}")`;
const RUNTIME_INTERNAL_HELPER_EXPRESSION =
  `require("node:path").resolve(process.resourcesPath,"${HELPER_FILENAME}")`;
const POWERSHELL_RESOLVER_EXPRESSION =
  `require(require("node:path").join(process.resourcesPath,"${RESOLVER_FILENAME}"))` +
  `.resolvePowerShellExecutable({environment:process.env,resourcesPath:process.resourcesPath})`;
const ENVIRONMENT_PATCH_MARKER = "environment.CODEX_PWSH_PATH=shellExecutable";
const DESKTOP_METADATA_ARGUMENT = "--codex-desktop-metadata-v1";
const REQUIRED_WORKER_MARKERS = [
  "Get-CimInstance Win32_Process",
  "Win32_PerfFormattedData_PerfProc_Process",
  "Select-Object ProcessId,ParentProcessId,",
  "WorkingSetSize,@{Name='CpuPercent'",
  "@{Name='AgeSeconds'",
];
const REQUIRED_RUNTIME_MARKERS = [
  ...REQUIRED_WORKER_MARKERS,
  "CODEX_INTERNAL_ORIGINATOR_OVERRIDE",
  "LOG_FORMAT",
  "RUST_LOG",
];
const ENVIRONMENT_FUNCTION_MARKERS = [
  "CODEX_INTERNAL_ORIGINATOR_OVERRIDE",
  "LOG_FORMAT",
  "RUST_LOG",
  "hostConfig",
  "resourcesPath",
];
const DESKTOP_METADATA_SCRIPT_MARKERS = [
  "function Decode-Rot13",
  "function Get-UserAssistEntries",
  "function Get-AppProcessKeys",
  "function Find-BestUserAssistMatch",
  "Get-Process -ErrorAction SilentlyContinue",
  "Get-StartApps",
  "bundleId",
  "displayName",
  "appPath",
  "processKeys",
  "useCount",
  "ConvertTo-Json -Compress -Depth 3",
];

function fail(message) {
  throw new Error(message);
}

function sha256(value) {
  return crypto.createHash("sha256").update(value).digest("hex");
}

function parseArguments(argv) {
  const options = { command: argv[2] };
  for (let index = 3; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key?.startsWith("--") || value == null) {
      fail(`Invalid argument near ${key || "<end>"}`);
    }
    options[key.slice(2)] = value;
  }
  return options;
}

function readArchive(archivePath) {
  const archive = fs.readFileSync(archivePath);
  if (archive.length < 16 || archive.readUInt32LE(0) !== 4) {
    fail("The file does not have a supported ASAR header");
  }

  const headerPickleSize = archive.readUInt32LE(4);
  const headerPayloadSize = archive.readUInt32LE(8);
  const headerJsonSize = archive.readUInt32LE(12);
  const dataOffset = 8 + headerPickleSize;
  if (
    headerPickleSize < 8 ||
    headerPayloadSize !== headerPickleSize - 4 ||
    headerJsonSize > headerPickleSize - 8 ||
    16 + headerJsonSize > archive.length ||
    dataOffset > archive.length
  ) {
    fail("The ASAR header sizes are inconsistent");
  }

  let header;
  try {
    header = JSON.parse(archive.subarray(16, 16 + headerJsonSize).toString("utf8"));
  } catch (error) {
    fail(`The ASAR header JSON is invalid: ${error.message}`);
  }

  return {
    archive,
    archivePath,
    dataOffset,
    header,
    headerJsonSize,
    headerSha256: sha256(archive.subarray(16, 16 + headerJsonSize)),
  };
}

function serializeHeader(header) {
  const json = Buffer.from(JSON.stringify(header), "utf8");
  const padding = (4 - (json.length % 4)) % 4;
  const headerPickleSize = 8 + json.length + padding;
  const prefix = Buffer.alloc(8);
  const headerPickle = Buffer.alloc(headerPickleSize);

  prefix.writeUInt32LE(4, 0);
  prefix.writeUInt32LE(headerPickleSize, 4);
  headerPickle.writeUInt32LE(headerPickleSize - 4, 0);
  headerPickle.writeUInt32LE(json.length, 4);
  json.copy(headerPickle, 8);
  return Buffer.concat([prefix, headerPickle]);
}

function collectEntries(node, prefix = "", output = []) {
  if (!node?.files) return output;
  for (const [name, entry] of Object.entries(node.files)) {
    const entryPath = prefix ? `${prefix}/${name}` : name;
    output.push({ entry, path: entryPath });
    collectEntries(entry, entryPath, output);
  }
  return output;
}

function collectPackedEntries(header) {
  return collectEntries(header).filter(
    ({ entry }) => !entry.files && !entry.unpacked && entry.offset != null && entry.size != null,
  );
}

function findEntry(header, entryPath) {
  return collectEntries(header).find(({ path: candidate }) => candidate === entryPath) || null;
}

function readEntry(parsed, entry, entryPath = "<unknown>") {
  const relativeOffset = Number(entry.offset);
  const size = Number(entry.size);
  const start = parsed.dataOffset + relativeOffset;
  const end = start + size;
  if (
    !Number.isSafeInteger(relativeOffset) ||
    !Number.isSafeInteger(size) ||
    relativeOffset < 0 ||
    size < 0 ||
    start < parsed.dataOffset ||
    end > parsed.archive.length
  ) {
    fail(`ASAR entry points outside the archive: ${entryPath}`);
  }
  return parsed.archive.subarray(start, end);
}

function updateIntegrity(entry, content, entryPath = "<unknown>") {
  if (!entry.integrity) return;
  const { blockSize } = readIntegrityBlockSize(entry.integrity, content.length, entryPath);
  const blocks = [];
  if (content.length === 0) {
    blocks.push(sha256(content));
  } else {
    for (let offset = 0; offset < content.length; offset += blockSize) {
      blocks.push(sha256(content.subarray(offset, Math.min(offset + blockSize, content.length))));
    }
  }
  entry.integrity.algorithm = "SHA256";
  entry.integrity.hash = sha256(content);
  entry.integrity.blockSize = blockSize;
  entry.integrity.blocks = blocks;
}

function readIntegrityBlockSize(integrity, contentLength, entryPath = "<unknown>") {
  const blockSize = Object.hasOwn(integrity, "blockSize")
    ? integrity.blockSize
    : 4 * 1024 * 1024;
  if (typeof blockSize !== "number" || !Number.isSafeInteger(blockSize) || blockSize <= 0) {
    fail(`Invalid ASAR integrity block size: ${entryPath}`);
  }
  const blockCount = Math.max(1, Math.ceil(contentLength / blockSize));
  if (blockCount > MAX_INTEGRITY_BLOCKS) {
    fail(`ASAR integrity block count exceeds limit: ${entryPath}`);
  }
  return { blockCount, blockSize };
}

function verifyArchive(parsed) {
  const entries = collectPackedEntries(parsed.header).sort((left, right) => {
    const offset = Number(left.entry.offset) - Number(right.entry.offset);
    return offset || left.path.localeCompare(right.path);
  });
  let integrityEntries = 0;
  let previousEnd = 0;

  for (const packed of entries) {
    const offset = Number(packed.entry.offset);
    const size = Number(packed.entry.size);
    if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(size) || offset < 0 || size < 0) {
      fail(`Invalid ASAR offset or size: ${packed.path}`);
    }
    if (size > 0 && offset < previousEnd) {
      fail(`Overlapping ASAR entry data near: ${packed.path}`);
    }
    const content = readEntry(parsed, packed.entry, packed.path);
    if (size > 0) previousEnd = Math.max(previousEnd, offset + size);
    if (packed.entry.integrity) {
      const { blockCount, blockSize } = readIntegrityBlockSize(
        packed.entry.integrity,
        content.length,
        packed.path,
      );
      if (!packed.entry.integrity.hash) continue;
      integrityEntries += 1;
      if (sha256(content) !== String(packed.entry.integrity.hash).toLowerCase()) {
        fail(`ASAR integrity mismatch: ${packed.path}`);
      }
      const blocks = packed.entry.integrity.blocks;
      if (Array.isArray(blocks)) {
        if (blocks.length !== blockCount) {
          fail(`ASAR block integrity mismatch: ${packed.path}`);
        }
        const calculated = [];
        if (content.length === 0) {
          calculated.push(sha256(content));
        } else {
          for (let start = 0; start < content.length; start += blockSize) {
            calculated.push(sha256(content.subarray(start, Math.min(start + blockSize, content.length))));
          }
        }
        if (JSON.stringify(calculated) !== JSON.stringify(blocks.map(String).map((v) => v.toLowerCase()))) {
          fail(`ASAR block integrity mismatch: ${packed.path}`);
        }
      }
    }
  }

  return {
    archiveBytes: parsed.archive.length,
    archiveSha256: sha256(parsed.archive),
    dataBytes: parsed.archive.length - parsed.dataOffset,
    headerSha256: parsed.headerSha256,
    integrityEntries,
    packedEntries: entries.length,
  };
}

function walkAst(node, visitor, parent = null, grandparent = null) {
  if (!node || typeof node !== "object") return;
  if (node.type) visitor(node, parent, grandparent);
  for (const [key, value] of Object.entries(node)) {
    if (key === "start" || key === "end") continue;
    if (Array.isArray(value)) {
      for (const child of value) walkAst(child, visitor, node, parent);
    } else if (value?.type) {
      walkAst(value, visitor, node, parent);
    }
  }
}

function getStaticString(node) {
  if (node?.type === "Literal" && typeof node.value === "string") return node.value;
  if (
    node?.type === "TemplateLiteral" &&
    node.expressions.length === 0 &&
    node.quasis.length === 1
  ) {
    return node.quasis[0].value.cooked;
  }
  return null;
}

function countOccurrences(source, value) {
  let count = 0;
  let offset = -1;
  while ((offset = source.indexOf(value, offset + 1)) !== -1) count += 1;
  return count;
}

function parseJavaScript(source, label) {
  try {
    return parse(source, {
      allowHashBang: true,
      ecmaVersion: "latest",
      sourceType: "script",
    });
  } catch (error) {
    fail(`${label} JavaScript is invalid: ${error.message}`);
  }
}

function isSupportedProcessQueryCall(callSource) {
  return (
    callSource.includes("-NoProfile") &&
    callSource.includes("-NonInteractive") &&
    callSource.includes("-Command") &&
    callSource.includes("windowsHide") &&
    !callSource.includes("ExecutablePath")
  );
}

function inspectProcessQuerySource(source, { allowOtherPowerShellLiterals, label }) {
  for (const marker of REQUIRED_WORKER_MARKERS) {
    if (!source.includes(marker)) {
      fail(`${label} contract changed: missing marker ${marker}`);
    }
  }

  const ast = parseJavaScript(source, label);
  const literals = [];
  const targets = [];
  walkAst(ast, (node, parent) => {
    if (getStaticString(node) !== "powershell.exe") return;
    literals.push(node);
    if (
      parent?.type === "CallExpression" &&
      parent.arguments.length >= 2 &&
      parent.arguments[0] === node
    ) {
      const callSource = source.slice(parent.start, parent.end);
      if (isSupportedProcessQueryCall(callSource)) {
        targets.push({ start: node.start, end: node.end });
      }
    }
  });

  const patchedTargets = countOccurrences(source, REPLACEMENT_EXPRESSION);
  const originalLiteralCountIsValid = allowOtherPowerShellLiterals || literals.length === 2;
  const patchedLiteralCountIsValid = allowOtherPowerShellLiterals || literals.length === 0;
  if (originalLiteralCountIsValid && targets.length === 2 && patchedTargets === 0) {
    return { state: "unpatched", targets };
  }
  if (patchedLiteralCountIsValid && targets.length === 0 && patchedTargets === 2) {
    return { state: "patched", targets: [] };
  }
  fail(
    `${label} contract changed: expected exactly two original or two patched process-query targets ` +
      `(literals=${literals.length}, targets=${targets.length}, patched=${patchedTargets})`,
  );
}

function inspectWorkerSource(source) {
  return inspectProcessQuerySource(source, {
    allowOtherPowerShellLiterals: false,
    label: "PowerShell worker",
  });
}

function applyWorkerPatch(source) {
  const inspection = inspectWorkerSource(source);
  if (inspection.state === "patched") return { changed: false, source };

  let patched = source;
  for (const target of [...inspection.targets].sort((left, right) => right.start - left.start)) {
    patched =
      patched.slice(0, target.start) +
      REPLACEMENT_EXPRESSION +
      patched.slice(target.end);
  }
  if (inspectWorkerSource(patched).state !== "patched") {
    fail("PowerShell worker verification failed after patching");
  }
  return { changed: true, source: patched };
}

function getPropertyName(property) {
  if (property?.computed) return null;
  if (property?.key?.type === "Identifier") return property.key.name;
  return getStaticString(property?.key);
}

function findRuntimeEnvironmentTarget(source) {
  const ast = parseJavaScript(source, "Codex runtime");
  const candidates = [];

  walkAst(ast, (node) => {
    if (
      node.type !== "FunctionDeclaration" &&
      node.type !== "FunctionExpression" &&
      node.type !== "ArrowFunctionExpression"
    ) {
      return;
    }
    const functionSource = source.slice(node.start, node.end);
    if (!ENVIRONMENT_FUNCTION_MARKERS.every((marker) => functionSource.includes(marker))) return;

    walkAst(node.body, (child) => {
      if (child.type !== "ReturnStatement" || child.argument?.type !== "ObjectExpression") return;
      const properties = new Map(
        child.argument.properties
          .filter((property) => property.type === "Property")
          .map((property) => [getPropertyName(property), property]),
      );
      if (!properties.has("executablePath") || !properties.has("args") || !properties.has("env")) {
        return;
      }
      const environment = properties.get("env").value;
      candidates.push({
        end: environment.end,
        source: source.slice(environment.start, environment.end),
        start: environment.start,
      });
    });
  });

  if (candidates.length !== 1) {
    fail(`Codex runtime environment contract changed: expected one target, found ${candidates.length}`);
  }
  return candidates[0];
}

function hasStaticArrayPrefix(array, expected) {
  if (array?.type !== "ArrayExpression" || array.elements.length < expected.length) return false;
  return expected.every((value, index) => getStaticString(array.elements[index]) === value);
}

function collectStaticStringBindings(ast) {
  const bindings = new Map();
  walkAst(ast, (node) => {
    if (node.type !== "VariableDeclarator" || node.id?.type !== "Identifier") return;
    const value = getStaticString(node.init);
    if (value != null) bindings.set(node.id.name, value);
  });
  return bindings;
}

function inspectDesktopMetadataScript(expression, staticStrings) {
  const matches = new Map();
  walkAst(expression, (node) => {
    if (node.type !== "Identifier") return;
    const value = staticStrings.get(node.name);
    if (value && DESKTOP_METADATA_SCRIPT_MARKERS.every((marker) => value.includes(marker))) {
      matches.set(node.name, value);
    }
  });
  if (matches.size !== 1) {
    fail(
      `Codex desktop metadata script contract changed: expected one static script, found ${matches.size}`,
    );
  }
  const [[identifier, script]] = matches;
  return { identifier, length: script.length, sha256: sha256(Buffer.from(script, "utf8")) };
}

function buildDesktopMetadataHelperArray(encodedExpressionSource) {
  return (
    `[${RUNTIME_INTERNAL_HELPER_EXPRESSION},${JSON.stringify(DESKTOP_METADATA_ARGUMENT)},` +
    `${encodedExpressionSource}]`
  );
}

function inspectRuntimePowerShellRedirects(source) {
  const ast = parseJavaScript(source, "Codex runtime PowerShell redirects");
  const staticStrings = collectStaticStringBindings(ast);
  const targetsByKind = {
    desktopMetadata: [],
    executablePathLookup: [],
    primaryRuntimeExtraction: [],
  };
  const unknown = [];
  const patchedMetadataTargets = [];
  let metadataScript = null;

  walkAst(ast, (node) => {
    if (
      node.type !== "ArrayExpression" ||
      node.elements.length !== 3 ||
      source.slice(node.elements[0]?.start, node.elements[0]?.end) !==
        RUNTIME_INTERNAL_HELPER_EXPRESSION ||
      getStaticString(node.elements[1]) !== DESKTOP_METADATA_ARGUMENT
    ) {
      return;
    }
    const encodedExpression = node.elements[2];
    const encodedSource = source.slice(encodedExpression.start, encodedExpression.end);
    if (!encodedSource.includes("utf16le") || !encodedSource.includes("base64")) {
      fail("Codex desktop metadata helper lost its encoded PowerShell fallback");
    }
    metadataScript = inspectDesktopMetadataScript(encodedExpression, staticStrings);
    patchedMetadataTargets.push({ start: node.start, end: node.end });
  });

  walkAst(ast, (node, parent, grandparent) => {
    if (getStaticString(node) !== "powershell.exe") return;

    let call = null;
    let argumentArray = null;
    if (parent?.type === "CallExpression" && parent.arguments[0] === node) {
      call = parent;
    } else if (
      parent?.type === "ArrayExpression" &&
      parent.elements[0] === node &&
      grandparent?.type === "CallExpression" &&
      grandparent.arguments[0] === parent
    ) {
      argumentArray = parent;
      call = grandparent;
    } else {
      return;
    }

    const callSource = source.slice(call.start, call.end);
    if (!argumentArray && isSupportedProcessQueryCall(callSource)) return;

    if (
      !argumentArray &&
      callSource.includes("Get-CimInstance Win32_Process") &&
      callSource.includes("ExecutablePath,CommandLine") &&
      callSource.includes("ConvertTo-Json") &&
      callSource.includes("windowsHide")
    ) {
      targetsByKind.executablePathLookup.push({ start: node.start, end: node.end });
      return;
    }

    if (
      argumentArray &&
      hasStaticArrayPrefix(argumentArray, [
        "powershell.exe",
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
      ]) &&
      argumentArray.elements.length === 8 &&
      argumentArray.elements[6]?.type === "Identifier" &&
      argumentArray.elements[7]?.type === "SpreadElement"
    ) {
      targetsByKind.primaryRuntimeExtraction.push({ start: node.start, end: node.end });
      return;
    }

    if (
      argumentArray &&
      hasStaticArrayPrefix(argumentArray, [
        "powershell.exe",
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-EncodedCommand",
      ]) &&
      argumentArray.elements.length === 7 &&
      source.slice(argumentArray.elements[6].start, argumentArray.elements[6].end).includes("utf16le") &&
      source.slice(argumentArray.elements[6].start, argumentArray.elements[6].end).includes("base64")
    ) {
      const encodedExpression = argumentArray.elements[6];
      metadataScript = inspectDesktopMetadataScript(encodedExpression, staticStrings);
      targetsByKind.desktopMetadata.push({
        end: argumentArray.end,
        start: argumentArray.start,
        value: buildDesktopMetadataHelperArray(
          source.slice(encodedExpression.start, encodedExpression.end),
        ),
      });
      return;
    }

    unknown.push({ source: callSource.slice(0, 240), start: node.start });
  });

  if (unknown.length > 0) {
    fail(
      "Codex runtime has unsupported executable-position powershell.exe calls: " +
        JSON.stringify(unknown),
    );
  }

  const counts = Object.fromEntries(
    Object.entries(targetsByKind).map(([kind, targets]) => [kind, targets.length]),
  );
  const targets = Object.values(targetsByKind).flat();
  for (const target of [
    ...targetsByKind.executablePathLookup,
    ...targetsByKind.primaryRuntimeExtraction,
  ]) {
    target.value = RUNTIME_INTERNAL_HELPER_EXPRESSION;
  }
  const helperTargets = countOccurrences(source, RUNTIME_INTERNAL_HELPER_EXPRESSION);
  const resolverTargets = countOccurrences(source, POWERSHELL_RESOLVER_EXPRESSION);
  const hasExpectedOriginalTargets =
    Object.values(counts).every((count) => count === 1) &&
    patchedMetadataTargets.length === 0 &&
    helperTargets === 0 &&
    resolverTargets === 0 &&
    metadataScript != null;
  const hasExpectedPatchedTargets =
    Object.values(counts).every((count) => count === 0) &&
    patchedMetadataTargets.length === 1 &&
    helperTargets === 3 &&
    resolverTargets === 1 &&
    metadataScript != null;
  if (hasExpectedOriginalTargets) {
    return { counts, helperTargets, metadataScript, resolverTargets, state: "unpatched", targets };
  }
  if (hasExpectedPatchedTargets) {
    return { counts, helperTargets, metadataScript, resolverTargets, state: "patched", targets: [] };
  }
  fail(
    "Codex runtime explicit PowerShell contract changed: " +
      JSON.stringify({ counts, helperTargets, patchedMetadataTargets: patchedMetadataTargets.length, resolverTargets }),
  );
}

function inspectRuntimeSource(source) {
  for (const marker of REQUIRED_RUNTIME_MARKERS) {
    if (!source.includes(marker)) {
      fail(`Codex runtime contract changed: missing marker ${marker}`);
    }
  }

  const query = inspectProcessQuerySource(source, {
    allowOtherPowerShellLiterals: true,
    label: "Codex runtime process query",
  });
  const redirects = inspectRuntimePowerShellRedirects(source);
  const environment = findRuntimeEnvironmentTarget(source);
  const environmentPatchCount = countOccurrences(source, ENVIRONMENT_PATCH_MARKER);
  const environmentIsPatched = environment.source.includes(ENVIRONMENT_PATCH_MARKER);
  let environmentState;
  if (environmentPatchCount === 0 && !environmentIsPatched) {
    environmentState = "unpatched";
  } else if (environmentPatchCount === 1 && environmentIsPatched) {
    environmentState = "patched";
  } else {
    fail(
      "Codex runtime environment contract changed: " +
        `(patches=${environmentPatchCount}, targetPatched=${environmentIsPatched})`,
    );
  }
  if (query.state !== redirects.state || query.state !== environmentState) {
    fail(
      "Codex runtime is partially patched: " +
        `queries=${query.state}, redirects=${redirects.state}, environment=${environmentState}`,
    );
  }
  return {
    environment,
    environmentState,
    queryState: query.state,
    queryTargets: query.targets,
    redirectCounts: redirects.counts,
    redirectHelperTargets: redirects.helperTargets,
    redirectMetadataScript: redirects.metadataScript,
    redirectResolverTargets: redirects.resolverTargets,
    redirectState: redirects.state,
    redirectTargets: redirects.targets,
    state: query.state,
  };
}

function wrapRuntimeEnvironment(expression) {
  return (
    `(environment=>{const pathModule=require("node:path"),` +
    `shellExecutable=${POWERSHELL_RESOLVER_EXPRESSION},shellDirectory=pathModule.dirname(shellExecutable),` +
    `legacyLauncherDirectory=pathModule.join(process.resourcesPath,"${LEGACY_LAUNCHER_DIRECTORY_NAME}"),` +
    `pathKeys=Object.keys(environment).filter(key=>key.toLowerCase()==="path"),` +
    `pathKey=pathKeys[0]??"PATH";for(const duplicateKey of pathKeys.slice(1))delete environment[duplicateKey];` +
    `environment[pathKey]=[shellDirectory,...String(environment[pathKey]??"").split(pathModule.delimiter)` +
    `.filter(entry=>{const normalized=entry.trim().split('"').join("").toLowerCase();` +
    `return normalized&&normalized!==shellDirectory.toLowerCase()&&` +
    `normalized!==legacyLauncherDirectory.toLowerCase()})].join(pathModule.delimiter);` +
    `${ENVIRONMENT_PATCH_MARKER};` +
    `return environment})(${expression})`
  );
}

function applyReplacements(source, replacements) {
  let patched = source;
  for (const replacement of [...replacements].sort((left, right) => right.start - left.start)) {
    patched =
      patched.slice(0, replacement.start) +
      replacement.value +
      patched.slice(replacement.end);
  }
  return patched;
}

function applyRuntimePatch(source) {
  const inspection = inspectRuntimeSource(source);
  if (inspection.state === "patched") return { changed: false, source };
  const replacements = inspection.queryTargets.map((target) => ({
    ...target,
    value: REPLACEMENT_EXPRESSION,
  }));
  replacements.push(
    ...inspection.redirectTargets.map((target) => ({
      ...target,
      value: target.value,
    })),
  );
  replacements.push({
    end: inspection.environment.end,
    start: inspection.environment.start,
    value: wrapRuntimeEnvironment(inspection.environment.source),
  });
  const patched = applyReplacements(source, replacements);
  if (inspectRuntimeSource(patched).state !== "patched") {
    fail("Codex runtime verification failed after patching");
  }
  return { changed: true, source: patched };
}

function findRuntimeEntry(parsed) {
  const candidates = [];
  for (const packed of collectPackedEntries(parsed.header)) {
    if (!RUNTIME_ENTRY_PATTERN.test(packed.path)) continue;
    const content = readEntry(parsed, packed.entry, packed.path);
    const source = content.toString("utf8");
    if (!REQUIRED_RUNTIME_MARKERS.every((marker) => source.includes(marker))) continue;
    candidates.push({ ...packed, content, source });
  }
  if (candidates.length !== 1) {
    fail(`Expected one packed Codex runtime entry, found ${candidates.length}`);
  }
  inspectRuntimeSource(candidates[0].source);
  return candidates[0];
}

function findFastModeEntries(parsed) {
  const candidates = [];
  for (const packed of collectPackedEntries(parsed.header)) {
    if (!FAST_MODE_ENTRY_PATTERN.test(packed.path)) continue;
    const content = readEntry(parsed, packed.entry, packed.path);
    const source = content.toString("utf8");
    if (!source.includes("fast_mode") && !source.includes(FAST_MODE_PATCH_MARKER)) continue;
    if (!source.includes("chatgpt") && !source.includes(FAST_MODE_PATCH_MARKER)) continue;
    const inspection = inspectFastModeSource(source, packed.path);
    if (inspection.state === "not-applicable") continue;
    if (inspection.state === "mixed") {
      fail(`Fast mode entry is only partially patched: ${packed.path}`);
    }
    candidates.push({ ...packed, content, inspection, source });
  }
  if (candidates.length === 0) {
    fail("No structurally supported Fast mode auth entry was found");
  }
  return candidates;
}

function summarizeFastModeEntries(entries) {
  const states = new Set(entries.map((item) => item.inspection.state));
  let state = "native";
  if (states.has("mixed")) state = "mixed";
  else if (states.has("unpatched")) state = "unpatched";
  else if (states.has("patched") && states.has("native")) state = "patched-and-native";
  else if (states.has("patched")) state = "patched";

  return {
    authGateTargets: entries.reduce(
      (total, item) => total + item.inspection.authGateTargets,
      0,
    ),
    effectiveApiKeyGates: entries.reduce(
      (total, item) => total + item.inspection.effectiveApiKeyGates,
      0,
    ),
    entries: entries.map((item) => ({
      authGateTargets: item.inspection.authGateTargets,
      effectiveApiKeyGates: item.inspection.effectiveApiKeyGates,
      markerTargets: item.inspection.markerTargets,
      nativeApiKeyGates: item.inspection.nativeApiKeyGates,
      path: item.path,
      state: item.inspection.state,
      totalAuthGates: item.inspection.totalAuthGates,
    })),
    markerTargets: entries.reduce(
      (total, item) => total + item.inspection.markerTargets,
      0,
    ),
    nativeApiKeyGates: entries.reduce(
      (total, item) => total + item.inspection.nativeApiKeyGates,
      0,
    ),
    state,
    totalAuthGates: entries.reduce(
      (total, item) => total + item.inspection.totalAuthGates,
      0,
    ),
  };
}

function createLogicalManifest(parsed) {
  const manifest = new Map();
  for (const item of collectEntries(parsed.header)) {
    const entry = item.entry;
    if (entry.files) {
      manifest.set(item.path, { type: "directory" });
    } else if (entry.link) {
      manifest.set(item.path, { link: String(entry.link), type: "link" });
    } else if (entry.unpacked) {
      manifest.set(item.path, {
        size: Number(entry.size ?? 0),
        type: "unpacked",
      });
    } else if (entry.offset != null && entry.size != null) {
      const content = readEntry(parsed, entry, item.path);
      manifest.set(item.path, {
        sha256: sha256(content),
        size: content.length,
        type: "packed",
      });
    } else {
      manifest.set(item.path, { type: "metadata" });
    }
  }
  return manifest;
}

function compareLogicalContents(before, after, allowedChangedPaths = [WORKER_ENTRY]) {
  const beforeManifest = createLogicalManifest(before);
  const afterManifest = createLogicalManifest(after);
  const allowed = new Set(allowedChangedPaths);
  const added = [...afterManifest.keys()].filter((entry) => !beforeManifest.has(entry));
  const removed = [...beforeManifest.keys()].filter((entry) => !afterManifest.has(entry));
  const changed = [];
  const packingChanged = [];

  for (const [entryPath, original] of beforeManifest) {
    const patched = afterManifest.get(entryPath);
    if (!patched) continue;
    if (original.type !== patched.type) packingChanged.push(entryPath);
    if (JSON.stringify(original) !== JSON.stringify(patched)) changed.push(entryPath);
  }

  const unexpected = changed.filter((entry) => !allowed.has(entry));
  if (added.length || removed.length || packingChanged.length || unexpected.length) {
    fail(
      "ASAR logical comparison failed: " +
        JSON.stringify({ added, removed, packingChanged, unexpected }),
    );
  }
  if (changed.length !== allowed.size || changed.some((entry) => !allowed.has(entry))) {
    fail(`Expected exactly these ASAR changes: ${[...allowed].join(", ")}`);
  }

  return {
    added,
    changed,
    contentChanges: changed.length,
    filesAfter: afterManifest.size,
    filesBefore: beforeManifest.size,
    packingChanged,
    removed,
  };
}

function resolveExtractionPath(root, entryPath) {
  const segments = String(entryPath).replace(/\\/g, "/").split("/");
  if (
    !entryPath ||
    path.isAbsolute(entryPath) ||
    segments.some((segment) => !segment || segment === "." || segment === "..")
  ) {
    fail(`Unsafe ASAR extraction path: ${entryPath}`);
  }
  const target = path.resolve(root, ...segments);
  const prefix = `${path.resolve(root)}${path.sep}`;
  if (!target.startsWith(prefix)) fail(`ASAR extraction escaped its root: ${entryPath}`);
  return target;
}

function extractArchive(archivePath, destinationPath, options = {}) {
  const parsed = readArchive(archivePath);
  const verification = verifyArchive(parsed);
  fs.mkdirSync(destinationPath, { recursive: true });
  const unpackedRoot = `${archivePath}.unpacked`;
  let extractedBytes = 0;
  let extractedFiles = 0;
  let unpackedFiles = 0;
  const unpackedMetadataMismatches = [];

  for (const item of collectEntries(parsed.header)) {
    const target = resolveExtractionPath(destinationPath, item.path);
    if (item.entry.files) {
      fs.mkdirSync(target, { recursive: true });
      continue;
    }
    fs.mkdirSync(path.dirname(target), { recursive: true });
    if (item.entry.unpacked) {
      const source = resolveExtractionPath(unpackedRoot, item.path);
      if (!fs.existsSync(source)) fail(`Unpacked ASAR payload is missing: ${item.path}`);
      fs.copyFileSync(source, target);
      const bytes = fs.statSync(target).size;
      const actualSha256 = sha256(fs.readFileSync(target));
      const expectedBytes = item.entry.size == null ? null : Number(item.entry.size);
      const expectedSha256 = item.entry.integrity?.hash
        ? String(item.entry.integrity.hash).toLowerCase()
        : null;
      const sizeMismatch = expectedBytes != null && bytes !== expectedBytes;
      const hashMismatch = expectedSha256 != null && actualSha256 !== expectedSha256;
      if (sizeMismatch || hashMismatch) {
        if (!options.allowUnpackedMetadataMismatch) {
          fail(
            `Unpacked ASAR payload ${sizeMismatch ? "size" : "integrity"} mismatch: ${item.path}`,
          );
        }
        unpackedMetadataMismatches.push({
          actualBytes: bytes,
          actualSha256,
          expectedBytes,
          expectedSha256,
          path: item.path,
        });
      }
      extractedBytes += bytes;
      extractedFiles += 1;
      unpackedFiles += 1;
      continue;
    }
    if (item.entry.link) {
      fail(`ASAR links are not supported by the smoke extractor: ${item.path}`);
    }
    const content = readEntry(parsed, item.entry, item.path);
    fs.writeFileSync(target, content, item.entry.executable ? { mode: 0o755 } : undefined);
    extractedBytes += content.length;
    extractedFiles += 1;
  }
  return {
    archiveVerification: verification,
    extractedBytes,
    extractedFiles,
    unpackedFiles,
    unpackedMetadataMismatches,
  };
}

function patchArchive(sourcePath, destinationPath) {
  if (path.resolve(sourcePath) === path.resolve(destinationPath)) {
    fail("Source and destination ASAR paths must be different");
  }
  if (fs.existsSync(destinationPath)) {
    fail(`Patch destination already exists: ${destinationPath}`);
  }

  const source = readArchive(sourcePath);
  const sourceVerification = verifyArchive(source);
  const worker = findEntry(source.header, WORKER_ENTRY);
  if (!worker || worker.entry.files || worker.entry.unpacked || worker.entry.offset == null) {
    fail(`Packed worker entry was not found: ${WORKER_ENTRY}`);
  }
  const originalWorker = readEntry(source, worker.entry, WORKER_ENTRY);
  const workerPatch = applyWorkerPatch(originalWorker.toString("utf8"));
  const runtime = findRuntimeEntry(source);
  const runtimePatch = applyRuntimePatch(runtime.source);
  const sourceFastEntries = findFastModeEntries(source);
  const sourceFastSummary = summarizeFastModeEntries(sourceFastEntries);
  if (sourceFastSummary.totalAuthGates !== EXPECTED_FAST_MODE_AUTH_GATES) {
    fail(
      `Expected exactly ${EXPECTED_FAST_MODE_AUTH_GATES} Fast mode auth gates, found ${sourceFastSummary.totalAuthGates}`,
    );
  }
  if (!workerPatch.changed || !runtimePatch.changed) {
    fail("The source ASAR must contain both unpatched worker and runtime targets");
  }
  const patchedWorker = Buffer.from(workerPatch.source, "utf8");
  const patchedRuntime = Buffer.from(runtimePatch.source, "utf8");
  let fastModeAuthTargets = 0;
  const fastModeChanges = [];
  for (const candidate of sourceFastEntries) {
    if (candidate.inspection.state !== "unpatched") continue;
    const result = applyFastModePatch(candidate.source, candidate.path);
    if (!result.changed || result.targets < 1) {
      fail(`Fast mode entry did not produce a patch: ${candidate.path}`);
    }
    fastModeAuthTargets += result.targets;
    fastModeChanges.push({
      content: Buffer.from(result.source, "utf8"),
      original: candidate.content,
      path: candidate.path,
    });
  }
  if (fastModeAuthTargets !== sourceFastSummary.authGateTargets) {
    fail("Fast mode target count changed while preparing the ASAR patch");
  }
  if (fastModeAuthTargets === 0 && sourceFastSummary.effectiveApiKeyGates === 0) {
    fail("The source ASAR has no effective API-key Fast mode gate");
  }

  const changes = [
    {
      content: patchedWorker,
      original: originalWorker,
      path: WORKER_ENTRY,
    },
    {
      content: patchedRuntime,
      original: runtime.content,
      path: runtime.path,
    },
    ...fastModeChanges,
  ].map((change) => {
    const record = findEntry(source.header, change.path);
    const originalOffset = Number(record.entry.offset);
    const originalSize = Number(record.entry.size);
    return {
      ...change,
      delta: change.content.length - originalSize,
      originalEnd: originalOffset + originalSize,
      originalOffset,
      originalSize,
    };
  }).sort((left, right) => left.originalOffset - right.originalOffset);

  for (let index = 1; index < changes.length; index += 1) {
    if (changes[index].originalOffset < changes[index - 1].originalEnd) {
      fail(`Overlapping ASAR patch targets: ${changes[index - 1].path}, ${changes[index].path}`);
    }
  }

  const header = JSON.parse(JSON.stringify(source.header));
  const changeByPath = new Map(changes.map((change) => [change.path, change]));
  for (const originalPacked of collectPackedEntries(source.header)) {
    const patchedPacked = findEntry(header, originalPacked.path);
    const originalOffset = Number(originalPacked.entry.offset);
    const shift = changes
      .filter((change) => change.originalEnd <= originalOffset)
      .reduce((total, change) => total + change.delta, 0);
    patchedPacked.entry.offset = String(originalOffset + shift);
    const change = changeByPath.get(originalPacked.path);
    if (change) {
      patchedPacked.entry.size = change.content.length;
      updateIntegrity(patchedPacked.entry, change.content, originalPacked.path);
    }
  }

  const originalData = source.archive.subarray(source.dataOffset);
  const dataChunks = [];
  let cursor = 0;
  for (const change of changes) {
    dataChunks.push(originalData.subarray(cursor, change.originalOffset), change.content);
    cursor = change.originalEnd;
  }
  dataChunks.push(originalData.subarray(cursor));
  const patchedArchive = Buffer.concat([serializeHeader(header), ...dataChunks]);
  fs.mkdirSync(path.dirname(path.resolve(destinationPath)), { recursive: true });
  fs.writeFileSync(destinationPath, patchedArchive, { flag: "wx" });

  try {
    const patched = readArchive(destinationPath);
    const patchedVerification = verifyArchive(patched);
    const patchedWorkerRecord = findEntry(patched.header, WORKER_ENTRY);
    const finalWorker = readEntry(patched, patchedWorkerRecord.entry, WORKER_ENTRY).toString("utf8");
    if (inspectWorkerSource(finalWorker).state !== "patched") {
      fail("Written ASAR does not contain the expected patched worker");
    }
    const patchedRuntimeRecord = findRuntimeEntry(patched);
    const patchedRuntimeInspection = inspectRuntimeSource(patchedRuntimeRecord.source);
    if (patchedRuntimeInspection.state !== "patched") {
      fail("Written ASAR does not contain the expected patched runtime");
    }
    const patchedFastEntries = findFastModeEntries(patched);
    const patchedFastSummary = summarizeFastModeEntries(patchedFastEntries);
    if (
      patchedFastSummary.authGateTargets !== 0 ||
      patchedFastSummary.totalAuthGates !== EXPECTED_FAST_MODE_AUTH_GATES ||
      patchedFastSummary.effectiveApiKeyGates !== EXPECTED_FAST_MODE_AUTH_GATES ||
      patchedFastSummary.state === "mixed" ||
      patchedFastSummary.state === "unpatched"
    ) {
      fail("Written ASAR still contains a ChatGPT-only Fast mode auth gate");
    }
    if (
      patchedFastSummary.markerTargets !==
      sourceFastSummary.markerTargets + fastModeAuthTargets
    ) {
      fail("Written ASAR has an unexpected Fast mode marker count");
    }
    if (
      patchedFastSummary.effectiveApiKeyGates <
      sourceFastSummary.effectiveApiKeyGates + fastModeAuthTargets
    ) {
      fail("Written ASAR does not preserve every API-key Fast mode gate");
    }
    const fastModeChangedPaths = fastModeChanges.map((change) => change.path);
    const logicalComparison = compareLogicalContents(
      source,
      patched,
      [WORKER_ENTRY, runtime.path, ...fastModeChangedPaths],
    );
    const originalFastByPath = new Map(sourceFastEntries.map((item) => [item.path, item]));
    const fastModeEntries = patchedFastEntries.map((item) => {
      const original = originalFastByPath.get(item.path);
      return {
        authGateTargets: item.inspection.authGateTargets,
        changed: fastModeChangedPaths.includes(item.path),
        effectiveApiKeyGates: item.inspection.effectiveApiKeyGates,
        markerTargets: item.inspection.markerTargets,
        nativeApiKeyGates: item.inspection.nativeApiKeyGates,
        originalSha256: original ? sha256(original.content) : null,
        patchedSha256: sha256(item.content),
        path: item.path,
        state: item.inspection.state,
        totalAuthGates: item.inspection.totalAuthGates,
      };
    });
    return {
      command: "patch",
      helperFilename: HELPER_FILENAME,
      logicalComparison,
      original: sourceVerification,
      patch: {
        fastMode: {
          authGateTargets: fastModeAuthTargets,
          changedEntries: fastModeChanges.length,
          changedPaths: fastModeChangedPaths,
          effectiveApiKeyGates: patchedFastSummary.effectiveApiKeyGates,
          entries: fastModeEntries,
          expectedAuthGates: EXPECTED_FAST_MODE_AUTH_GATES,
          marker: FAST_MODE_PATCH_MARKER,
          markerTargets: patchedFastSummary.markerTargets,
          nativeApiKeyGates: patchedFastSummary.nativeApiKeyGates,
          sourceState: sourceFastSummary.state,
          state: patchedFastSummary.state,
          totalAuthGates: patchedFastSummary.totalAuthGates,
        },
        powerShellResolverExpression: POWERSHELL_RESOLVER_EXPRESSION,
        replacementExpression: REPLACEMENT_EXPRESSION,
        resolverFilename: RESOLVER_FILENAME,
        runtimeEntry: runtime.path,
        runtimeEnvironmentTargets: 1,
        runtimeInternalHelperTargets: 3,
        runtimeInternalPowerShellEliminatedTargets: 3,
        runtimeMetadataScriptBytes: patchedRuntimeInspection.redirectMetadataScript.length,
        runtimeMetadataScriptSha256: patchedRuntimeInspection.redirectMetadataScript.sha256,
        runtimeOriginalBytes: runtime.content.length,
        runtimeOriginalSha256: sha256(runtime.content),
        runtimePatchedBytes: patchedRuntime.length,
        runtimePatchedSha256: sha256(patchedRuntime),
        runtimePowerShellDirectTargets: 0,
        runtimePowerShellResolutionTargets: 1,
        runtimeQueryTargets: 2,
        totalInternalPowerShellEliminatedTargets: 7,
        workerEntry: WORKER_ENTRY,
        workerOriginalBytes: originalWorker.length,
        workerOriginalSha256: sha256(originalWorker),
        workerPatchedBytes: patchedWorker.length,
        workerPatchedSha256: sha256(patchedWorker),
        workerTargets: 2,
      },
      patched: patchedVerification,
      sourcePath: path.resolve(sourcePath),
      destinationPath: path.resolve(destinationPath),
    };
  } catch (error) {
    fs.rmSync(destinationPath, { force: true });
    throw error;
  }
}

function inspectArchive(archivePath) {
  const parsed = readArchive(archivePath);
  const verification = verifyArchive(parsed);
  const worker = findEntry(parsed.header, WORKER_ENTRY);
  if (!worker || worker.entry.unpacked || worker.entry.offset == null) {
    fail(`Packed worker entry was not found: ${WORKER_ENTRY}`);
  }
  const workerContent = readEntry(parsed, worker.entry, WORKER_ENTRY);
  const runtime = findRuntimeEntry(parsed);
  const fastMode = summarizeFastModeEntries(findFastModeEntries(parsed));
  return {
    ...verification,
    command: "inspect",
    fastMode,
    runtimeEntry: runtime.path,
    runtimeSha256: sha256(runtime.content),
    runtimeState: inspectRuntimeSource(runtime.source).state,
    workerEntry: WORKER_ENTRY,
    workerSha256: sha256(workerContent),
    workerState: inspectWorkerSource(workerContent.toString("utf8")).state,
  };
}

function main() {
  const options = parseArguments(process.argv);
  let result;
  if (options.command === "inspect") {
    if (!options.asar) fail("inspect requires --asar");
    result = inspectArchive(options.asar);
  } else if (options.command === "verify") {
    if (!options.asar) fail("verify requires --asar");
    result = { command: "verify", ...verifyArchive(readArchive(options.asar)) };
  } else if (options.command === "patch") {
    if (!options.source || !options.destination) {
      fail("patch requires --source and --destination");
    }
    result = patchArchive(options.source, options.destination);
  } else {
    fail("Usage: asar-patch.js inspect|verify|patch [options]");
  }

  const json = `${JSON.stringify(result, null, 2)}\n`;
  if (options.report) fs.writeFileSync(options.report, json);
  process.stdout.write(json);
}

if (require.main === module) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`ASAR patch failed: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  DESKTOP_METADATA_ARGUMENT,
  DESKTOP_METADATA_SCRIPT_MARKERS,
  ENVIRONMENT_PATCH_MARKER,
  EXPECTED_FAST_MODE_AUTH_GATES,
  FAST_MODE_ENTRY_PATTERN,
  HELPER_FILENAME,
  MAX_INTEGRITY_BLOCKS,
  POWERSHELL_RESOLVER_EXPRESSION,
  REPLACEMENT_EXPRESSION,
  RUNTIME_INTERNAL_HELPER_EXPRESSION,
  REQUIRED_RUNTIME_MARKERS,
  REQUIRED_WORKER_MARKERS,
  WORKER_ENTRY,
  applyRuntimePatch,
  applyWorkerPatch,
  compareLogicalContents,
  createLogicalManifest,
  extractArchive,
  findEntry,
  findFastModeEntries,
  findRuntimeEntry,
  inspectArchive,
  inspectRuntimeSource,
  inspectWorkerSource,
  patchArchive,
  readArchive,
  readEntry,
  serializeHeader,
  sha256,
  summarizeFastModeEntries,
  verifyArchive,
  wrapRuntimeEnvironment,
};
