'use strict';

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

function functionName(path, filename) {
  const node = path.node;
  if (node.id?.name) return node.id.name;
  if (node.key && !node.computed) return node.key.name ?? node.key.value;
  if (path.parentPath.isVariableDeclarator() && t.isIdentifier(path.parentPath.node.id)) return path.parentPath.node.id.name;
  return `<anonymous@${filename}:${node.loc?.start.line ?? 0}>`;
}

function arrowArguments(node) {
  const values = [];
  for (const parameter of node.params) {
    if (t.isIdentifier(parameter)) values.push(t.cloneNode(parameter));
    else if (t.isRestElement(parameter) && t.isIdentifier(parameter.argument)) values.push(t.spreadElement(t.cloneNode(parameter.argument)));
    else return null;
  }
  return t.arrayExpression(values);
}

function transform(source, filename, options = {}) {
  const ast = parser.parse(source, {
    sourceType: 'unambiguous', sourceFilename: filename,
    plugins: ['typescript', 'jsx', 'classProperties', 'classPrivateProperties', 'topLevelAwait']
  });
  const unsupported = [];
  traverse(ast, { Function: { exit(path) {
    const node = path.node;
    const name = functionName(path, filename);
    if (node.generator) {
      unsupported.push({ name, reason: 'UnsupportedShape', detail: 'Node: GeneratorFunction' });
      return;
    }
    const arrowArgs = path.isArrowFunctionExpression() ? arrowArguments(node) : null;
    if (path.isArrowFunctionExpression() && arrowArgs === null) {
      unsupported.push({ name, reason: 'UnsupportedShape', detail: 'Node: DestructuredArrowParameters' });
      return;
    }
    if (!t.isBlockStatement(node.body)) node.body = t.blockStatement([t.returnStatement(node.body)]);

    const frame = path.scope.generateUidIdentifier('bdFrame');
    const result = path.scope.generateUidIdentifier('bdResult');
    const completed = path.scope.generateUidIdentifier('bdCompleted');
    const error = path.scope.generateUidIdentifier('bdError');
    const caught = path.scope.generateUidIdentifier('bdCaught');
    path.get('body').traverse({
      Function(inner) { inner.skip(); },
      ReturnStatement(returnPath) {
        let value = returnPath.node.argument ?? t.identifier('undefined');
        if (node.async && returnPath.node.argument) value = t.awaitExpression(value);
        returnPath.node.argument = t.sequenceExpression([
          t.assignmentExpression('=', t.cloneNode(result), value),
          t.assignmentExpression('=', t.cloneNode(completed), t.booleanLiteral(true)),
          t.cloneNode(result)
        ]);
      }
    });
    const original = node.body.body;
    original.push(t.expressionStatement(t.assignmentExpression('=', t.cloneNode(completed), t.booleanLiteral(true))));
    const args = path.isArrowFunctionExpression() ? arrowArgs :
      t.callExpression(t.memberExpression(t.identifier('Array'), t.identifier('from')), [t.identifier('arguments')]);
    const metadata = t.objectExpression([
      t.objectProperty(t.identifier('methodFullName'), t.stringLiteral(`${filename}:${name}`)),
      t.objectProperty(t.identifier('filePath'), t.stringLiteral(filename)),
      t.objectProperty(t.identifier('line'), t.numericLiteral(node.loc?.start.line ?? 0)),
      t.objectProperty(t.identifier('async'), t.booleanLiteral(Boolean(node.async)))
    ]);
    node.body.body = [
      t.variableDeclaration('const', [t.variableDeclarator(frame, t.callExpression(runtimeMember('enter'), [metadata, args]))]),
      t.variableDeclaration('let', [t.variableDeclarator(result), t.variableDeclarator(completed, t.booleanLiteral(false)), t.variableDeclarator(error)]),
      t.tryStatement(t.blockStatement(original), t.catchClause(caught, t.blockStatement([
        t.expressionStatement(t.assignmentExpression('=', t.cloneNode(error), t.cloneNode(caught))),
        t.throwStatement(t.cloneNode(caught))
      ])), t.blockStatement([t.expressionStatement(t.callExpression(runtimeMember('exit'), [
        t.cloneNode(frame), t.conditionalExpression(t.cloneNode(completed), t.cloneNode(result), t.identifier('undefined')), t.cloneNode(error)
      ]))]))
    ];
  } } });
  return { code: generate(ast, { sourceMaps: options.sourceMaps ?? false, sourceFileName: filename }, source).code, unsupported };
}

module.exports = { transform };