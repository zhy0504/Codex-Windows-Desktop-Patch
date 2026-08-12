"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");

const PROJECT_ROOT = path.resolve(__dirname, "..");
const PATCH_VERSION = require("../package.json").version;
const RESOLVER_FILENAME = "codex-powershell-resolver.js";
const RESOLVER_SOURCE = path.join(PROJECT_ROOT, "resources", RESOLVER_FILENAME);

function sha256File(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

function loadResolver(filePath) {
  const modulePath = require.resolve(path.resolve(filePath));
  delete require.cache[modulePath];
  return require(modulePath);
}

function validateResolver(filePath) {
  if (!fs.existsSync(filePath)) {
    throw new Error(`PowerShell resolver was not found: ${filePath}`);
  }
  const stat = fs.statSync(filePath);
  if (!stat.isFile() || stat.size < 1024) {
    throw new Error(`PowerShell resolver is not a valid source file: ${filePath}`);
  }
  const resolver = loadResolver(filePath);
  if (
    resolver.RESOLVER_VERSION !== PATCH_VERSION ||
    typeof resolver.findPowerShellExecutable !== "function" ||
    typeof resolver.resolvePowerShellExecutable !== "function"
  ) {
    throw new Error("PowerShell resolver exports or version do not match the patch");
  }
  return {
    bytes: stat.size,
    filename: path.basename(filePath),
    sha256: sha256File(filePath),
    version: resolver.RESOLVER_VERSION,
  };
}

function assertSourceVersion() {
  return validateResolver(RESOLVER_SOURCE).version;
}

function installResolver(outputPath) {
  const source = validateResolver(RESOLVER_SOURCE);
  fs.mkdirSync(path.dirname(path.resolve(outputPath)), { recursive: true });
  fs.copyFileSync(RESOLVER_SOURCE, outputPath);
  const installed = validateResolver(outputPath);
  if (installed.sha256 !== source.sha256) {
    throw new Error("Installed PowerShell resolver differs from the repository source");
  }
  return installed;
}

module.exports = {
  RESOLVER_FILENAME,
  RESOLVER_SOURCE,
  assertSourceVersion,
  installResolver,
  validateResolver,
};
