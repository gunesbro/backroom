import path from 'node:path';

/**
 * Features talk to each other only through their index.ts (docs/requirements.md, "Web ve masaüstü").
 *
 * The import is resolved against the file system, so alias ('@/features/x/...'), relative
 * ('../../x/...') and sibling-feature escapes are all caught. A file may import anything inside
 * its own feature; anything else may import a feature only through its folder or its index.
 */

const FEATURE_FILE =
  /^(?<src>.*[\\/]apps[\\/][^\\/]+[\\/]src)[\\/]features[\\/](?<feature>[^\\/]+)(?:[\\/]|$)/;

function featureOf(filePath) {
  const match = FEATURE_FILE.exec(filePath);
  return match?.groups ? { src: match.groups.src, feature: match.groups.feature } : null;
}

function srcRootOf(filePath) {
  const match = /^(?<src>.*[\\/]apps[\\/][^\\/]+[\\/]src)(?:[\\/]|$)/.exec(filePath);
  return match?.groups?.src ?? null;
}

function resolveImport(importPath, fromFile) {
  if (importPath.startsWith('@/')) {
    const src = srcRootOf(fromFile);
    return src ? path.join(src, importPath.slice(2)) : null;
  }

  if (importPath.startsWith('.')) {
    return path.resolve(path.dirname(fromFile), importPath);
  }

  return null;
}

function isFeaturePublicEntry(target, src, feature) {
  const featureRoot = path.join(src, 'features', feature);
  const relative = path.relative(featureRoot, target);
  return relative === '' || /^index(\.[cm]?[jt]sx?)?$/.test(relative);
}

/** @type {import('eslint').Rule.RuleModule} */
const featureBoundaries = {
  meta: {
    type: 'problem',
    docs: { description: 'Import other features only through their index.ts.' },
    messages: {
      internal:
        "Import from the '{{feature}}' feature's index.ts (e.g. '@/features/{{feature}}'), not from its internals.",
    },
    schema: [],
  },
  create(context) {
    const fromFile = context.filename;
    const own = featureOf(fromFile);

    function check(node) {
      const importPath = node.source?.value;
      if (typeof importPath !== 'string') {
        return;
      }

      const target = resolveImport(importPath, fromFile);
      const targetFeature = target ? featureOf(target) : null;
      if (!target || !targetFeature) {
        return;
      }

      const sameFeature = own && own.src === targetFeature.src && own.feature === targetFeature.feature;
      if (sameFeature || isFeaturePublicEntry(target, targetFeature.src, targetFeature.feature)) {
        return;
      }

      context.report({ node: node.source, messageId: 'internal', data: { feature: targetFeature.feature } });
    }

    return {
      ImportDeclaration: check,
      ExportNamedDeclaration: check,
      ExportAllDeclaration: check,
      ImportExpression: check,
    };
  },
};

export default {
  meta: { name: 'backroom' },
  rules: { 'feature-boundaries': featureBoundaries },
};
