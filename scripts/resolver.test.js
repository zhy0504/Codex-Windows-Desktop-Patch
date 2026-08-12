"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const {
  RESOLVER_VERSION,
  clearPowerShellResolutionCache,
  findPowerShellExecutable,
  resolvePowerShellExecutable,
} = require("../resources/codex-powershell-resolver");
const { RESOLVER_FILENAME, installResolver, validateResolver } = require("./resolver");
const { PATCH_VERSION } = require("./helper");

function writeExecutable(root, relativePath) {
  const target = path.join(root, ...relativePath.split("/"));
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, "fixture");
  return target;
}

test("resolver source version matches the independent patch version", () => {
  assert.equal(RESOLVER_VERSION, PATCH_VERSION);
});

test("uses an explicit CODEX_PWSH_PATH and rejects an invalid override", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-resolver-override-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const configured = writeExecutable(root, "configured/pwsh.exe");
  assert.equal(
    findPowerShellExecutable({ CODEX_PWSH_PATH: `\"${configured}\"` }, path.join(root, "resources")),
    path.resolve(configured),
  );
  assert.throws(
    () =>
      findPowerShellExecutable(
        { CODEX_PWSH_PATH: path.join(root, "missing.exe") },
        path.join(root, "resources"),
      ),
    /does not point to a usable/,
  );
});

test("selects the newest portable PowerShell and refreshes a stale process cache", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-resolver-portable-"));
  t.after(() => {
    clearPowerShellResolutionCache();
    fs.rmSync(root, { force: true, recursive: true });
  });
  const older = writeExecutable(root, "local/CodexPwshRuntime/PowerShell/7.5.4/pwsh.exe");
  const newest = writeExecutable(root, "local/CodexPwshRuntime/PowerShell/7.6.4/pwsh.exe");
  const environment = { LOCALAPPDATA: path.join(root, "local"), PATH: "" };
  assert.equal(
    resolvePowerShellExecutable({
      environment,
      resourcesPath: path.join(root, "resources"),
    }),
    path.resolve(newest),
  );
  assert.equal(environment.CODEX_PWSH_PATH, path.resolve(newest));
  fs.rmSync(newest);
  assert.equal(
    resolvePowerShellExecutable({
      environment,
      resourcesPath: path.join(root, "resources"),
    }),
    path.resolve(older),
  );
  assert.equal(environment.CODEX_PWSH_PATH, path.resolve(older));
});

test("does not discard a different invalid explicit override when refreshing cache", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-resolver-cache-override-"));
  t.after(() => {
    clearPowerShellResolutionCache();
    fs.rmSync(root, { force: true, recursive: true });
  });
  const cached = writeExecutable(root, "cached/pwsh.exe");
  const environment = { CODEX_PWSH_PATH: cached, PATH: "" };
  assert.equal(
    resolvePowerShellExecutable({ environment, resourcesPath: path.join(root, "resources") }),
    path.resolve(cached),
  );
  fs.rmSync(cached);
  environment.CODEX_PWSH_PATH = path.join(root, "explicit-missing.exe");
  assert.throws(
    () =>
      resolvePowerShellExecutable({
        environment,
        resourcesPath: path.join(root, "resources"),
      }),
    /does not point to a usable/,
  );
});

test("does not replace a stale explicit override with an automatic fallback", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-resolver-stale-override-"));
  t.after(() => {
    clearPowerShellResolutionCache();
    fs.rmSync(root, { force: true, recursive: true });
  });
  const configured = writeExecutable(root, "configured/pwsh.exe");
  writeExecutable(root, "local/CodexPwshRuntime/PowerShell/7.6.4/pwsh.exe");
  const environment = {
    CODEX_PWSH_PATH: configured,
    LOCALAPPDATA: path.join(root, "local"),
    PATH: "",
  };
  assert.equal(
    resolvePowerShellExecutable({ environment, resourcesPath: path.join(root, "resources") }),
    path.resolve(configured),
  );
  fs.rmSync(configured);
  assert.throws(
    () =>
      resolvePowerShellExecutable({
        environment,
        resourcesPath: path.join(root, "resources"),
      }),
    /does not point to a usable/,
  );
});

test("does not resolve a legacy managed launcher from PATH", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-resolver-legacy-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  writeExecutable(root, "codex-pwsh/pwsh.exe");
  const fallback = writeExecutable(
    root,
    "windows/System32/WindowsPowerShell/v1.0/powershell.exe",
  );
  const environment = {
    PATH: path.join(root, "codex-pwsh"),
    SystemRoot: path.join(root, "windows"),
  };
  assert.equal(
    findPowerShellExecutable(environment, path.join(root, "resources")),
    path.resolve(fallback),
  );
});

test("installs a byte-identical validated resolver", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-resolver-install-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const output = path.join(root, RESOLVER_FILENAME);
  const installed = installResolver(output);
  assert.equal(installed.version, PATCH_VERSION);
  assert.deepEqual(validateResolver(output), installed);
});
