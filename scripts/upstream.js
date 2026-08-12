"use strict";

const fs = require("node:fs");
const http = require("node:http");
const https = require("node:https");
const path = require("node:path");
const { spawn } = require("node:child_process");
const {
  getAppInfo,
  getCookie,
  getDownloadUrl,
  getFileList,
} = require("./fetch-msstore");
const { parseWindowsPackageName, selectWindowsPackage } = require("./windows-package");

const ARCHITECTURE = "x64";
const MARKET = "US";
const PRODUCT_ID = "9plm9xgg6vks";
const RING = "Retail";

async function detectLatestWindowsPackage() {
  const cookie = await getCookie();
  const app = await getAppInfo(PRODUCT_ID, MARKET);
  if (!app.categoryId) throw new Error("Microsoft Store response has no Codex category ID");
  const packages = await getFileList(cookie, app.categoryId, RING);
  const selected = selectWindowsPackage(packages, ARCHITECTURE);
  const identity = parseWindowsPackageName(selected.name);
  const url = await getDownloadUrl(
    selected.updateID,
    selected.revisionNumber,
    RING,
    selected.digest,
  );
  if (!url) throw new Error(`Microsoft Store returned no URL for ${selected.name}`);
  assertMicrosoftDeliveryUrl(url);
  return {
    architecture: identity.architecture,
    digest: selected.digest || null,
    packageName: selected.name,
    productId: PRODUCT_ID,
    size: Number(selected.size || 0),
    url,
    version: identity.version,
  };
}

function assertMicrosoftDeliveryUrl(value) {
  const url = new URL(value);
  const hostname = url.hostname.toLowerCase();
  if (!["http:", "https:"].includes(url.protocol)) {
    throw new Error(`Unexpected Store package URL protocol: ${url.protocol}`);
  }
  if (
    hostname !== "microsoft.com" &&
    !hostname.endsWith(".microsoft.com") &&
    !hostname.endsWith(".windowsupdate.com")
  ) {
    throw new Error(`Refusing a non-Microsoft package URL: ${hostname}`);
  }
  return url;
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function downloadPackage(url, destinationPath, expectedBytes = 0) {
  assertMicrosoftDeliveryUrl(url);
  fs.mkdirSync(path.dirname(path.resolve(destinationPath)), { recursive: true });
  const partialPath = `${destinationPath}.part`;
  fs.rmSync(partialPath, { force: true });

  if (process.platform === "win32" && expectedBytes > 0) {
    await downloadInSegments(url, partialPath, expectedBytes);
    const bytes = fs.statSync(partialPath).size;
    if (bytes !== expectedBytes) {
      fs.rmSync(partialPath, { force: true });
      throw new Error(`MSIX size mismatch: expected ${expectedBytes}, received ${bytes}`);
    }
    fs.rmSync(destinationPath, { force: true });
    fs.renameSync(partialPath, destinationPath);
    return { bytes, destinationPath: path.resolve(destinationPath) };
  }

  let lastError;
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    try {
      await downloadOnce(url, partialPath, 0);
      const bytes = fs.statSync(partialPath).size;
      if (expectedBytes > 0 && bytes !== expectedBytes) {
        throw new Error(`MSIX size mismatch: expected ${expectedBytes}, received ${bytes}`);
      }
      fs.rmSync(destinationPath, { force: true });
      fs.renameSync(partialPath, destinationPath);
      return { bytes, destinationPath: path.resolve(destinationPath) };
    } catch (error) {
      lastError = error;
      fs.rmSync(partialPath, { force: true });
      if (attempt < 3) await delay(attempt * 2000);
    }
  }
  throw lastError;
}

function downloadOnce(value, destinationPath, redirects) {
  if (redirects > 8) return Promise.reject(new Error("Too many MSIX download redirects"));
  const url = assertMicrosoftDeliveryUrl(value);
  if (process.platform === "win32") {
    return downloadWithCurl(url.href, destinationPath);
  }
  const client = url.protocol === "https:" ? https : http;
  return new Promise((resolve, reject) => {
    const request = client.get(
      url,
      {
        headers: {
          "User-Agent": "Codex-Windows-Desktop-Patch/1.0",
        },
      },
      (response) => {
        if (
          response.statusCode >= 300 &&
          response.statusCode < 400 &&
          response.headers.location
        ) {
          response.resume();
          const redirected = new URL(response.headers.location, url).href;
          downloadOnce(redirected, destinationPath, redirects + 1).then(resolve, reject);
          return;
        }
        if (response.statusCode !== 200) {
          response.resume();
          reject(new Error(`MSIX download failed: HTTP ${response.statusCode}`));
          return;
        }
        const stream = fs.createWriteStream(destinationPath, { flags: "wx" });
        stream.on("error", reject);
        response.on("error", reject);
        response.pipe(stream);
        stream.on("finish", () => stream.close(resolve));
      },
    );
    request.setTimeout(60_000, () => request.destroy(new Error("MSIX download timed out")));
    request.on("error", reject);
  });
}

function downloadWithCurl(url, destinationPath) {
  return new Promise((resolve, reject) => {
    const child = spawn(
      "curl.exe",
      [
        "--fail",
        "--location",
        "--retry",
        "3",
        "--retry-delay",
        "2",
        "--connect-timeout",
        "30",
        "--proto",
        "=http,https",
        "--proto-redir",
        "=http,https",
        "--output",
        destinationPath,
        url,
      ],
      {
        stdio: ["ignore", "inherit", "inherit"],
        windowsHide: true,
      },
    );
    child.once("error", reject);
    child.once("exit", (code) => {
      if (code === 0) resolve();
      else reject(new Error(`curl.exe failed to download the MSIX (exit ${code})`));
    });
  });
}

async function downloadInSegments(url, destinationPath, expectedBytes, segmentCount = 8) {
  if (!Number.isSafeInteger(expectedBytes) || expectedBytes < 1) {
    throw new Error(`Invalid segmented download size: ${expectedBytes}`);
  }
  const count = Math.max(1, Math.min(segmentCount, expectedBytes));
  const partRoot = `${destinationPath}.parts`;
  fs.rmSync(partRoot, { force: true, recursive: true });
  fs.mkdirSync(partRoot, { recursive: true });
  const chunkSize = Math.ceil(expectedBytes / count);
  const parts = [];
  for (let index = 0; index < count; index += 1) {
    const start = index * chunkSize;
    if (start >= expectedBytes) break;
    const end = Math.min(expectedBytes - 1, start + chunkSize - 1);
    parts.push({
      end,
      path: path.join(partRoot, `part-${String(index).padStart(3, "0")}`),
      start,
    });
  }

  process.stdout.write(
    `[download] ${expectedBytes} bytes in ${parts.length} verified ranges\n`,
  );
  try {
    const results = await Promise.allSettled(
      parts.map((part) => downloadRangeWithCurl(url, part.path, part.start, part.end)),
    );
    const failed = results.find((result) => result.status === "rejected");
    if (failed) throw failed.reason;

    for (const part of parts) {
      const actual = fs.statSync(part.path).size;
      const expected = part.end - part.start + 1;
      if (actual !== expected) {
        throw new Error(
          `MSIX range size mismatch for ${part.start}-${part.end}: expected ${expected}, received ${actual}`,
        );
      }
    }

    fs.rmSync(destinationPath, { force: true });
    const output = fs.openSync(destinationPath, "wx");
    const buffer = Buffer.allocUnsafe(8 * 1024 * 1024);
    try {
      for (const part of parts) {
        const input = fs.openSync(part.path, "r");
        try {
          let bytesRead;
          do {
            bytesRead = fs.readSync(input, buffer, 0, buffer.length, null);
            if (bytesRead > 0) fs.writeSync(output, buffer, 0, bytesRead);
          } while (bytesRead > 0);
        } finally {
          fs.closeSync(input);
        }
      }
    } finally {
      fs.closeSync(output);
    }
    return { parts: parts.length };
  } catch (error) {
    fs.rmSync(destinationPath, { force: true });
    throw error;
  } finally {
    fs.rmSync(partRoot, { force: true, recursive: true });
  }
}

function downloadRangeWithCurl(url, destinationPath, start, end) {
  return new Promise((resolve, reject) => {
    const child = spawn(
      "curl.exe",
      [
        "--fail",
        "--location",
        "--retry",
        "3",
        "--retry-delay",
        "2",
        "--connect-timeout",
        "30",
        "--proto",
        "=http,https",
        "--proto-redir",
        "=http,https",
        "--silent",
        "--show-error",
        "--range",
        `${start}-${end}`,
        "--output",
        destinationPath,
        "--write-out",
        "%{http_code}",
        url,
      ],
      {
        stdio: ["ignore", "pipe", "inherit"],
        windowsHide: true,
      },
    );
    const chunks = [];
    child.stdout.on("data", (chunk) => chunks.push(chunk));
    child.once("error", reject);
    child.once("exit", (code) => {
      const status = Buffer.concat(chunks).toString("utf8").trim();
      if (code !== 0) {
        reject(new Error(`curl.exe failed for range ${start}-${end} (exit ${code})`));
      } else if (!status.endsWith("206")) {
        reject(
          new Error(
            `Microsoft CDN did not honor range ${start}-${end} (HTTP ${status || "unknown"})`,
          ),
        );
      } else {
        resolve();
      }
    });
  });
}

module.exports = {
  ARCHITECTURE,
  MARKET,
  PRODUCT_ID,
  RING,
  assertMicrosoftDeliveryUrl,
  detectLatestWindowsPackage,
  downloadInSegments,
  downloadRangeWithCurl,
  downloadWithCurl,
  downloadPackage,
};
