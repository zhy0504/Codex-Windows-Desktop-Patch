"use strict";

const { parse } = require("acorn");

const FAST_MODE_PATCH_MARKER = "__CODEX_API_FAST_MODE_PATCH_V1__";
const FAST_MODE_PATCH_COMMENT = `/*${FAST_MODE_PATCH_MARKER}*/`;

function fail(message) {
  throw new Error(message);
}

function countOccurrences(source, value) {
  if (!value) return 0;
  let count = 0;
  let offset = 0;
  while ((offset = source.indexOf(value, offset)) !== -1) {
    count += 1;
    offset += value.length;
  }
  return count;
}

function parseJavaScript(source, label) {
  try {
    return parse(source, {
      allowHashBang: true,
      ecmaVersion: "latest",
      sourceType: "module",
    });
  } catch (error) {
    fail(`${label} is not valid JavaScript: ${error.message}`);
  }
}

function walkAst(node, visitor, ancestors = []) {
  if (!node || typeof node !== "object") return;
  if (node.type) visitor(node, ancestors);
  ancestors.push(node);
  for (const key of Object.keys(node)) {
    if (key === "type" || key === "start" || key === "end") continue;
    const child = node[key];
    if (Array.isArray(child)) {
      for (const item of child) {
        if (item?.type) walkAst(item, visitor, ancestors);
      }
    } else if (child?.type) {
      walkAst(child, visitor, ancestors);
    }
  }
  ancestors.pop();
}

function isFunctionNode(node) {
  return (
    node?.type === "ArrowFunctionExpression" ||
    node?.type === "FunctionDeclaration" ||
    node?.type === "FunctionExpression"
  );
}

function getStaticString(node) {
  if (node?.type === "Literal" && typeof node.value === "string") return node.value;
  if (
    node?.type === "TemplateLiteral" &&
    node.expressions.length === 0 &&
    node.quasis.length === 1
  ) {
    return node.quasis[0].value.cooked;
  }
  return null;
}

function unwrapChain(node) {
  return node?.type === "ChainExpression" ? node.expression : node;
}

function isStableAuthExpression(node) {
  const expression = unwrapChain(node);
  if (!expression) return false;
  if (expression.type === "Identifier" || expression.type === "ThisExpression") return true;
  if (expression.type !== "MemberExpression") return false;
  if (!isStableAuthExpression(expression.object)) return false;
  if (!expression.computed) return expression.property?.type === "Identifier";
  return getStaticString(expression.property) != null;
}

function isAuthMethodExpression(node) {
  const expression = unwrapChain(node);
  if (expression?.type !== "MemberExpression") return false;
  const propertyName = expression.computed
    ? getStaticString(expression.property)
    : expression.property?.name;
  return propertyName === "authMethod";
}

function isLegacyAuthExpression(node) {
  const expression = unwrapChain(node);
  return expression?.type === "Identifier" || isAuthMethodExpression(expression);
}

function getChatGptComparison(node) {
  if (node?.type !== "BinaryExpression") return null;
  const left = getStaticString(node.left);
  const right = getStaticString(node.right);
  if (left === "chatgpt" && right !== "chatgpt") {
    return { authExpression: node.right, literal: node.left };
  }
  if (right === "chatgpt" && left !== "chatgpt") {
    return { authExpression: node.left, literal: node.right };
  }
  return null;
}

function findNearestFunction(ancestors) {
  for (let index = ancestors.length - 1; index >= 0; index -= 1) {
    if (isFunctionNode(ancestors[index])) return ancestors[index];
  }
  return null;
}

function findLogicalRoot(node, ancestors, operator) {
  let root = node;
  for (let index = ancestors.length - 1; index >= 0; index -= 1) {
    const candidate = ancestors[index];
    if (candidate.type !== "LogicalExpression" || candidate.operator !== operator) break;
    root = candidate;
  }
  return root;
}

function flattenLogicalOperands(node, operator, operands = []) {
  if (node?.type === "LogicalExpression" && node.operator === operator) {
    flattenLogicalOperands(node.left, operator, operands);
    flattenLogicalOperands(node.right, operator, operands);
  } else {
    operands.push(node);
  }
  return operands;
}

function isEquivalentApiKeyComparison(node, source, authSource, operator) {
  if (node?.type !== "BinaryExpression" || node.operator !== operator) return false;
  const left = getStaticString(node.left);
  const right = getStaticString(node.right);
  let expression = null;
  if (left === "apikey" && right !== "apikey") expression = node.right;
  if (right === "apikey" && left !== "apikey") expression = node.left;
  if (!expression) return false;
  return source.slice(expression.start, expression.end) === authSource;
}

function hasEquivalentApiKeyComparison(root, source, authSource, operator, logicalOperator) {
  return flattenLogicalOperands(root, logicalOperator).some((operand) =>
    isEquivalentApiKeyComparison(operand, source, authSource, operator),
  );
}

function hasExpandedApiKeyAlternative(node, ancestors, source) {
  const comparison = getChatGptComparison(node);
  if (!comparison) return false;
  const authSource = source.slice(comparison.authExpression.start, comparison.authExpression.end);
  const logicalOperator = node.operator === "===" ? "||" : "&&";
  for (let index = ancestors.length - 1; index >= 0; index -= 1) {
    const candidate = ancestors[index];
    if (isFunctionNode(candidate)) break;
    if (
      candidate.type === "LogicalExpression" &&
      candidate.operator === logicalOperator &&
      containsNode(candidate, node) &&
      hasEquivalentApiKeyComparison(candidate, source, authSource, node.operator, logicalOperator)
    ) {
      return true;
    }
  }
  return false;
}

function containsNode(container, node) {
  return container?.start <= node.start && container?.end >= node.end;
}

function containsName(node, names) {
  let found = false;
  const visit = (candidate, isRoot = false) => {
    if (found || !candidate || typeof candidate !== "object") return;
    if (!isRoot && isFunctionNode(candidate)) return;
    if (candidate.type === "Identifier" && names.has(candidate.name)) found = true;
    else {
      const value = getStaticString(candidate);
      if (value != null && names.has(value)) found = true;
    }
    if (found) return;
    for (const key of Object.keys(candidate)) {
      if (key === "type" || key === "start" || key === "end") continue;
      const child = candidate[key];
      if (Array.isArray(child)) {
        for (const item of child) visit(item);
      } else {
        visit(child);
      }
    }
  };
  visit(node, true);
  return found;
}

function isNamedReference(node, names) {
  const expression = unwrapChain(node);
  if (expression?.type === "Identifier") return names.has(expression.name);
  if (expression?.type !== "MemberExpression") return false;
  const propertyName = expression.computed
    ? getStaticString(expression.property)
    : expression.property?.name;
  return names.has(propertyName);
}

function getStaticBoolean(node) {
  if (node?.type === "Literal" && typeof node.value === "boolean") return node.value;
  if (
    node?.type === "UnaryExpression" &&
    node.operator === "!" &&
    node.argument?.type === "Literal" &&
    (node.argument.value === 0 || node.argument.value === 1)
  ) {
    return node.argument.value === 0;
  }
  return null;
}

function requiresFastModeEnabled(node, desiredValue, fastModeNames) {
  const expression = unwrapChain(node);
  if (!expression) return false;
  if (isNamedReference(expression, fastModeNames)) return desiredValue;
  if (expression.type === "UnaryExpression" && expression.operator === "!") {
    return requiresFastModeEnabled(expression.argument, !desiredValue, fastModeNames);
  }
  if (expression.type === "SequenceExpression" && expression.expressions.length > 0) {
    return requiresFastModeEnabled(
      expression.expressions[expression.expressions.length - 1],
      desiredValue,
      fastModeNames,
    );
  }
  if (expression.type === "LogicalExpression") {
    const left = requiresFastModeEnabled(expression.left, desiredValue, fastModeNames);
    const right = requiresFastModeEnabled(expression.right, desiredValue, fastModeNames);
    if (expression.operator === "&&") return desiredValue ? left || right : left && right;
    if (expression.operator === "||") return desiredValue ? left && right : left || right;
    return false;
  }
  if (expression.type !== "BinaryExpression") return false;
  const operators = new Set(["===", "!==", "==", "!="]);
  if (!operators.has(expression.operator)) return false;
  const leftBoolean = getStaticBoolean(expression.left);
  const rightBoolean = getStaticBoolean(expression.right);
  let booleanValue = null;
  if (typeof leftBoolean === "boolean" && isNamedReference(expression.right, fastModeNames)) {
    booleanValue = leftBoolean;
  } else if (
    typeof rightBoolean === "boolean" &&
    isNamedReference(expression.left, fastModeNames)
  ) {
    booleanValue = rightBoolean;
  }
  if (booleanValue == null) return false;
  const equality = expression.operator === "===" || expression.operator === "==";
  const resultWhenEnabled = equality ? booleanValue === true : booleanValue !== true;
  return desiredValue === resultWhenEnabled;
}

function hasFastModeBindingConjunction(node, ancestors, fn, fastModeNames, source) {
  let bindingName = null;
  for (let index = ancestors.length - 1; index >= 0; index -= 1) {
    const candidate = ancestors[index];
    if (
      candidate.type === "VariableDeclarator" &&
      (candidate.init === node ||
        (containsNode(candidate.init, node) && hasExpandedApiKeyAlternative(node, ancestors, source)))
    ) {
      if (candidate.id?.type === "Identifier") bindingName = candidate.id.name;
      break;
    }
    if (candidate === fn) break;
  }
  if (!bindingName || fn.body?.type !== "BlockStatement") return false;

  const bindingNames = new Set([bindingName]);
  let found = false;
  walkAst(
    fn.body,
    (candidate, candidateAncestors) => {
      if (found || candidate.type !== "LogicalExpression" || candidate.operator !== "&&") {
        return;
      }
      if (candidate.start <= node.end || findNearestFunction(candidateAncestors) !== fn) {
        return;
      }
      const operands = flattenLogicalOperands(candidate, "&&");
      if (!operands.some((operand) => isNamedReference(operand, bindingNames))) return;
      if (!operands.some((operand) => requiresFastModeEnabled(operand, true, fastModeNames))) {
        return;
      }
      found = true;
    },
    [fn],
  );
  return found;
}

function hasFastModeGuardedReturn(node, ancestors, fn, fastModeNames, source) {
  const guard = ancestors.find(
    (candidate) =>
      candidate.type === "IfStatement" &&
      (candidate.test === node ||
        (containsNode(candidate.test, node) &&
          hasExpandedApiKeyAlternative(node, ancestors, source))),
  );
  if (
    !guard ||
    (node.operator !== "!==" && node.operator !== "!=") ||
    !isAbruptStatement(guard.consequent) ||
    guard.alternate ||
    fn.body?.type !== "BlockStatement"
  ) {
    return false;
  }

  const guardIndex = fn.body.body.findIndex((statement) => statement === guard);
  if (guardIndex < 0) return false;
  for (const statement of fn.body.body.slice(guardIndex + 1)) {
    let found = false;
    walkAst(
      statement,
      (candidate, candidateAncestors) => {
        if (
          found ||
          candidate.type !== "ReturnStatement" ||
          findNearestFunction(candidateAncestors) !== fn ||
          !containsName(candidate.argument, fastModeNames)
        ) {
          return;
        }
        found = true;
      },
      [fn],
    );
    if (found) return true;
  }
  return false;
}

function hasConjunctiveFastModeRelationship(node, ancestors, fastModeNames) {
  for (let index = ancestors.length - 1; index >= 0; index -= 1) {
    const candidate = ancestors[index];
    if (isFunctionNode(candidate)) break;
    if (candidate.type !== "LogicalExpression") continue;
    if (candidate.operator !== "&&" && candidate.operator !== "||") continue;

    const nodeInLeft = containsNode(candidate.left, node);
    const nodeInRight = containsNode(candidate.right, node);
    if (nodeInLeft === nodeInRight) continue;
    const otherBranch = nodeInLeft ? candidate.right : candidate.left;

    let expression = candidate;
    let negated = false;
    for (let parentIndex = index - 1; parentIndex >= 0; parentIndex -= 1) {
      const parent = ancestors[parentIndex];
      if (
        parent.type !== "UnaryExpression" ||
        parent.operator !== "!" ||
        parent.argument !== expression
      ) {
        break;
      }
      negated = !negated;
      expression = parent;
    }
    const effectiveOperator = negated
      ? candidate.operator === "&&" ? "||" : "&&"
      : candidate.operator;
    if (
      effectiveOperator === "&&" &&
      requiresFastModeEnabled(otherBranch, !negated, fastModeNames)
    ) {
      return true;
    }
  }
  return false;
}

function isAbsentValue(node) {
  return (
    (node?.type === "Literal" && node.value == null) ||
    (node?.type === "UnaryExpression" &&
      node.operator === "void" &&
      node.argument?.type === "Literal" &&
      node.argument.value === 0)
  );
}

function isAbruptStatement(node) {
  if (node?.type === "ReturnStatement" || node?.type === "ThrowStatement") return true;
  if (node?.type !== "BlockStatement" || node.body.length === 0) return false;
  return isAbruptStatement(node.body[node.body.length - 1]);
}

function hasDominatingFastModeGuard(node, fn, fastModeNames) {
  if (fn.body?.type !== "BlockStatement") return false;
  const targetIndex = fn.body.body.findIndex((statement) => containsNode(statement, node));
  if (targetIndex < 1) return false;
  const featureRequirementNames = new Set(["featureRequirements"]);
  return fn.body.body.slice(0, targetIndex).some((statement) =>
    statement.type === "IfStatement" &&
    isAbruptStatement(statement.consequent) &&
    containsName(statement.test, featureRequirementNames) &&
    requiresFastModeEnabled(statement.test, false, fastModeNames),
  );
}

function isFastModeAuthGate(node, ancestors, fn, source) {
  const fastModeNames = new Set(["fast_mode"]);
  const serviceTierNames = new Set([
    "isServiceTierAllowed",
    "serviceTier",
    "service_tier",
  ]);
  const hasFunctionFastContext =
    containsName(fn, fastModeNames) && containsName(fn, new Set(["featureRequirements"]));
  if (hasConjunctiveFastModeRelationship(node, ancestors, fastModeNames)) return true;
  if (
    hasFunctionFastContext &&
    (hasFastModeBindingConjunction(node, ancestors, fn, fastModeNames, source) ||
      hasFastModeGuardedReturn(node, ancestors, fn, fastModeNames, source))
  ) {
    return true;
  }
  if (!hasFunctionFastContext || !hasDominatingFastModeGuard(node, fn, fastModeNames)) {
    return false;
  }

  for (let index = ancestors.length - 1; index >= 0; index -= 1) {
    const candidate = ancestors[index];
    if (candidate === fn) break;
    if (candidate.type !== "ConditionalExpression" || !containsNode(candidate.test, node)) {
      continue;
    }
    const positiveComparison = node.operator === "===" || node.operator === "==";
    const negativeComparison = node.operator === "!==" || node.operator === "!=";
    if (!positiveComparison && !negativeComparison) continue;
    const serviceTierBranch = positiveComparison ? candidate.consequent : candidate.alternate;
    const absentBranch = positiveComparison ? candidate.alternate : candidate.consequent;
    if (isNamedReference(serviceTierBranch, serviceTierNames) && isAbsentValue(absentBranch)) {
      return true;
    }
  }
  return false;
}

function makeApiKeyLiteral(literalSource) {
  if (literalSource.startsWith("'")) return "'apikey'";
  if (literalSource.startsWith("`")) return "`apikey`";
  return '"apikey"';
}

function collectFastModeAuthGates(ast, source, label) {
  const gates = [];
  walkAst(ast, (node, ancestors) => {
    const comparison = getChatGptComparison(node);
    if (!comparison) return;
    const fn = findNearestFunction(ancestors);
    if (!fn) return;
    if (!isFastModeAuthGate(node, ancestors, fn, source)) return;
    const functionSource = source.slice(fn.start, fn.end);
    if (node.operator !== "===" && node.operator !== "!==") {
      fail(`${label} has an unsupported Fast mode auth comparison: ${node.operator}`);
    }
    if (!isStableAuthExpression(comparison.authExpression)) {
      fail(`${label} has a Fast mode auth comparison with an unstable expression`);
    }
    if (node.operator === "!==" && !isLegacyAuthExpression(comparison.authExpression)) {
      fail(`${label} has an unsupported legacy Fast mode auth comparison`);
    }

    const authSource = source.slice(
      comparison.authExpression.start,
      comparison.authExpression.end,
    );
    const literalSource = source.slice(comparison.literal.start, comparison.literal.end);
    const original = source.slice(node.start, node.end);
    const logicalOperator = node.operator === "===" ? "||" : "&&";
    const logicalRoot = findLogicalRoot(node, ancestors, logicalOperator);
    const expanded =
      logicalRoot !== node &&
      hasEquivalentApiKeyComparison(
        logicalRoot,
        source,
        authSource,
        node.operator,
        logicalOperator,
      );
    const expansionSource = source.slice(logicalRoot.start, logicalRoot.end);

    gates.push({
      authSource,
      context: {
        featureRequirements: functionSource.includes("featureRequirements"),
        isServiceTierAllowed: functionSource.includes("isServiceTierAllowed"),
        serviceTier: functionSource.includes("serviceTier"),
        serviceTierWire: functionSource.includes("service_tier"),
      },
      end: node.end,
      expanded,
      markerApplied: expanded && expansionSource.includes(FAST_MODE_PATCH_MARKER),
      operator: node.operator,
      original,
      apiKeyLiteral: makeApiKeyLiteral(literalSource),
      start: node.start,
    });
  });
  return gates;
}

function inspectFastModeSource(source, label = "Fast mode bundle") {
  const ast = parseJavaScript(source, label);
  const gates = collectFastModeAuthGates(ast, source, label);
  const unpatched = gates.filter((gate) => !gate.expanded);
  const expanded = gates.filter((gate) => gate.expanded);
  const markedExpanded = expanded.filter((gate) => gate.markerApplied);
  const nativeExpanded = expanded.filter((gate) => !gate.markerApplied);
  const markerTargets = countOccurrences(source, FAST_MODE_PATCH_MARKER);

  if (markerTargets !== markedExpanded.length) {
    fail(`${label} has a Fast mode patch marker outside a supported auth gate`);
  }

  let state = "not-applicable";
  if (unpatched.length > 0 && markerTargets > 0) state = "mixed";
  else if (unpatched.length > 0) state = "unpatched";
  else if (markerTargets > 0) state = "patched";
  else if (expanded.length > 0) state = "native";

  return {
    authGateTargets: unpatched.length,
    effectiveApiKeyGates: markedExpanded.length + nativeExpanded.length,
    expandedApiKeyGates: expanded.length,
    gates,
    markerTargets,
    nativeApiKeyGates: nativeExpanded.length,
    state,
    totalAuthGates: gates.length,
  };
}

function applyReplacements(source, replacements) {
  const ordered = [...replacements].sort((left, right) => right.start - left.start);
  let output = source;
  for (const replacement of ordered) {
    output =
      output.slice(0, replacement.start) +
      replacement.value +
      output.slice(replacement.end);
  }
  return output;
}

function applyFastModePatch(source, label = "Fast mode bundle") {
  const inspection = inspectFastModeSource(source, label);
  if (inspection.state === "mixed") {
    fail(`${label} contains a partially applied Fast mode patch`);
  }
  if (inspection.state !== "unpatched") {
    return { changed: false, inspection, source, targets: 0 };
  }

  const replacements = inspection.gates
    .filter((gate) => !gate.expanded)
    .map((gate) => ({
      end: gate.end,
      start: gate.start,
      value:
        gate.operator === "!=="
          ? `(${gate.original}${FAST_MODE_PATCH_COMMENT}&&${gate.authSource}!==${gate.apiKeyLiteral})`
          : `(${gate.original}${FAST_MODE_PATCH_COMMENT}||${gate.authSource}===${gate.apiKeyLiteral})`,
    }));
  const patchedSource = applyReplacements(source, replacements);
  parseJavaScript(patchedSource, `${label} after Fast mode patching`);
  const patchedInspection = inspectFastModeSource(patchedSource, label);
  if (
    patchedInspection.state !== "patched" ||
    patchedInspection.authGateTargets !== 0 ||
    patchedInspection.markerTargets < inspection.markerTargets + replacements.length
  ) {
    fail(`${label} did not pass Fast mode verification after patching`);
  }

  return {
    changed: true,
    inspection: patchedInspection,
    originalInspection: inspection,
    source: patchedSource,
    targets: replacements.length,
  };
}

module.exports = {
  FAST_MODE_PATCH_COMMENT,
  FAST_MODE_PATCH_MARKER,
  applyFastModePatch,
  collectFastModeAuthGates,
  inspectFastModeSource,
};
