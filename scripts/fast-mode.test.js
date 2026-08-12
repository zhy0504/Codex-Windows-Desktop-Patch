"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");

const {
  FAST_MODE_PATCH_MARKER,
  applyFastModePatch,
  inspectFastModeSource,
} = require("./fast-mode");

function loadFunction(source, name) {
  return new Function(`${source}\nreturn ${name};`)();
}

test("expands the Fast selector to API-key auth without changing surrounding precedence", () => {
  const source = [
    "function selector(enabled, account, requirements) {",
    "  return enabled && account?.authMethod === `chatgpt` && requirements.featureRequirements.fast_mode !== false;",
    "}",
  ].join("\n");
  const before = inspectFastModeSource(source, "selector fixture");
  assert.equal(before.state, "unpatched");
  assert.equal(before.authGateTargets, 1);

  const result = applyFastModePatch(source, "selector fixture");
  assert.equal(result.changed, true);
  assert.equal(result.targets, 1);
  assert.equal(result.inspection.state, "patched");
  assert.equal(result.inspection.effectiveApiKeyGates, 1);
  assert.equal(result.source.includes(FAST_MODE_PATCH_MARKER), true);

  const selector = loadFunction(result.source, "selector");
  const requirements = { featureRequirements: { fast_mode: true } };
  assert.equal(selector(true, { authMethod: "chatgpt" }, requirements), true);
  assert.equal(selector(true, { authMethod: "apikey" }, requirements), true);
  assert.equal(selector(true, { authMethod: "bedrockApiKey" }, requirements), false);
  assert.equal(selector(false, { authMethod: "apikey" }, requirements), false);
  assert.equal(
    selector(true, { authMethod: "apikey" }, { featureRequirements: { fast_mode: false } }),
    false,
  );

  const repeated = applyFastModePatch(result.source, "selector fixture");
  assert.equal(repeated.changed, false);
  assert.equal(repeated.inspection.state, "patched");
});

test("preserves request-time service tier selection for API-key auth", () => {
  const source = [
    "function readTier(authMethod, config) {",
    "  if (config.requirements.featureRequirements.fast_mode === false) return null;",
    "  return authMethod === \"chatgpt\" ? config.service_tier : null;",
    "}",
  ].join("\n");
  const result = applyFastModePatch(source, "request fixture");
  const readTier = loadFunction(result.source, "readTier");
  const config = {
    requirements: { featureRequirements: { fast_mode: true } },
    service_tier: "fast",
  };
  assert.equal(readTier("chatgpt", config), "fast");
  assert.equal(readTier("apikey", config), "fast");
  assert.equal(readTier("bedrockApiKey", config), null);
});

test("widens the request-time negative gate only to API-key auth", () => {
  const source = [
    "function allowed(authMethod, requirements) {",
    "  return !(authMethod !== 'chatgpt' || requirements.featureRequirements.fast_mode === false);",
    "}",
  ].join("\n");
  const result = applyFastModePatch(source, "legacy fixture");
  const allowed = loadFunction(result.source, "allowed");
  const enabled = { featureRequirements: { fast_mode: true } };
  assert.equal(allowed("chatgpt", enabled), true);
  assert.equal(allowed("apikey", enabled), true);
  assert.equal(allowed("bedrockApiKey", enabled), false);
  assert.equal(
    allowed("apikey", { featureRequirements: { fast_mode: false } }),
    false,
  );
});

test("recognizes an upstream API-key expansion without rewriting it", () => {
  const source = [
    "function allowed(authMethod, config) {",
    "  return (authMethod === 'chatgpt' || authMethod === 'apikey') && config.fast_mode;",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "native fixture");
  assert.equal(inspection.state, "native");
  assert.equal(inspection.nativeApiKeyGates, 1);
  assert.equal(inspection.effectiveApiKeyGates, 1);
  assert.equal(applyFastModePatch(source, "native fixture").changed, false);
});

test("recognizes sequential auth guards used by the current Store bundle", () => {
  const source = [
    "function serviceTierAllowed(host, requirements) {",
    "  const isChatGpt = host?.authMethod === `chatgpt`;",
    "  const allowed = isChatGpt && requirements?.featureRequirements?.fast_mode !== !1;",
    "  return { isServiceTierAllowed: allowed };",
    "}",
    "function requestAllowed(authMethod, requirements) {",
    "  if (authMethod !== `chatgpt`) return false;",
    "  return requirements?.featureRequirements?.fast_mode !== !1;",
    "}",
  ].join("\n");

  const before = inspectFastModeSource(source, "sequential fixture");
  assert.equal(before.state, "unpatched");
  assert.equal(before.authGateTargets, 2);

  const result = applyFastModePatch(source, "sequential fixture");
  assert.equal(result.changed, true);
  assert.equal(result.targets, 2);
  assert.equal(result.inspection.state, "patched");
  assert.equal(result.inspection.effectiveApiKeyGates, 2);

  const serviceTierAllowed = loadFunction(result.source, "serviceTierAllowed");
  const requestAllowed = loadFunction(result.source, "requestAllowed");
  const enabled = { featureRequirements: { fast_mode: true } };
  const disabled = { featureRequirements: { fast_mode: false } };
  assert.equal(serviceTierAllowed({ authMethod: "chatgpt" }, enabled).isServiceTierAllowed, true);
  assert.equal(serviceTierAllowed({ authMethod: "apikey" }, enabled).isServiceTierAllowed, true);
  assert.equal(serviceTierAllowed({ authMethod: "bedrockApiKey" }, enabled).isServiceTierAllowed, false);
  assert.equal(serviceTierAllowed({ authMethod: "apikey" }, disabled).isServiceTierAllowed, false);
  assert.equal(requestAllowed("chatgpt", enabled), true);
  assert.equal(requestAllowed("apikey", enabled), true);
  assert.equal(requestAllowed("bedrockApiKey", enabled), false);
  assert.equal(requestAllowed("apikey", disabled), false);
});

test("does not infer a sequential Fast gate from unrelated statements", () => {
  const source = [
    "function unrelated(authMethod, config) {",
    "  const isChatGpt = authMethod === `chatgpt`;",
    "  const optional = config.fast_mode ? config.accountOnly : isChatGpt;",
    "  return config.accountOnly;",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "unrelated sequential fixture");
  assert.equal(inspection.state, "not-applicable");
  assert.equal(applyFastModePatch(source, "unrelated sequential fixture").changed, false);
});

test("ignores ChatGPT auth comparisons outside Fast mode functions", () => {
  const source = [
    "function accountOnly(authMethod) { return authMethod === 'chatgpt'; }",
    "const fast_mode = true;",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "unrelated fixture");
  assert.equal(inspection.state, "not-applicable");
  assert.equal(inspection.authGateTargets, 0);
});

test("ignores an unrelated ChatGPT auth comparison in the same Fast mode function", () => {
  const source = [
    "function selector(enabled, authMethod, requirements) {",
    "  const accountOnly = authMethod === 'chatgpt';",
    "  return [accountOnly, enabled && authMethod === 'chatgpt' && requirements.featureRequirements.fast_mode];",
    "}",
  ].join("\n");
  const result = applyFastModePatch(source, "same-function fixture");
  assert.equal(result.targets, 1);
  assert.equal(result.inspection.totalAuthGates, 1);

  const selector = loadFunction(result.source, "selector");
  const requirements = { featureRequirements: { fast_mode: true } };
  assert.deepEqual(selector(true, "apikey", requirements), [false, true]);
  assert.deepEqual(selector(true, "chatgpt", requirements), [true, true]);
});

test("ignores an auth branch that is an alternative to Fast mode", () => {
  const source = [
    "function accountOrFast(authMethod, config) {",
    "  return (authMethod === 'chatgpt' && config.accountOnly) || config.fast_mode;",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "alternative branch fixture");
  assert.equal(inspection.state, "not-applicable");
  assert.equal(inspection.totalAuthGates, 0);

  const result = applyFastModePatch(source, "alternative branch fixture");
  assert.equal(result.changed, false);
  assert.equal(result.source, source);
  const accountOrFast = loadFunction(result.source, "accountOrFast");
  assert.equal(accountOrFast("apikey", { accountOnly: true, fast_mode: false }), false);
});

test("ignores Fast mode when it is optional inside the auth branch", () => {
  const source = [
    "function accountOrFast(authMethod, config) {",
    "  return authMethod === 'chatgpt' && (config.fast_mode || config.accountOnly);",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "optional Fast branch fixture");
  assert.equal(inspection.state, "not-applicable");
  const result = applyFastModePatch(source, "optional Fast branch fixture");
  assert.equal(result.changed, false);
  const accountOrFast = loadFunction(result.source, "accountOrFast");
  assert.equal(accountOrFast("apikey", { accountOnly: true, fast_mode: false }), false);
});

test("ignores optional Fast mode conditions inside a negative auth gate", () => {
  const source = [
    "function allowed(authMethod, config) {",
    "  return !(authMethod !== 'chatgpt' || (config.fast_mode === false && config.accountOnly));",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "optional negative Fast fixture");
  assert.equal(inspection.state, "not-applicable");
  const result = applyFastModePatch(source, "optional negative Fast fixture");
  assert.equal(result.changed, false);
  const allowed = loadFunction(result.source, "allowed");
  assert.equal(allowed("apikey", { accountOnly: false, fast_mode: true }), false);
});

test("ignores a service-tier ternary whose ChatGPT branch points elsewhere", () => {
  const source = [
    "function choose(authMethod, config) {",
    "  if (config.featureRequirements.fast_mode === false) return null;",
    "  return authMethod === 'chatgpt' ? config.accountOnly : config.serviceTier;",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "reversed service-tier fixture");
  assert.equal(inspection.state, "not-applicable");
  assert.equal(applyFastModePatch(source, "reversed service-tier fixture").changed, false);
});

test("requires a disabling Fast mode guard to dominate a service-tier gate", () => {
  const source = [
    "function choose(authMethod, config) {",
    "  if (config.featureRequirements.fast_mode) config.record();",
    "  return authMethod === 'chatgpt' ? config.serviceTier : null;",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "non-dominating Fast fixture");
  assert.equal(inspection.state, "not-applicable");
  assert.equal(applyFastModePatch(source, "non-dominating Fast fixture").changed, false);
});

test("does not treat a side-effecting void expression as an empty service-tier branch", () => {
  const source = [
    "function readTier(authMethod, config) {",
    "  if (config.featureRequirements.fast_mode === false) return null;",
    "  return authMethod === 'chatgpt' ? config.serviceTier : void config.cleanup();",
    "}",
  ].join("\n");
  const inspection = inspectFastModeSource(source, "side-effecting void fixture");
  assert.equal(inspection.state, "not-applicable");
  assert.equal(applyFastModePatch(source, "side-effecting void fixture").changed, false);
});

test("does not treat a conditionally allowed API key comparison as native support", () => {
  const source = [
    "function allowed(authMethod, entitled, config) {",
    "  return (authMethod === 'chatgpt' || (authMethod === 'apikey' && entitled)) && config.fast_mode;",
    "}",
  ].join("\n");
  const before = inspectFastModeSource(source, "conditional API-key fixture");
  assert.equal(before.state, "unpatched");
  assert.equal(before.effectiveApiKeyGates, 0);

  const result = applyFastModePatch(source, "conditional API-key fixture");
  const allowed = loadFunction(result.source, "allowed");
  assert.equal(result.changed, true);
  assert.equal(allowed("apikey", false, { fast_mode: true }), true);
});

test("does not treat a conditionally excluding API key comparison as a native negative gate", () => {
  const source = [
    "function rejected(authMethod, entitled, config) {",
    "  return authMethod !== 'chatgpt' && (authMethod !== 'apikey' || entitled) && config.fast_mode;",
    "}",
  ].join("\n");
  const before = inspectFastModeSource(source, "conditional negative API-key fixture");
  assert.equal(before.state, "unpatched");
  assert.equal(before.effectiveApiKeyGates, 0);

  const result = applyFastModePatch(source, "conditional negative API-key fixture");
  const rejected = loadFunction(result.source, "rejected");
  assert.equal(result.changed, true);
  assert.equal(rejected("apikey", true, { fast_mode: true }), false);
});

test("fails closed for unstable or partially patched Fast auth gates", () => {
  const unstable = [
    "function readTier(config) {",
    "  return config.fast_mode && (getAuth() === 'chatgpt' ? config.service_tier : null);",
    "}",
  ].join("\n");
  assert.throws(
    () => inspectFastModeSource(unstable, "unstable fixture"),
    /unstable expression/,
  );

  const mixed = [
    "function first(auth, config) {",
    `  return (auth === 'chatgpt'/*${FAST_MODE_PATCH_MARKER}*/ || auth === 'apikey') && config.fast_mode;`,
    "}",
    "function second(auth, config) {",
    "  return auth === 'chatgpt' && config.fast_mode;",
    "}",
  ].join("\n");
  assert.equal(inspectFastModeSource(mixed, "mixed fixture").state, "mixed");
  assert.throws(
    () => applyFastModePatch(mixed, "mixed fixture"),
    /partially applied/,
  );
});

test("rejects malformed JavaScript instead of applying a text-only patch", () => {
  assert.throws(
    () => inspectFastModeSource("function broken( { fast_mode === 'chatgpt'", "broken fixture"),
    /not valid JavaScript/,
  );
});

test("rejects a patch marker that is not attached to a supported auth gate", () => {
  const source = [
    "function allowed(authMethod, config) {",
    "  return (authMethod === 'chatgpt' || authMethod === 'apikey') && config.fast_mode;",
    "}",
    `const marker = '${FAST_MODE_PATCH_MARKER}';`,
  ].join("\n");
  assert.throws(
    () => inspectFastModeSource(source, "stray marker fixture"),
    /marker outside a supported auth gate/,
  );
});
