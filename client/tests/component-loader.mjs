import { existsSync, readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import ts from 'typescript';

// Node cannot execute Angular's legacy decorators. Compile project components in memory
// with the installed TypeScript compiler; production dependencies load normally.
const sourceRoot = new URL('../src/', import.meta.url).href;

registerHooks({
  resolve(specifier, context, nextResolve) {
    // Match Angular's bundler: this dependency advertises ESM through its module field.
    if (specifier === 'file-saver-es') {
      return nextResolve(new URL('../node_modules/file-saver-es/src/FileSaver.js', import.meta.url).href, context);
    }

    if (context.parentURL?.startsWith(sourceRoot)) {
      const url = specifier.startsWith('.')
        ? new URL(specifier, context.parentURL)
        : specifier.startsWith('src/')
          ? new URL(specifier.slice(4), sourceRoot)
          : null;

      if (url && !url.pathname.endsWith('.ts') && existsSync(new URL(`${url.href}.ts`))) {
        return nextResolve(`${url.href}.ts`, context);
      }
    }

    return nextResolve(specifier, context);
  },
  load(url, context, nextLoad) {
    if (url.startsWith(sourceRoot) && url.endsWith('.ts')) {
      return {
        format: 'module',
        shortCircuit: true,
        source: ts.transpileModule(readFileSync(new URL(url), 'utf8'), {
          compilerOptions: {
            target: ts.ScriptTarget.ES2022,
            module: ts.ModuleKind.ESNext,
            experimentalDecorators: true,
            useDefineForClassFields: false,
          },
        }).outputText,
      };
    }

    return nextLoad(url, context);
  },
});
