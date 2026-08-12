"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const PROJECT_ROOT = path.resolve(__dirname, "..");
const HELPER_FILENAME = "codex-powershell-shim.exe";
const HELPER_SOURCE = path.join(PROJECT_ROOT, "resources", "CodexPowerShellShim.cs");
const PATCH_VERSION = require("../package.json").version;
const SELF_TEST_ARGUMENT = "--codex-pwsh-shim-self-test";

function sha256File(filePath) {
  const hash = crypto.createHash("sha256");
  hash.update(fs.readFileSync(filePath));
  return hash.digest("hex");
}

function findCSharpCompiler(environment = process.env) {
  const windowsRoot = environment.WINDIR || environment.SystemRoot;
  if (!windowsRoot) return null;
  return [
    path.join(windowsRoot, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
    path.join(windowsRoot, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe"),
  ].find((candidate) => fs.existsSync(candidate)) || null;
}

function assertSourceVersion() {
  if (!fs.existsSync(HELPER_SOURCE)) {
    throw new Error(`PowerShell helper source was not found: ${HELPER_SOURCE}`);
  }
  const source = fs.readFileSync(HELPER_SOURCE, "utf8");
  const match = source.match(/private const string ShimVersion = "([^"]+)"/);
  if (!match || match[1] !== PATCH_VERSION) {
    throw new Error(
      `PowerShell helper version does not match package.json: ${match?.[1] || "(missing)"} vs ${PATCH_VERSION}`,
    );
  }
  return match[1];
}

function validateHelper(helperPath, { runSelfTest = true } = {}) {
  if (!fs.existsSync(helperPath)) {
    throw new Error(`PowerShell helper was not found: ${helperPath}`);
  }
  const stat = fs.statSync(helperPath);
  if (!stat.isFile() || stat.size < 1024) {
    throw new Error(`PowerShell helper is not a valid executable: ${helperPath}`);
  }
  if (fs.readFileSync(helperPath).subarray(0, 2).toString("ascii") !== "MZ") {
    throw new Error(`PowerShell helper has no PE signature: ${helperPath}`);
  }

  let selfTest = null;
  let selfTestDurationMs = null;
  if (runSelfTest) {
    const startedAt = process.hrtime.bigint();
    const result = spawnSync(helperPath, [SELF_TEST_ARGUMENT], {
      encoding: "utf8",
      timeout: 20_000,
      windowsHide: true,
    });
    if (result.error || result.status !== 0) {
      const detail =
        result.error?.message || result.stderr?.trim() || result.stdout?.trim() || `exit ${result.status}`;
      throw new Error(`PowerShell helper self-test failed: ${detail}`);
    }
    try {
      selfTest = JSON.parse(result.stdout.trim());
    } catch {
      throw new Error("PowerShell helper self-test returned invalid JSON");
    }
    if (
      selfTest.ShimVersion !== PATCH_VERSION ||
      !Number.isInteger(selfTest.NativeProcessRows) ||
      selfTest.NativeProcessRows < 1 ||
      selfTest.DirectWmiRows !== 1 ||
      !Number.isInteger(selfTest.ExecutablePathRows) ||
      selfTest.ExecutablePathRows < 1 ||
      !Number.isInteger(selfTest.DesktopAppRows) ||
      selfTest.DesktopAppRows < 1 ||
      selfTest.ZipRoundTripFiles !== 1 ||
      selfTest.PowerShellChildProcesses !== 0 ||
      !selfTest.DurationsMs ||
      !["ProcessTree", "ProcessDetails", "ExecutablePath", "DesktopMetadata", "ZipRoundTrip"].every(
        (name) => Number.isInteger(selfTest.DurationsMs[name]) && selfTest.DurationsMs[name] >= 0,
      )
    ) {
      throw new Error("PowerShell helper self-test returned unexpected evidence");
    }
    selfTestDurationMs = Number(process.hrtime.bigint() - startedAt) / 1e6;
  }

  return {
    bytes: stat.size,
    filename: path.basename(helperPath),
    selfTest,
    selfTestDurationMs,
    sha256: sha256File(helperPath),
    version: PATCH_VERSION,
  };
}

function compileHelper(outputPath) {
  if (process.platform !== "win32") {
    throw new Error("The PowerShell helper must be compiled on Windows");
  }
  assertSourceVersion();
  const compiler = findCSharpCompiler();
  if (!compiler) throw new Error("The .NET Framework C# compiler was not found");

  fs.mkdirSync(path.dirname(path.resolve(outputPath)), { recursive: true });
  const temporaryPath = `${outputPath}.${process.pid}.${Date.now()}.tmp.exe`;
  try {
    const result = spawnSync(
      compiler,
      [
        "/nologo",
        "/target:exe",
        "/optimize+",
        "/platform:x64",
        "/reference:Microsoft.CSharp.dll",
        "/reference:System.Management.dll",
        "/reference:System.IO.Compression.dll",
        "/reference:System.IO.Compression.FileSystem.dll",
        `/out:${temporaryPath}`,
        HELPER_SOURCE,
      ],
      {
        encoding: "utf8",
        timeout: 60_000,
        windowsHide: true,
      },
    );
    if (result.error || result.status !== 0 || !fs.existsSync(temporaryPath)) {
      const detail =
        result.error?.message || result.stderr?.trim() || result.stdout?.trim() || `exit ${result.status}`;
      throw new Error(`PowerShell helper compilation failed: ${detail}`);
    }
    const validation = validateHelper(temporaryPath);
    fs.rmSync(outputPath, { force: true });
    fs.renameSync(temporaryPath, outputPath);
    return { compiler, ...validation, filename: path.basename(outputPath) };
  } finally {
    fs.rmSync(temporaryPath, { force: true });
  }
}

function main() {
  const outputIndex = process.argv.indexOf("--output");
  if (outputIndex === -1 || !process.argv[outputIndex + 1]) {
    throw new Error("Usage: helper.js --output <codex-powershell-shim.exe>");
  }
  const result = compileHelper(path.resolve(process.argv[outputIndex + 1]));
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

if (require.main === module) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`Helper build failed: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  HELPER_FILENAME,
  HELPER_SOURCE,
  PATCH_VERSION,
  SELF_TEST_ARGUMENT,
  assertSourceVersion,
  compileHelper,
  findCSharpCompiler,
  sha256File,
  validateHelper,
};
