import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const packageRoot = resolve(here, '..');

const candidates = [
  resolve(packageRoot, '../../../agent-skills/adobe-illustrator-scripting/node_modules/typescript/lib/tsc.js'),
  resolve(packageRoot, '../../../extendscript-toolchain/node_modules/typescript/lib/tsc.js'),
  resolve(packageRoot, '../../../eson/node_modules/typescript/lib/tsc.js'),
  resolve(packageRoot, '../../../arcfit/node_modules/typescript/lib/tsc.js')
];

let compiler = null;
let version = null;

for (const candidate of candidates) {
  if (!existsSync(candidate)) continue;

  const packageJson = resolve(candidate, '../../package.json');
  if (!existsSync(packageJson)) continue;

  const parsed = JSON.parse(readFileSync(packageJson, 'utf8'));
  if (parsed.version !== '5.9.3') continue;

  compiler = candidate;
  version = parsed.version;
  break;
}

if (!compiler) {
  console.error(
    'COM Tool V2 Node SDK typecheck requires an existing TypeScript 5.9.3 ' +
    'compiler at one of the approved /scripts workspace locations; no compiler ' +
    'was found and this gate does not install or download dependencies.'
  );
  process.exit(2);
}

const config = resolve(packageRoot, 'tsconfig.types-smoke.json');
const result = spawnSync(
  process.execPath,
  [compiler, '--project', config, '--pretty', 'false'],
  {
    cwd: packageRoot,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe']
  }
);

if (result.stdout) process.stdout.write(result.stdout);
if (result.stderr) process.stderr.write(result.stderr);

if (result.status !== 0) {
  process.exit(result.status ?? 1);
}

console.log(
  JSON.stringify({
    ok: true,
    typescriptVersion: version,
    compiler,
    config
  })
);
