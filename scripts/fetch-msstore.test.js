const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const https = require("node:https");
const os = require("node:os");
const path = require("node:path");
const { EventEmitter } = require("node:events");
const { Readable } = require("node:stream");

const globalAgentCaBeforeImport = https.globalAgent.options.ca;
const {
  applyStoreTlsOptions,
  downloadFile,
  findDownloadUrlByDigest,
  findPackageByArchitecture,
  httpsRequest,
  loadStoreCertificateAuthorities,
  withRetry,
} = require("./fetch-msstore");

async function createHttpServer(handler) {
  const server = http.createServer(handler);
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  return {
    url: `http://127.0.0.1:${address.port}`,
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

async function withTempDirectory(callback) {
  const directory = await fs.promises.mkdtemp(
    path.join(os.tmpdir(), "codex-msstore-test-"),
  );
  try {
    return await callback(directory);
  } finally {
    await fs.promises.rm(directory, { recursive: true, force: true });
  }
}

test("loading the Store client does not modify the global HTTPS agent", () => {
  assert.strictEqual(https.globalAgent.options.ca, globalAgentCaBeforeImport);
});

test("Store CA options preserve global proxy-agent routing", () => {
  const options = applyStoreTlsOptions(
    {},
    new URL("https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx"),
  );
  assert.equal(Object.hasOwn(options, "agent"), false);
  assert.ok(Array.isArray(options.ca));
  assert.ok(options.ca.length > 0);
});

test("Store package downloads use request-level CAs without replacing agent routing", async () => {
  const originalGet = https.get;
  let observedOptions;
  https.get = (url, options, callback) => {
    observedOptions = options;
    const request = new EventEmitter();
    request.setTimeout = () => request;
    process.nextTick(() => {
      const response = Readable.from([Buffer.from("package")]);
      response.statusCode = 200;
      response.headers = { "content-length": "7" };
      callback(response);
    });
    return request;
  };

  try {
    await withTempDirectory(async (directory) => {
      await downloadFile(
        "https://fe3.delivery.mp.microsoft.com/package.msix",
        path.join(directory, "package.msix"),
      );
    });
  } finally {
    https.get = originalGet;
  }

  assert.equal(Object.hasOwn(observedOptions, "agent"), false);
  assert.ok(Array.isArray(observedOptions.ca));
  assert.ok(observedOptions.ca.length > 0);
});

test("the Store CA bundle preserves NODE_EXTRA_CA_CERTS", async () => {
  await withTempDirectory(async (directory) => {
    const certificate = "test-environment-certificate";
    const certificatePath = path.join(directory, "extra-ca.pem");
    await fs.promises.writeFile(certificatePath, certificate);
    const previousPath = process.env.NODE_EXTRA_CA_CERTS;

    try {
      process.env.NODE_EXTRA_CA_CERTS = certificatePath;
      assert.ok(loadStoreCertificateAuthorities().includes(certificate));
    } finally {
      if (previousPath === undefined) delete process.env.NODE_EXTRA_CA_CERTS;
      else process.env.NODE_EXTRA_CA_CERTS = previousPath;
    }
  });
});

test("retries transient failures and returns the eventual result", async () => {
  let attempts = 0;
  const result = await withRetry(
    async () => {
      attempts++;
      if (attempts < 3) throw new Error("Request timeout");
      return "ok";
    },
    { maxAttempts: 3, retryDelayMs: 0 },
  );

  assert.equal(result, "ok");
  assert.equal(attempts, 3);
});
test("throws the final error when all retry attempts fail", async () => {
  let attempts = 0;
  await assert.rejects(
    withRetry(
      async () => {
        attempts++;
        throw new Error(`failure-${attempts}`);
      },
      { maxAttempts: 3, retryDelayMs: 0 },
    ),
    /failure-3/,
  );
  assert.equal(attempts, 3);
});

test("matches package architecture as a complete name segment", () => {
  const packages = [
    { name: "OpenAI.Codex_1.0.0.0_arm64__publisher.msix" },
    { name: "OpenAI.Codex_1.0.0.0_x64__publisher.msix" },
  ];
  assert.equal(findPackageByArchitecture(packages, "x64"), packages[1]);
  assert.equal(findPackageByArchitecture(packages, "ia32"), undefined);
});

test("retries HTTP 429 and 5xx responses before returning success", async (t) => {
  let attempts = 0;
  const server = await createHttpServer((request, response) => {
    attempts++;
    if (attempts === 1) {
      response.writeHead(429).end("busy");
    } else if (attempts === 2) {
      response.writeHead(503).end("unavailable");
    } else {
      response.writeHead(200).end("ok");
    }
  });
  t.after(server.close);

  const response = await httpsRequest(server.url, {
    maxAttempts: 3,
    retryDelayMs: 0,
  });

  assert.equal(response.status, 200);
  assert.equal(response.body, "ok");
  assert.equal(attempts, 3);
});

test("returns the final retryable HTTP response after exhausting retries", async (t) => {
  let attempts = 0;
  const server = await createHttpServer((request, response) => {
    attempts++;
    response.writeHead(500).end("still failing");
  });
  t.after(server.close);

  const response = await httpsRequest(server.url, {
    maxAttempts: 2,
    retryDelayMs: 0,
  });

  assert.equal(response.status, 500);
  assert.equal(response.body, "still failing");
  assert.equal(attempts, 2);
});

test("does not retry a non-transient HTTP 4xx response", async (t) => {
  let attempts = 0;
  const server = await createHttpServer((request, response) => {
    attempts++;
    response.writeHead(404).end("missing");
  });
  t.after(server.close);

  const response = await httpsRequest(server.url, {
    maxAttempts: 3,
    retryDelayMs: 0,
  });

  assert.equal(response.status, 404);
  assert.equal(attempts, 1);
});

test("selects a download URL only when its digest matches", () => {
  const locations = [
    { FileDigest: "digest-a", Url: "https://example.test/a.msix" },
    { FileDigest: "digest-b", Url: "https://example.test/b.msix" },
  ];

  assert.equal(
    findDownloadUrlByDigest(locations, "digest-b"),
    "https://example.test/b.msix",
  );
  assert.equal(findDownloadUrlByDigest(locations, "digest-missing"), "");
  assert.equal(findDownloadUrlByDigest(locations, ""), "");
});

test("downloads through a relative HTTP redirect and commits the complete file", async (t) => {
  const content = Buffer.from("complete package payload");
  const server = await createHttpServer((request, response) => {
    if (request.url === "/start") {
      response.writeHead(302, { Location: "/package.msix" }).end();
      return;
    }
    response.writeHead(200, { "Content-Length": content.length });
    response.end(content);
  });
  t.after(server.close);

  await withTempDirectory(async (directory) => {
    const destination = path.join(directory, "package.msix");
    const result = await downloadFile(`${server.url}/start`, destination);

    assert.equal(result, destination);
    assert.deepEqual(await fs.promises.readFile(destination), content);
    assert.deepEqual(await fs.promises.readdir(directory), ["package.msix"]);
  });
});

test("does not expose the destination before the response and file stream finish", async (t) => {
  let releaseResponse;
  let markFirstChunk;
  const firstChunkSent = new Promise((resolve) => {
    markFirstChunk = resolve;
  });
  const server = await createHttpServer((request, response) => {
    response.writeHead(200);
    response.write("first-");
    releaseResponse = () => response.end("last");
    markFirstChunk();
  });
  t.after(server.close);

  await withTempDirectory(async (directory) => {
    const destination = path.join(directory, "package.msix");
    const pendingDownload = downloadFile(server.url, destination);
    await firstChunkSent;

    assert.equal(fs.existsSync(destination), false);
    releaseResponse();
    await pendingDownload;
    assert.equal(await fs.promises.readFile(destination, "utf-8"), "first-last");
  });
});

test("rejects non-200 downloads without creating a destination or partial file", async (t) => {
  const server = await createHttpServer((request, response) => {
    response.writeHead(404).end("not a package");
  });
  t.after(server.close);

  await withTempDirectory(async (directory) => {
    const destination = path.join(directory, "package.msix");
    await assert.rejects(downloadFile(server.url, destination), /HTTP 404/);
    assert.deepEqual(await fs.promises.readdir(directory), []);
  });
});

test("cleans up a partial download when the response stream aborts", async (t) => {
  const server = await createHttpServer((request, response) => {
    response.writeHead(200, { "Content-Length": 100 });
    response.write("partial");
    response.socket.destroy();
  });
  t.after(server.close);

  await withTempDirectory(async (directory) => {
    const destination = path.join(directory, "package.msix");
    await assert.rejects(downloadFile(server.url, destination));
    assert.deepEqual(await fs.promises.readdir(directory), []);
  });
});

test("limits redirects and rejects unsafe redirect protocols", async (t) => {
  const loopServer = await createHttpServer((request, response) => {
    response.writeHead(302, { Location: "/loop" }).end();
  });
  const unsafeServer = await createHttpServer((request, response) => {
    response.writeHead(302, { Location: "file:///tmp/package.msix" }).end();
  });
  t.after(loopServer.close);
  t.after(unsafeServer.close);

  await withTempDirectory(async (directory) => {
    const destination = path.join(directory, "package.msix");
    await assert.rejects(
      downloadFile(`${loopServer.url}/loop`, destination, { maxRedirects: 1 }),
      /redirect limit/,
    );
    await assert.rejects(
      downloadFile(unsafeServer.url, destination),
      /Unsupported download protocol/,
    );
    assert.deepEqual(await fs.promises.readdir(directory), []);
  });
});
