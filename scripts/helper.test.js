"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const test = require("node:test");

const {
  HELPER_FILENAME,
  PATCH_VERSION,
  assertSourceVersion,
  compileHelper,
  validateHelper,
} = require("./helper");

test("helper source version matches the independent patch version", () => {
  assert.equal(assertSourceVersion(), PATCH_VERSION);
});

test("rejects a file without a PE header", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-helper-invalid-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const target = path.join(root, HELPER_FILENAME);
  fs.writeFileSync(target, Buffer.alloc(2048));
  assert.throws(() => validateHelper(target, { runSelfTest: false }), /PE signature/);
});

test(
  "compiled helper optimizes exact queries and preserves fallback behavior",
  { skip: process.platform !== "win32" },
  (t) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-helper-test-"));
    t.after(() => fs.rmSync(root, { force: true, recursive: true }));
    const helperPath = path.join(root, HELPER_FILENAME);
    const compiled = compileHelper(helperPath);
    assert.equal(compiled.selfTest.ShimVersion, PATCH_VERSION);
    assert.ok(compiled.selfTest.ExecutablePathRows > 0);
    assert.ok(compiled.selfTest.DesktopAppRows > 0);
    assert.equal(compiled.selfTest.ZipRoundTripFiles, 1);
    assert.equal(compiled.selfTest.PowerShellChildProcesses, 0);

    const run = (command, env, commandArguments = []) =>
      spawnSync(
        helperPath,
        ["-NoProfile", "-NonInteractive", "-Command", command, ...commandArguments],
        { encoding: "utf8", env, timeout: 20_000, windowsHide: true },
      );
    const optimizedEnvironment = {
      ...process.env,
      CODEX_PWSH_PATH: path.join(root, "missing-pwsh.exe"),
      CODEX_PWSH_SHIM_REQUIRE_OPTIMIZATIONS: "1",
    };
    const treeCommand =
      "$ErrorActionPreference = 'Stop'; Get-CimInstance Win32_Process | " +
      "Select-Object ProcessId,ParentProcessId | ConvertTo-Json -Depth 2";
    const tree = run(treeCommand, optimizedEnvironment);
    assert.equal(tree.status, 0, tree.stderr);
    assert.ok(JSON.parse(tree.stdout).length > 0);

    const detailCommand =
      "$ErrorActionPreference = 'Stop'; $cpuByPid = @{}; " +
      "Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | " +
      "ForEach-Object { $cpuByPid[[int]$_.IDProcess] = [double]$_.PercentProcessorTime }; " +
      `Get-CimInstance Win32_Process -Filter \"ProcessId = ${process.pid}\" | ` +
      "Select-Object ProcessId,ParentProcessId,CommandLine,WorkingSetSize," +
      "@{Name='CpuPercent';Expression={$cpuByPid[[int]$_.ProcessId]}}," +
      "@{Name='AgeSeconds';Expression={[int]((Get-Date) - $_.CreationDate).TotalSeconds}} | " +
      "ConvertTo-Json -Depth 2";
    const details = run(detailCommand, optimizedEnvironment);
    assert.equal(details.status, 0, details.stderr);
    const rows = JSON.parse(details.stdout);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].ProcessId, process.pid);

    const executablePathCommand =
      "$ErrorActionPreference = 'Stop'; Get-CimInstance Win32_Process | " +
      "Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Depth 2";
    const executablePaths = run(executablePathCommand, optimizedEnvironment);
    assert.equal(executablePaths.status, 0, executablePaths.stderr);
    assert.ok(
      JSON.parse(executablePaths.stdout).some((row) => row.ProcessId === process.pid),
    );

    const metadata = spawnSync(
      helperPath,
      [
        "--codex-desktop-metadata-v1",
        Buffer.from("$null", "utf16le").toString("base64"),
      ],
      { encoding: "utf8", env: optimizedEnvironment, timeout: 20_000, windowsHide: true },
    );
    assert.equal(metadata.status, 0, metadata.stderr);
    const desktopApps = JSON.parse(metadata.stdout);
    assert.ok(desktopApps.length > 0);
    assert.ok(
      desktopApps.every(
        (app) =>
          typeof app.bundleId === "string" &&
          typeof app.displayName === "string" &&
          Array.isArray(app.processKeys),
      ),
    );

    const zipSource = path.join(root, "zip-source");
    const zipOutput = path.join(root, "zip-output");
    const zipPath = path.join(root, "runtime.zip");
    fs.mkdirSync(zipSource);
    fs.writeFileSync(path.join(zipSource, "probe.txt"), "runtime-zip-probe");
    const packed = spawnSync(
      "tar.exe",
      ["-a", "-c", "-f", zipPath, "-C", zipSource, "probe.txt"],
      { encoding: "utf8", timeout: 20_000, windowsHide: true },
    );
    assert.equal(packed.status, 0, packed.stderr || packed.error?.message);
    const zipListCommand = [
      "param($ArchivePath)",
      "$ErrorActionPreference = 'Stop'",
      "Add-Type -AssemblyName System.IO.Compression.FileSystem",
      "$archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)",
      "try { $archive.Entries | ForEach-Object { $_.FullName } } finally { $archive.Dispose() }",
    ].join("\n");
    const listed = run(zipListCommand, optimizedEnvironment, [zipPath]);
    assert.equal(listed.status, 0, listed.stderr);
    assert.match(listed.stdout, /probe\.txt/);
    const zipExtractCommand = [
      "param($ArchivePath, $ExtractDir)",
      "$ErrorActionPreference = 'Stop'",
      "Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractDir -Force",
    ].join("\n");
    const extracted = run(zipExtractCommand, optimizedEnvironment, [zipPath, zipOutput]);
    assert.equal(extracted.status, 0, extracted.stderr);
    assert.equal(fs.readFileSync(path.join(zipOutput, "probe.txt"), "utf8"), "runtime-zip-probe");

    const fallback = run("$PSVersionTable.PSVersion.ToString()", process.env);
    assert.equal(fallback.status, 0, fallback.stderr);
    assert.match(fallback.stdout.trim(), /^(5\.1|7\.)/);

    const windowsPowerShell = path.join(
      process.env.WINDIR,
      "System32",
      "WindowsPowerShell",
      "v1.0",
      "powershell.exe",
    );
    const overridden = run("$PSVersionTable.PSVersion.ToString()", {
      ...process.env,
      CODEX_PWSH_PATH: windowsPowerShell,
    });
    assert.equal(overridden.status, 0, overridden.stderr);
    assert.match(overridden.stdout.trim(), /^5\.1/);
  },
);
