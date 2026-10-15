// Rebuild from pinned source; no changes to package caches or installed xterm.
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
const here = dirname(fileURLToPath(import.meta.url));
const generated = resolve(process.argv[2]);
const require = createRequire(resolve(generated, 'build-tools/package.json'));
const { build } = require('esbuild');
const source = resolve(generated, 'package/src');
const common = { bundle:true, resolveExtensions:['.ts','.js','.d.ts'], nodePaths:[source], tsconfigRaw:{compilerOptions:{experimentalDecorators:true,useDefineForClassFields:false}}, logLevel:'warning' };
const result = await build({...common, entryPoints:[resolve(source,'browser/public/Terminal.ts')],
  format:'iife', globalName:'XtermResearch', target:'es2020', minify:true, legalComments:'inline',
  banner:{js:'/*! xterm.js 5.5.0 MIT; MODIFIED TerminalSurfacePrototype research 2026-10-15. See ThirdParty/xterm-LICENSE and xterm-TerminalResearch-MODIFICATIONS.txt. */'},
  footer:{js:'var Terminal = XtermResearch.Terminal;'}, write:false});
const asset = result.outputFiles[0].contents;
const hash = createHash('sha256').update(asset).digest('hex');
const expected = JSON.parse(readFileSync(resolve(here,'checksums.json'))).asset;
if (hash !== expected) throw new Error('Rebuilt asset checksum mismatch: '+hash);
writeFileSync(resolve(generated,'xterm.js'),asset);
await build({...common, entryPoints:[resolve(here,'checks.ts')], alias:{'research-source':source},
  platform:'node', format:'cjs', outfile:resolve(generated,'checks.cjs')});
console.log('Rebuilt asset verified: '+hash);
