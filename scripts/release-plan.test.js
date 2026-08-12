"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const test = require("node:test");

const {
  appendGitHubOutputs,
  createReleaseTag,
  parseBoolean,
  planRelease,
} = require("./release-plan");
const { assertMicrosoftDeliveryUrl, downloadInSegments } = require("./upstream");

test("creates a deterministic independent patch release tag", () => {
  assert.equal(
    createReleaseTag("26.721.4979.0", "1.0.0"),
    "windows-msstore-26.721.4979.0-desktop-patch-1.0.0",
  );
  assert.throws(() => createReleaseTag("latest/bad", "1.0.0"), /unsafe/);
});

test("builds and publishes only when the deterministic release is absent", () => {
  const missing = planRelease({
    msixVersion: "26.721.4979.0",
    patchVersion: "1.0.0",
    releaseExists: false,
  });
  assert.equal(missing.shouldBuild, true);
  assert.equal(missing.shouldPublish, true);
  const existing = planRelease({
    msixVersion: "26.721.4979.0",
    patchVersion: "1.0.0",
    releaseExists: true,
  });
  assert.equal(existing.shouldBuild, false);
  assert.equal(existing.shouldPublish, false);
});

test("force rebuild does not overwrite an existing release", () => {
  const plan = planRelease({
    forceBuild: true,
    msixVersion: "26.721.4979.0",
    patchVersion: "1.0.0",
    releaseExists: true,
  });
  assert.equal(plan.shouldBuild, true);
  assert.equal(plan.shouldPublish, false);
  assert.equal(plan.reason, "manual-force-build-existing-release");
});

test("parses workflow booleans and writes one-line outputs", (t) => {
  assert.equal(parseBoolean(undefined, true), true);
  assert.equal(parseBoolean("0"), false);
  assert.throws(() => parseBoolean("maybe"), /Invalid boolean/);
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-plan-test-"));
  t.after(() => fs.rmSync(root, { force: true, recursive: true }));
  const output = path.join(root, "output.txt");
  appendGitHubOutputs(output, { should_build: true, tag: "safe-tag" });
  assert.equal(fs.readFileSync(output, "utf8"), "should_build=true\ntag=safe-tag\n");
  assert.throws(() => appendGitHubOutputs(output, { bad: "two\nlines" }), /one line/);
});

test("accepts only Microsoft delivery hosts for package URLs", () => {
  assert.equal(
    assertMicrosoftDeliveryUrl("https://tlu.dl.delivery.mp.microsoft.com/file.msix").hostname,
    "tlu.dl.delivery.mp.microsoft.com",
  );
  assert.throws(
    () => assertMicrosoftDeliveryUrl("https://example.com/file.msix"),
    /non-Microsoft/,
  );
});

test(
  "segmented downloader validates ranges and reassembles in order",
  { skip: process.platform !== "win32" },
  async (t) => {
    const payload = Buffer.alloc(256 * 1024 + 37);
    for (let index = 0; index < payload.length; index += 1) payload[index] = index % 251;
    const server = http.createServer((request, response) => {
      const match = String(request.headers.range || "").match(/^bytes=(\d+)-(\d+)$/);
      if (!match) {
        response.writeHead(416);
        response.end();
        return;
      }
      const start = Number(match[1]);
      const end = Math.min(Number(match[2]), payload.length - 1);
      const body = payload.subarray(start, end + 1);
      response.writeHead(206, {
        "Accept-Ranges": "bytes",
        "Content-Length": body.length,
        "Content-Range": `bytes ${start}-${end}/${payload.length}`,
      });
      response.end(body);
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    t.after(() => server.close());
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-range-test-"));
    t.after(() => fs.rmSync(root, { force: true, recursive: true }));
    const output = path.join(root, "payload.bin");
    const address = server.address();
    await downloadInSegments(
      `http://127.0.0.1:${address.port}/payload`,
      output,
      payload.length,
      4,
    );
    assert.deepEqual(fs.readFileSync(output), payload);
  },
);
