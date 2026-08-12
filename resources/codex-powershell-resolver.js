"use strict";

const fs = require("node:fs");
const path = require("node:path");

const CACHE_KEY = "codex.windowsDesktopPatch.resolvedExecutable.v1";
const CACHE_SYMBOL = Symbol.for(CACHE_KEY);
const LEGACY_LAUNCHER_DIRECTORY_NAME = "codex-pwsh";
const PREFERRED_POWERSHELL_VARIABLE = "CODEX_PWSH_PATH";
const RESOLVER_VERSION = "1.0.0";

function getEnvironmentKeys(environment, name) {
  const normalized = name.toLowerCase();
  return Object.keys(environment).filter((key) => key.toLowerCase() === normalized);
}

function getEnvironmentValue(environment, name) {
  const keys = getEnvironmentKeys(environment, name);
  return keys.length === 0 ? undefined : environment[keys[0]];
}

function setEnvironmentValue(environment, name, value) {
  const keys = getEnvironmentKeys(environment, name);
  const target = keys[0] || name;
  environment[target] = value;
  for (const duplicate of keys.slice(1)) delete environment[duplicate];
}

function clearEnvironmentValue(environment, name) {
  for (const key of getEnvironmentKeys(environment, name)) delete environment[key];
}

function normalizeCandidate(value) {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (trimmed.length >= 2 && trimmed.startsWith('"') && trimmed.endsWith('"')) {
    return trimmed.slice(1, -1).trim() || null;
  }
  return trimmed || null;
}

function isLegacyLauncher(candidate) {
  const filename = path.basename(candidate).toLowerCase();
  const directory = path.basename(path.dirname(candidate)).toLowerCase();
  return (
    directory === LEGACY_LAUNCHER_DIRECTORY_NAME &&
    (filename === "powershell.exe" || filename === "pwsh.exe")
  );
}

function isUsablePowerShell(candidate) {
  const normalized = normalizeCandidate(candidate);
  if (!normalized) return false;
  try {
    const resolved = path.resolve(normalized);
    return !isLegacyLauncher(resolved) && fs.statSync(resolved).isFile();
  } catch {
    return false;
  }
}

function compareVersionDirectories(left, right) {
  const parse = (value) =>
    path
      .basename(value)
      .split("-", 1)[0]
      .split(".")
      .slice(0, 4)
      .map((part) => (/^\d+$/.test(part) ? Number(part) : 0));
  const leftVersion = parse(left);
  const rightVersion = parse(right);
  for (let index = 0; index < 4; index += 1) {
    const difference = (leftVersion[index] || 0) - (rightVersion[index] || 0);
    if (difference !== 0) return difference;
  }
  return left.localeCompare(right, undefined, { sensitivity: "base" });
}

function findPortablePowerShell(localAppData) {
  const normalizedRoot = normalizeCandidate(localAppData);
  if (!normalizedRoot) return null;
  const root = path.join(normalizedRoot, "CodexPwshRuntime", "PowerShell");
  let directories;
  try {
    directories = fs
      .readdirSync(root, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => path.join(root, entry.name));
  } catch {
    return null;
  }
  directories.sort(compareVersionDirectories);
  for (const directory of directories.reverse()) {
    const candidate = path.join(directory, "pwsh.exe");
    if (isUsablePowerShell(candidate)) return path.resolve(candidate);
  }
  return null;
}

function findOnPath(environment) {
  const pathValue = getEnvironmentValue(environment, "PATH");
  if (typeof pathValue !== "string") return null;
  for (const entry of pathValue.split(path.delimiter)) {
    const normalized = normalizeCandidate(entry);
    if (!normalized) continue;
    const candidate = path.join(normalized, "pwsh.exe");
    if (isUsablePowerShell(candidate)) return path.resolve(candidate);
  }
  return null;
}

function findPowerShellExecutable(environment, resourcesPath) {
  const configured = normalizeCandidate(
    getEnvironmentValue(environment, PREFERRED_POWERSHELL_VARIABLE),
  );
  if (configured) {
    if (isUsablePowerShell(configured)) return path.resolve(configured);
    throw new Error(
      `${PREFERRED_POWERSHELL_VARIABLE} does not point to a usable PowerShell executable: ${configured}`,
    );
  }

  const seen = new Set();
  const useCandidate = (candidate) => {
    const normalized = normalizeCandidate(candidate);
    if (!normalized) return null;
    const resolved = path.resolve(normalized);
    const key = resolved.toLowerCase();
    if (seen.has(key)) return null;
    seen.add(key);
    return isUsablePowerShell(resolved) ? resolved : null;
  };
  const combine = (root, ...segments) => {
    const normalized = normalizeCandidate(root);
    return normalized ? path.join(normalized, ...segments) : null;
  };

  for (const candidate of [
    combine(resourcesPath, "pwsh", "pwsh.exe"),
    combine(getEnvironmentValue(environment, "ProgramW6432"), "PowerShell", "7", "pwsh.exe"),
    combine(getEnvironmentValue(environment, "ProgramFiles"), "PowerShell", "7", "pwsh.exe"),
  ]) {
    const resolved = useCandidate(candidate);
    if (resolved) return resolved;
  }

  const localAppData = getEnvironmentValue(environment, "LOCALAPPDATA");
  const portable = findPortablePowerShell(localAppData);
  if (portable) return portable;

  const pathCandidate = findOnPath(environment);
  if (pathCandidate) return pathCandidate;

  for (const candidate of [
    combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"),
    combine(
      getEnvironmentValue(environment, "SystemRoot") ||
        getEnvironmentValue(environment, "WINDIR"),
      "System32",
      "WindowsPowerShell",
      "v1.0",
      "powershell.exe",
    ),
  ]) {
    const resolved = useCandidate(candidate);
    if (resolved) return resolved;
  }

  throw new Error("No usable PowerShell executable was found.");
}

function resolvePowerShellExecutable({
  environment = process.env,
  resourcesPath = process.resourcesPath,
  useCache = true,
} = {}) {
  const cachedValue = globalThis[CACHE_SYMBOL];
  const cached =
    typeof cachedValue === "string"
      ? { executable: cachedValue, ownsEnvironmentValue: false }
      : cachedValue;
  if (useCache && cached && typeof cached.executable === "string") {
    if (isUsablePowerShell(cached.executable)) {
      setEnvironmentValue(environment, PREFERRED_POWERSHELL_VARIABLE, cached.executable);
      return cached.executable;
    }

    delete globalThis[CACHE_SYMBOL];
    const configured = normalizeCandidate(
      getEnvironmentValue(environment, PREFERRED_POWERSHELL_VARIABLE),
    );
    if (
      cached.ownsEnvironmentValue === true &&
      configured &&
      path.resolve(configured) === path.resolve(cached.executable)
    ) {
      clearEnvironmentValue(environment, PREFERRED_POWERSHELL_VARIABLE);
    }
  }
  const hadExplicitPreference = Boolean(
    normalizeCandidate(getEnvironmentValue(environment, PREFERRED_POWERSHELL_VARIABLE)),
  );
  const resolved = findPowerShellExecutable(environment, resourcesPath);
  setEnvironmentValue(environment, PREFERRED_POWERSHELL_VARIABLE, resolved);
  if (useCache) {
    globalThis[CACHE_SYMBOL] = {
      executable: resolved,
      ownsEnvironmentValue: !hadExplicitPreference,
    };
  }
  return resolved;
}

function clearPowerShellResolutionCache() {
  delete globalThis[CACHE_SYMBOL];
}

module.exports = {
  CACHE_KEY,
  PREFERRED_POWERSHELL_VARIABLE,
  RESOLVER_VERSION,
  clearPowerShellResolutionCache,
  findPowerShellExecutable,
  resolvePowerShellExecutable,
};
