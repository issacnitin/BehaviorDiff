'use strict';

const path = require('node:path');
const parser = require('@babel/parser');
const traverse = require('@babel/traverse').default;
const generate = require('@babel/generator').default;
const t = require('@babel/types');

function runtimeMember(name) {
  return t.memberExpression(t.memberExpression(t.identifier('globalThis'), t.callExpression(
    t.memberExpression(t.identifier('Symbol'), t.identifier('for')),
    [t.stringLiteral('behaviordiff.runtime')]
  ), true), t.identifier(name));
}

function relativeSourcePath(filename, repositoryRoot = process.cwd()) {
  const absolute = path.resolve(filename);
  return path.relative(path.resolve(repositoryRoot), absolute).replaceAll('\\', '/');
}

function propertyName(node) {
  if (t.isIdentifier(node)) return node.name;
  if (t.isPrivateName(node) && t.isIdentifier(node.id)) return `#${node.id.name}`;
  if (t.isStringLiteral(node) || t.isNumericLiteral(node)) return String(node.value);
  return null;
}

function className(functionPath) {
  const classPath = functionPath.findParent(parent => parent.isClass());
  if (!classPath) return null;
  if (classPath.node.id?.name) return classPath.node.id.name;
  if (classPath.parentPath.isVariableDeclarator() && t.isIdentifier(classPath.parentPath.node.id)) {
    return classPath.parentPath.node.id.name;
  }
  return null;
}

function objectName(objectPath) {
  if (!objectPath?.isObjectExpression()) return null;
  if (objectPath.parentPath.isVariableDeclarator() && t.isIdentifier(objectPath.parentPath.node.id)) {
    return objectPath.parentPath.node.id.name;
  }
  if (objectPath.parentPath.isAssignmentExpression() && t.isIdentifier(objectPath.parentPath.node.left)) {
    return objectPath.parentPath.node.left.name;
  }
  return null;
}

function localCallableName(functionPath) {
  const node = functionPath.node;
  if (node.id?.name) return node.id.name;
  if (functionPath.isClassMethod() || functionPath.isClassPrivateMethod()) {
    const key = propertyName(node.key);
    const owner = className(functionPath);
    return owner && key ? `${owner}.${key}` : key;
  }
  if (functionPath.isObjectMethod()) {
    const key = propertyName(node.key);
    const owner = objectName(functionPath.parentPath);
    return owner && key ? `${owner}.${key}` : key;
  }
  if (functionPath.parentPath.isVariableDeclarator() && t.isIdentifier(functionPath.parentPath.node.id)) {
    return functionPath.parentPath.node.id.name;
  }
  if ((functionPath.parentPath.isObjectProperty() || functionPath.parentPath.isClassProperty())
      && !functionPath.parentPath.node.computed) {
    const key = propertyName(functionPath.parentPath.node.key);
    const owner = objectName(functionPath.parentPath.parentPath);
    return owner && key ? `${owner}.${key}` : key;
  }
  if (functionPath.parentPath.isAssignmentExpression()) {
    const left = functionPath.parentPath.node.left;
    if (t.isIdentifier(left)) return left.name;
    if (t.isMemberExpression(left) && !left.computed) {
      const object = t.isIdentifier(left.object) ? left.object.name : null;
      const property = propertyName(left.property);
      if (object && property) return `${object}.${property}`;
    }
  }
  return null;
}

function callableIdentity(functionPath) {
  const location = functionPath.node.loc?.start;
  const parts = [localCallableName(functionPath) ?? `<anonymous@${location?.line ?? 0}:${location?.column ?? 0}>`];
  let parentFunction = functionPath.findParent(parent => parent.isFunction());
  while (parentFunction) {
    const parentLocation = parentFunction.node.loc?.start;
    parts.unshift(localCallableName(parentFunction)
      ?? `<anonymous@${parentLocation?.line ?? 0}:${parentLocation?.column ?? 0}>`);
    parentFunction = parentFunction.findParent(parent => parent.isFunction());
  }
  return parts.join('.');
}

function arrowArguments(node) {
  const values = [];
  for (const parameter of node.params) {
    if (t.isIdentifier(parameter)) values.push(t.cloneNode(parameter));
    else if (t.isAssignmentPattern(parameter) && t.isIdentifier(parameter.left)) {
      values.push(t.cloneNode(parameter.left));
    }
    else if (t.isRestElement(parameter) && t.isIdentifier(parameter.argument)) {
      values.push(t.spreadElement(t.cloneNode(parameter.argument)));
    } else return null;
  }
  return t.arrayExpression(values);
}

function isDerivedConstructor(functionPath) {
  if (!(functionPath.isClassMethod() || functionPath.isClassPrivateMethod())
      || functionPath.node.kind !== 'constructor') return false;
  const owner = functionPath.findParent(parent => parent.isClass());
  return Boolean(owner?.node.superClass);
}

function memberFor(functionPath, modulePath, decision) {
  const location = functionPath.node.loc?.start;
  const member = {
    methodFullName: `${modulePath}#${callableIdentity(functionPath)}`,
    status: decision.status,
    returnKind: functionPath.node.generator ? 'Generator' : functionPath.node.async ? 'Async' : 'Sync',
    sourceResolution: 'debugInfo',
    line: location?.line ?? 0,
    column: location?.column ?? 0
  };
  if (decision.skipReason) member.skipReason = decision.skipReason;
  if (decision.detail) member.detail = decision.detail;
  return member;
}

function skipDecision(functionPath, options, arrowArgs) {
  if (options.instrument === false) {
    return {
      status: 'Skipped',
      skipReason: options.skipReason ?? 'ExcludedByScope',
      detail: options.skipDetail ?? 'Node: ExcludedByScope'
    };
  }
  if (!functionPath.node.body) {
    return { status: 'Skipped', skipReason: 'DeclaredExternally', detail: 'Node: NoBody' };
  }
  if (functionPath.node.generator) {
    return { status: 'Skipped', skipReason: 'UnsupportedShape', detail: 'Node: GeneratorFunction' };
  }
  if (functionPath.isArrowFunctionExpression() && arrowArgs === null) {
    return { status: 'Skipped', skipReason: 'UnsupportedShape', detail: 'Node: DestructuredArrowParameters' };
  }
  if (isDerivedConstructor(functionPath)) {
    return { status: 'Skipped', skipReason: 'UnsupportedShape', detail: 'Node: DerivedConstructor' };
  }
  return { status: 'Patched' };
}

function transform(source, filename, options = {}) {
  const modulePath = relativeSourcePath(filename, options.repositoryRoot);
  const ast = parser.parse(source, {
    sourceType: 'unambiguous',
    sourceFilename: modulePath,
    plugins: ['typescript', 'jsx', 'classProperties', 'classPrivateProperties', 'topLevelAwait']
  });
  const members = [];

  traverse(ast, { Function: { exit(functionPath) {
    const node = functionPath.node;
    const arrowArgs = functionPath.isArrowFunctionExpression() ? arrowArguments(node) : undefined;
    const decision = skipDecision(functionPath, options, arrowArgs);
    const member = memberFor(functionPath, modulePath, decision);
    members.push(member);
    if (decision.status !== 'Patched') return;

    if (!t.isBlockStatement(node.body)) {
      node.body = t.blockStatement([t.returnStatement(node.body)]);
    }
    const originalBody = node.body;
    const callbackBody = t.blockStatement(
      originalBody.body,
      originalBody.directives.map(directive => t.cloneNode(directive))
    );
    const callback = t.arrowFunctionExpression([], callbackBody, node.async);
    const args = functionPath.isArrowFunctionExpression()
      ? arrowArgs
      : t.callExpression(t.memberExpression(t.identifier('Array'), t.identifier('from')), [t.identifier('arguments')]);
    const metadata = t.valueToNode({
      assembly: modulePath,
      methodFullName: member.methodFullName,
      filePath: modulePath,
      filePathResolution: 'debugInfo',
      line: member.line,
      column: member.column
    });
    node.body = t.blockStatement([
      t.returnStatement(t.callExpression(runtimeMember(node.async ? 'runAsync' : 'runSync'), [metadata, args, callback]))
    ], originalBody.directives);
  } } });

  if (options.instrument !== false) {
    const registration = t.expressionStatement(t.callExpression(runtimeMember('registerModule'), [
      t.valueToNode({ assembly: modulePath, members })
    ]));
    const additions = options.bootstrapImport
      ? [t.importDeclaration([], t.stringLiteral(options.bootstrapImport)), registration]
      : [registration];
    let insertion = 0;
    while (insertion < ast.program.body.length && t.isImportDeclaration(ast.program.body[insertion])) insertion++;
    ast.program.body.splice(insertion, 0, ...additions);
  }

  return {
    code: generate(ast, { sourceMaps: options.sourceMaps ?? false, sourceFileName: modulePath }, source).code,
    modulePath,
    members,
    unsupported: members.filter(member => member.status === 'Skipped')
  };
}

module.exports = { transform, relativeSourcePath };