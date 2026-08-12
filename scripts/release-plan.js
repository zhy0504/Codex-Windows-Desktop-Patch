"use strict";

const fs = require("node:fs");
const https = require("node:https");
const path = require("node:path");
const { PATCH_VERSION } = require("./helper");
const { detectLatestWindowsPackage } = require("./upstream");

function parseBoolean(value, defaultValue = false) {
  if (value === undefined || value === null || value === "") return defaultValue;
  if (typeof value === "boolean") return value;
  const normalized = String(value).trim().toLowerCase();
  if (["1", "true", "yes", "on"].includes(normalized)) return true;
  if (["0", "false", "no", "off"].includes(normalized)) return false;
  throw new Error(`Invalid boolean value: ${value}`);
}

function assertReleaseComponent(value, label) {
  const normalized = String(value || "").trim();
  if (!normalized || !/^[0-9A-Za-z._-]+$/.test(normalized)) {
    throw new Error(`${label} contains characters that are unsafe in a release tag`);
  }
  return normalized;
}

function assertWindowsVersion(value) {
  const version = assertReleaseComponent(value, "Windows version");
  if (!/^\d+(?:\.\d+){2,3}$/.test(version)) {
    throw new Error(`Unexpected Microsoft Store version: ${value}`);
  }
  return version;
}

function createReleaseTag(msixVersion, patchVersion) {
  return [
    "windows-msstore",
    assertWindowsVersion(msixVersion),
    "desktop-patch",
    assertReleaseComponent(patchVersion, "Patch version"),
  ].join("-");
}

function planRelease({
  forceBuild = false,
  msixVersion,
  patchVersion,
  publishRequested = true,
  releaseExists,
}) {
  const tag = createReleaseTag(msixVersion, patchVersion);
  const shouldBuild = Boolean(forceBuild) || !releaseExists;
  const shouldPublish = shouldBuild && Boolean(publishRequested) && !releaseExists;
  let reason;
  if (releaseExists && forceBuild) reason = "manual-force-build-existing-release";
  else if (releaseExists) reason = "release-already-published";
  else if (!publishRequested) reason = "release-missing-build-only";
  else reason = "release-missing";
  return {
    forceBuild: Boolean(forceBuild),
    msixVersion: assertWindowsVersion(msixVersion),
    patchVersion: assertReleaseComponent(patchVersion, "Patch version"),
    publishRequested: Boolean(publishRequested),
    reason,
    releaseExists: Boolean(releaseExists),
    shouldBuild,
    shouldPublish,
    tag,
  };
}

function requestRelease(repository, tag, token) {
  if (!/^[0-9A-Za-z_.-]+\/[0-9A-Za-z_.-]+$/.test(repository || "")) {
    throw new Error(`Invalid GitHub repository: ${repository || "(missing)"}`);
  }
  if (!token) throw new Error("GITHUB_TOKEN is required to check releases");
  return new Promise((resolve, reject) => {
    const request = https.request(
      {
        headers: {
          Accept: "application/vnd.github+json",
          Authorization: `Bearer ${token}`,
          "User-Agent": "Codex-Windows-Desktop-Patch",
          "X-GitHub-Api-Version": "2022-11-28",
        },
        hostname: "api.github.com",
        method: "GET",
        path: `/repos/${repository}/releases/tags/${encodeURIComponent(tag)}`,
      },
      (response) => {
        const chunks = [];
        response.on("data", (chunk) => chunks.push(chunk));
        response.on("end", () => {
          const body = Buffer.concat(chunks).toString("utf8");
          if (response.statusCode === 404) return resolve(null);
          if (response.statusCode !== 200) {
            return reject(
              new Error(`GitHub release lookup failed: HTTP ${response.statusCode}: ${body.slice(0, 300)}`),
            );
          }
          try {
            resolve(JSON.parse(body));
          } catch {
            reject(new Error("GitHub release lookup returned invalid JSON"));
          }
        });
      },
    );
    request.setTimeout(30_000, () => request.destroy(new Error("GitHub release lookup timed out")));
    request.on("error", reject);
    request.end();
  });
}

function appendGitHubOutputs(outputPath, values) {
  if (!outputPath) return;
  const lines = Object.entries(values).map(([key, value]) => {
    const output = String(value);
    if (/\r|\n/.test(output)) throw new Error(`GitHub output ${key} must be one line`);
    return `${key}=${output}`;
  });
  fs.appendFileSync(outputPath, `${lines.join("\n")}\n`);
}

async function main() {
  const upstream = await detectLatestWindowsPackage();
  const detectOnly = process.argv.includes("--detect-only");
  const tag = createReleaseTag(upstream.version, PATCH_VERSION);
  const release = detectOnly
    ? null
    : await requestRelease(process.env.GITHUB_REPOSITORY, tag, process.env.GITHUB_TOKEN);
  if (release?.draft) {
    throw new Error(`Draft release ${tag} exists; publish or remove it before retrying`);
  }

  const plan = detectOnly
    ? {
        detectedAt: new Date().toISOString(),
        msixVersion: upstream.version,
        packageBytes: upstream.size,
        packageName: upstream.packageName,
        patchVersion: PATCH_VERSION,
        tag,
      }
    : {
        ...planRelease({
          forceBuild: parseBoolean(process.env.FORCE_BUILD),
          msixVersion: upstream.version,
          patchVersion: PATCH_VERSION,
          publishRequested: parseBoolean(process.env.PUBLISH_RELEASE, true),
          releaseExists: release !== null,
        }),
        detectedAt: new Date().toISOString(),
        packageBytes: upstream.size,
        packageName: upstream.packageName,
      };

  if (!detectOnly) {
    appendGitHubOutputs(process.env.GITHUB_OUTPUT, {
      msix_version: plan.msixVersion,
      package_name: plan.packageName,
      patch_version: plan.patchVersion,
      reason: plan.reason,
      release_exists: plan.releaseExists,
      release_tag: plan.tag,
      should_build: plan.shouldBuild,
      should_publish: plan.shouldPublish,
    });
  }
  const json = `${JSON.stringify(plan, null, 2)}\n`;
  if (process.env.PLAN_OUTPUT_PATH) {
    const target = path.resolve(process.env.PLAN_OUTPUT_PATH);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, json);
  }
  process.stdout.write(json);
}

if (require.main === module) {
  main().catch((error) => {
    process.stderr.write(`Release planning failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  appendGitHubOutputs,
  assertWindowsVersion,
  createReleaseTag,
  parseBoolean,
  planRelease,
  requestRelease,
};
