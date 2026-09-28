#!/usr/bin/env node
// Offline generator for the COM Tool V2 knowledge pack.
//
// This script is a BUILD-TIME tool. It is never invoked by the runtime. The
// runtime reads only the generated pack text through ComTool.Knowledge, which
// has no SQLite dependency and never loads the multi-megabyte source JSON.
//
// Authoritative source for this lane is the clean, tracked
// data/illustrator_com.sqlite index plus data/inventory_manifest.json. The
// generator:
//
//   1. hashes the SQLite database before and after the read and fails if the
//      bytes changed (proves the read-only contract);
//   2. cross-checks every count and the recorded source_sha256 in the database
//      `meta` table against inventory_manifest.json and fails closed on any
//      disagreement, rather than generating a pack;
//   3. reads the database through the sqlite3 CLI in `mode=ro`, so no journal,
//      WAL or temp file can be created next to the legacy artifact;
//   4. emits a canonical, byte-deterministic pack (every section is emitted in
//      a total order and no wall-clock value is written into the pack).
//
// Usage:
//   node scripts/build-knowledge-pack.mjs [--check] [--out <path>]
//        [--db <path>] [--manifest <path>] [--sqlite <sqlite3.exe>]
//        [--host-version <version>] [--host-product <name>]
//
// `--check` regenerates into memory and fails when the committed pack differs.

import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, statSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const V2_ROOT = path.resolve(HERE, '..');
const SCRIPTS_ROOT = path.resolve(V2_ROOT, '..');
const SKILL_ROOT = path.join(
  SCRIPTS_ROOT,
  'agent-skills',
  'illustrator-com-automation-skill');

export const PACK_FORMAT = 'comtool.knowledge.pack/v1';
export const PACK_VERSION = 1;
export const PACK_MAGIC = 'comtool.knowledge.pack/1';

const DEFAULT_OUT = path.join(
  V2_ROOT,
  'src',
  'ComTool.Knowledge',
  'Assets',
  'illustrator-com.knowledge-pack');

const SECTION_ORDER = [
  'interfaces',
  'implemented',
  'methods',
  'parameters',
  'properties',
  'accessors',
  'accessor_parameters',
  'enums',
  'enum_values',
];

// Manifest key -> expected database row, used for the fail-closed cross-check.
const MANIFEST_TO_META = new Map([
  ['source_file', 'source_file'],
  ['source_sha256', 'source_sha256'],
  ['generated_at_utc', 'generated_at_utc'],
  ['interfaces', 'interfaces'],
  ['methods', 'methods'],
  ['properties', 'properties'],
  ['property_accessors', 'property_accessors'],
  ['parameters', 'parameters'],
  ['coclass_implemented_interfaces', 'coclass_implemented_interfaces'],
  ['source_outgoing_interfaces_flagged', 'source_outgoing_interfaces_flagged'],
  ['enums', 'enums'],
  ['enum_values', 'enum_values'],
]);

class PackGenerationError extends Error {
  constructor(kind, message) {
    super(message);
    this.kind = kind;
  }
}

function fail(kind, message) {
  throw new PackGenerationError(kind, message);
}

function parseArgs(argv) {
  const options = {
    check: false,
    out: DEFAULT_OUT,
    db: path.join(SKILL_ROOT, 'data', 'illustrator_com.sqlite'),
    manifest: path.join(SKILL_ROOT, 'data', 'inventory_manifest.json'),
    json: path.join(SKILL_ROOT, 'data', 'illustrator_com_commands.json'),
    sqlite: undefined,
    observedHostVersion: undefined,
    observedHostProduct: undefined,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const value = () => {
      i += 1;
      if (i >= argv.length) fail('invalid_argument', `${arg} requires a value.`);
      return argv[i];
    };
    switch (arg) {
      case '--check': options.check = true; break;
      case '--out': options.out = path.resolve(value()); break;
      case '--db': options.db = path.resolve(value()); break;
      case '--manifest': options.manifest = path.resolve(value()); break;
      case '--json': options.json = path.resolve(value()); break;
      case '--sqlite': options.sqlite = value(); break;
      case '--observed-host-version': options.observedHostVersion = value(); break;
      case '--observed-host-product': options.observedHostProduct = value(); break;
      case '--help':
      case '-h':
        process.stdout.write(`${HELP}\n`);
        process.exit(0);
        break;
      default:
        fail('invalid_argument', `Unknown argument '${arg}'.`);
    }
  }

  return options;
}

const HELP = `build-knowledge-pack.mjs

Build-time generator for the ComTool V2 knowledge pack. Not used at runtime.

  --check            regenerate in memory and fail when the committed pack differs
  --out <path>       pack output path
  --db <path>        authoritative SQLite index
  --manifest <path>  inventory manifest used for the fail-closed cross-check
  --json <path>      source JSON, hashed only to report on-disk drift
  --sqlite <exe>     sqlite3 CLI to use (default: resolved from PATH)
  --observed-host-version <v>   build-machine Illustrator version; recorded as a
                                 non-authoritative observation only
  --observed-host-product <n>   build-machine Illustrator product name

Pack authority is the SQLite index hash plus the manifest source hash, counts and
export timestamp. The COM type library carries no application version, so
pack.host.version is 'unknown' unless the manifest itself declares one. Values
passed to --observed-host-version are never used as pack provenance.
`;

// Manifest keys that would legitimately declare the exporting host version.
// When none is present the pack reports version=unknown instead of inferring one
// from the build machine.
const MANIFEST_HOST_VERSION_KEYS = [
  'host_version',
  'illustrator_version',
  'hostVersion',
  'illustratorVersion',
];

function declaredHostVersion(manifest) {
  for (const key of MANIFEST_HOST_VERSION_KEYS) {
    const value = manifest[key];
    if (typeof value === 'string' && value.trim() !== '') {
      return { version: value.trim(), key };
    }
  }
  return { version: 'unknown', key: null };
}

function sha256File(file) {
  const hash = createHash('sha256');
  hash.update(readFileSync(file));
  return hash.digest('hex');
}

function ordinal(a, b) {
  const left = a ?? '';
  const right = b ?? '';
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}

function compareNumbers(a, b) {
  const left = a === null || a === undefined ? 0 : a;
  const right = b === null || b === undefined ? 0 : b;
  return left === right ? 0 : left < right ? -1 : 1;
}

// sqlite3 CLI cannot be given a NUL byte in -nullvalue, so SQL NULL is mapped to
// a control character that cannot occur in a COM type-library field. The emitted
// body is checked for the sentinel afterwards and generation fails closed if it
// ever appears, so a collision can never silently corrupt a record.
const NULL_SENTINEL = '\u001D';
const TAB = '\t';

function escapeField(value) {
  if (value === null || value === undefined) return '';
  return String(value)
    .replace(/\\/g, '\\\\')
    .replace(/\t/g, '\\t')
    .replace(/\r/g, '\\r')
    .replace(/\n/g, '\\n');
}

function row(fields) {
  return fields.map(escapeField).join('\t');
}

// --- sqlite3 access ----------------------------------------------------------

function resolveSqlite(explicit) {
  if (explicit) {
    if (!existsSync(explicit)) {
      fail('sqlite_not_found', `sqlite3 not found at '${explicit}'.`);
    }
    return explicit;
  }
  try {
    execFileSync('sqlite3', ['-version'], { stdio: 'ignore' });
    return 'sqlite3';
  } catch {
    fail(
      'sqlite_not_found',
      'sqlite3 CLI was not found on PATH. Pass --sqlite <sqlite3.exe>. '
      + 'The runtime never needs sqlite3; only pack regeneration does.');
    return undefined;
  }
}

function openReadOnlyQuery(sqliteExe, dbPath) {
  // mode=ro makes the sqlite3 CLI open the file read-only through a URI, so it
  // cannot create -wal/-shm/journal siblings next to the legacy artifact.
  const uri = `file:${dbPath.replace(/\\/g, '/')}?mode=ro`;
  return (sql) => {
    let stdout;
    try {
      stdout = execFileSync(
        sqliteExe,
        ['-batch', '-noheader', '-separator', TAB, '-nullvalue', NULL_SENTINEL, uri, sql],
        { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, windowsHide: true });
    } catch (error) {
      fail(
        'sqlite_query_failed',
        `sqlite3 query failed: ${error.message}\nSQL: ${sql}`);
      return [];
    }
    if (stdout.length === 0) return [];
    const lines = stdout.replace(/\r?\n$/, '').split(/\r?\n/);
    return lines.map((line) => line.split(TAB).map((cell) => (
      cell === NULL_SENTINEL ? null : cell
    )));
  };
}

// --- extraction --------------------------------------------------------------

function loadManifest(manifestPath) {
  if (!existsSync(manifestPath)) {
    fail('manifest_not_found', `Manifest not found: ${manifestPath}`);
  }
  let manifest;
  try {
    manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  } catch (error) {
    fail('manifest_unreadable', `Manifest is not valid JSON: ${error.message}`);
  }
  return manifest;
}

function loadMeta(query) {
  const meta = new Map();
  for (const [key, raw] of query('SELECT key, value FROM meta ORDER BY key')) {
    let value = raw;
    try {
      value = JSON.parse(raw);
    } catch {
      // Quoted or plain text values are kept verbatim.
    }
    meta.set(key, value);
  }
  return meta;
}

function crossCheckAgainstManifest(meta, manifest) {
  const mismatches = [];
  for (const [manifestKey, metaKey] of MANIFEST_TO_META) {
    if (!(manifestKey in manifest)) {
      mismatches.push(`manifest is missing '${manifestKey}'`);
      continue;
    }
    if (!meta.has(metaKey)) {
      mismatches.push(`database meta is missing '${metaKey}'`);
      continue;
    }
    const expected = manifest[manifestKey];
    const actual = meta.get(metaKey);
    if (String(expected) !== String(actual)) {
      mismatches.push(
        `${manifestKey}: manifest=${JSON.stringify(expected)} `
        + `database=${JSON.stringify(actual)}`);
    }
  }

  if (mismatches.length > 0) {
    fail(
      'manifest_database_mismatch',
      'Refusing to generate: the SQLite index and inventory_manifest.json '
      + `disagree.\n  - ${mismatches.join('\n  - ')}`);
  }
}

function verifyRowCounts(query, counts) {
  const checks = [
    ['interfaces', 'SELECT COUNT(*) FROM interfaces'],
    ['methods', 'SELECT COUNT(*) FROM methods'],
    ['methodParameters', 'SELECT COUNT(*) FROM parameters'],
    ['properties', 'SELECT COUNT(*) FROM properties'],
    ['propertyAccessors', 'SELECT COUNT(*) FROM accessors'],
    ['accessorParameters', 'SELECT COUNT(*) FROM accessor_parameters'],
    ['parameters', 'SELECT (SELECT COUNT(*) FROM parameters) + (SELECT COUNT(*) FROM accessor_parameters)'],
    ['implementedInterfaces', 'SELECT COUNT(*) FROM implemented_interfaces'],
    ['enums', 'SELECT COUNT(*) FROM enums'],
    ['enumValues', 'SELECT COUNT(*) FROM enum_values'],
  ];

  const mismatches = [];
  for (const [key, sql] of checks) {
    const [[actual]] = query(sql);
    if (Number(actual) !== counts[key]) {
      mismatches.push(`${key}: manifest=${counts[key]} database=${actual}`);
    }
  }

  if (mismatches.length > 0) {
    fail(
      'manifest_database_mismatch',
      'Refusing to generate: database row counts disagree with the manifest.\n'
      + `  - ${mismatches.join('\n  - ')}`);
  }
}

function extract(query) {
  const interfaces = query(
    'SELECT name, kind, guid, flags FROM interfaces ORDER BY name COLLATE BINARY')
    .map(([name, kind, guid, flags]) => ({ name, kind, guid, flags }))
    .sort((a, b) => ordinal(a.name, b.name));

  const implemented = query(
    'SELECT coclass_name, interface_name, flags FROM implemented_interfaces '
    + 'ORDER BY coclass_name COLLATE BINARY, interface_name COLLATE BINARY')
    .map(([coclassName, interfaceName, flags]) => ({ coclassName, interfaceName, flags }))
    .sort((a, b) => ordinal(a.coclassName, b.coclassName) || ordinal(a.interfaceName, b.interfaceName));

  const rawMethods = query(
    'SELECT interface_name, name, dispid, invoke_kind, function_kind, return_type, '
    + 'optional_parameter_count, flags FROM methods')
    .map(([interfaceName, name, dispid, invokeKind, functionKind, returnType, optionalCount, flags]) => ({
      interfaceName,
      name,
      dispid: dispid === null ? null : Number(dispid),
      invokeKind,
      functionKind,
      returnType,
      optionalCount: optionalCount === null ? null : Number(optionalCount),
      flags,
    }))
    .sort((a, b) => ordinal(a.interfaceName, b.interfaceName)
      || ordinal(a.name, b.name)
      || compareNumbers(a.dispid, b.dispid)
      || ordinal(a.invokeKind, b.invokeKind)
      || ordinal(a.returnType, b.returnType));

  // Method and property identifiers are reassigned in canonical emission order
  // so the pack is a pure function of the inventory content, not of the
  // database row ids.
  const rawProperties = query(
    'SELECT interface_name, name, dispid FROM properties')
    .map(([interfaceName, name, dispid]) => ({
      interfaceName,
      name,
      dispid: dispid === null ? null : Number(dispid),
    }))
    .sort((a, b) => ordinal(a.interfaceName, b.interfaceName)
      || ordinal(a.name, b.name)
      || compareNumbers(a.dispid, b.dispid));

  const methodIdByIndex = new Map(rawMethods.map((method, index) => [`${method.interfaceName} ${method.name} ${method.dispid}`, index + 1]));
  const propertyIdByIndex = new Map(rawProperties.map((prop, index) => [`${prop.interfaceName} ${prop.name} ${prop.dispid}`, index + 1]));

  const methodIdFor = (interfaceName, name, dispid) => methodIdByIndex.get(`${interfaceName} ${name} ${dispid}`);
  const propertyIdFor = (interfaceName, name, dispid) => propertyIdByIndex.get(`${interfaceName} ${name} ${dispid}`);

  const parameters = query(
    'SELECT m.interface_name, m.name, m.dispid, p.position, p.name, p.type, p.flags '
    + 'FROM parameters p JOIN methods m ON m.id = p.method_id')
    .map(([interfaceName, name, dispid, position, paramName, paramType, flags]) => ({
      methodId: methodIdFor(interfaceName, name, dispid === null ? null : Number(dispid)),
      position: Number(position),
      name: paramName,
      type: paramType,
      flags,
    }))
    .filter((param) => param.methodId !== undefined)
    .sort((a, b) => compareNumbers(a.methodId, b.methodId) || compareNumbers(a.position, b.position));

  // Accessor order inside a property follows the source declaration order (the
  // database row id) because the documented property type is derived from the
  // FIRST non-void accessor return type. Reordering accessors would silently
  // change reported types.
  const rawAccessors = query(
    'SELECT p.interface_name, p.name, p.dispid, a.id, a.name, a.dispid, a.invoke_kind, '
    + 'a.function_kind, a.return_type, a.optional_parameter_count, a.flags '
    + 'FROM accessors a JOIN properties p ON p.id = a.property_id')
    .map(([interfaceName, propertyName, propertyDispid, accessorSourceId, accessorName, dispid, invokeKind, functionKind, returnType, optionalCount, flags]) => ({
      propertyId: propertyIdFor(interfaceName, propertyName, propertyDispid === null ? null : Number(propertyDispid)),
      sourceId: Number(accessorSourceId),
      name: accessorName,
      dispid: dispid === null ? null : Number(dispid),
      invokeKind,
      functionKind,
      returnType,
      optionalCount: optionalCount === null ? null : Number(optionalCount),
      flags,
    }))
    .filter((accessor) => accessor.propertyId !== undefined)
    .sort((a, b) => compareNumbers(a.propertyId, b.propertyId) || compareNumbers(a.sourceId, b.sourceId));

  const accessorParameters = query(
    'SELECT p.interface_name, p.name, p.dispid, a.id, ap.position, ap.name, ap.type, ap.flags '
    + 'FROM accessor_parameters ap '
    + 'JOIN accessors a ON a.id = ap.accessor_id '
    + 'JOIN properties p ON p.id = a.property_id')
    .map(([interfaceName, propertyName, propertyDispid, accessorSourceId, position, paramName, paramType, flags]) => ({
      propertyId: propertyIdFor(interfaceName, propertyName, propertyDispid === null ? null : Number(propertyDispid)),
      accessorSourceId: Number(accessorSourceId),
      position: Number(position),
      name: paramName,
      type: paramType,
      flags,
    }))
    .filter((param) => param.propertyId !== undefined)
    .map((param) => ({
      accessorId: accessorIdOf(rawAccessors, param),
      position: param.position,
      name: param.name,
      type: param.type,
      flags: param.flags,
    }))
    .filter((param) => param.accessorId !== undefined)
    .sort((a, b) => compareNumbers(a.accessorId, b.accessorId) || compareNumbers(a.position, b.position));

  const enums = query('SELECT name FROM enums ORDER BY name COLLATE BINARY')
    .map(([name]) => name)
    .sort(ordinal);

  const enumValues = query(
    'SELECT enum_name, name, value FROM enum_values')
    .map(([enumName, name, value]) => ({
      enumName,
      name,
      value: value === null ? null : Number(value),
    }))
    .sort((a, b) => ordinal(a.enumName, b.enumName)
      || compareNumbers(a.value, b.value)
      || ordinal(a.name, b.name));

  return {
    interfaces,
    implemented,
    methods: rawMethods.map((method, index) => ({ id: index + 1, ...method })),
    parameters,
    properties: rawProperties.map((prop, index) => ({ id: index + 1, ...prop })),
    accessors: rawAccessors.map((accessor, index) => ({ id: index + 1, ...accessor })),
    accessorParameters,
    enums,
    enumValues,
  };
}

function accessorIdOf(rawAccessors, param) {
  const index = rawAccessors.findIndex((accessor) => accessor.propertyId === param.propertyId
    && accessor.sourceId === param.accessorSourceId);
  return index < 0 ? undefined : index + 1;
}

// --- emission ----------------------------------------------------------------

function buildBody(data) {
  const lines = [];

  lines.push(`##interfaces`);
  for (const item of data.interfaces) {
    lines.push(row([item.name, item.kind, item.guid, item.flags]));
  }

  lines.push(`##implemented`);
  for (const item of data.implemented) {
    lines.push(row([item.coclassName, item.interfaceName, item.flags]));
  }

  lines.push(`##methods`);
  for (const item of data.methods) {
    lines.push(row([
      item.id,
      item.interfaceName,
      item.name,
      item.dispid,
      item.invokeKind,
      item.functionKind,
      item.returnType,
      item.optionalCount,
      item.flags,
    ]));
  }

  lines.push(`##parameters`);
  for (const item of data.parameters) {
    lines.push(row([item.methodId, item.position, item.name, item.type, item.flags]));
  }

  lines.push(`##properties`);
  for (const item of data.properties) {
    lines.push(row([item.id, item.interfaceName, item.name, item.dispid]));
  }

  lines.push(`##accessors`);
  for (const item of data.accessors) {
    lines.push(row([
      item.id,
      item.propertyId,
      item.name,
      item.dispid,
      item.invokeKind,
      item.functionKind,
      item.returnType,
      item.optionalCount,
      item.flags,
    ]));
  }

  lines.push(`##accessor_parameters`);
  for (const item of data.accessorParameters) {
    lines.push(row([item.accessorId, item.position, item.name, item.type, item.flags]));
  }

  lines.push(`##enums`);
  for (const item of data.enums) {
    lines.push(row([item]));
  }

  lines.push(`##enum_values`);
  for (const item of data.enumValues) {
    lines.push(row([item.enumName, item.name, item.value]));
  }

  return `${lines.join('\n')}\n`;
}

function assertNoSentinel(body) {
  if (body.includes(NULL_SENTINEL)) {
    fail(
      'null_sentinel_collision',
      'Refusing to generate: a source field contained the NULL sentinel byte. '
      + 'The pack format cannot represent this value unambiguously.');
  }
  return body;
}

function buildHeader({
  meta,
  manifest,
  data,
  databaseSha256,
  databasePath,
  manifestPath,
  jsonOnDiskSha256,
  observedHostVersion,
  observedHostProduct,
}) {
  const body = assertNoSentinel(buildBody(data));
  const bodyBytes = Buffer.byteLength(body, 'utf8');
  const bodySha256 = createHash('sha256').update(body, 'utf8').digest('hex');
  const jsonMatchesManifest = jsonOnDiskSha256 === manifest.source_sha256;
  const host = declaredHostVersion(manifest);

  // Provenance block: pack authority. Every field here is derived from the
  // SQLite index and the manifest only.
  const header = [
    PACK_MAGIC,
    `pack.format=${PACK_FORMAT}`,
    `pack.version=${PACK_VERSION}`,
    'pack.encoding=utf-8',
    'pack.lineTerminator=lf',
    'pack.fieldSeparator=tab',
    'pack.escape=backslash',
    'pack.sections=' + SECTION_ORDER.join(','),
    'pack.provenance.source.kind=sqlite-index',
    `pack.provenance.source.database=${path.basename(databasePath)}`,
    `pack.provenance.source.databaseSha256=${databaseSha256}`,
    `pack.provenance.source.json=${manifest.source_file}`,
    `pack.provenance.source.jsonSha256=${manifest.source_sha256}`,
    `pack.provenance.source.jsonOnDiskSha256Observed=${jsonOnDiskSha256 ?? ''}`,
    `pack.provenance.source.jsonOnDiskMatchesManifest=${jsonMatchesManifest}`,
    `pack.provenance.source.manifest=${path.basename(manifestPath)}`,
    `pack.provenance.export.generatedAtUtc=${meta.get('generated_at_utc') ?? manifest.generated_at_utc}`,
    `pack.provenance.export.interfaceKinds=${JSON.stringify(manifest.interface_kinds ?? {})}`,
    `pack.provenance.export.sourceOutgoingInterfacesFlagged=${manifest.source_outgoing_interfaces_flagged}`,
    `pack.provenance.export.coclassImplementedInterfaces=${manifest.coclass_implemented_interfaces}`,
    `pack.provenance.host.family=illustrator`,
    `pack.provenance.host.version=${host.version}`,
    `pack.provenance.host.versionSource=${host.key ? `inventory_manifest:${host.key}` : 'none'}`,
    'pack.provenance.host.note=the COM type library does not self-report an application version',
    `pack.counts.interfaces=${data.interfaces.length}`,
    `pack.counts.methods=${data.methods.length}`,
    `pack.counts.methodParameters=${data.parameters.length}`,
    `pack.counts.properties=${data.properties.length}`,
    `pack.counts.propertyAccessors=${data.accessors.length}`,
    `pack.counts.accessorParameters=${data.accessorParameters.length}`,
    `pack.counts.parameters=${data.parameters.length + data.accessorParameters.length}`,
    `pack.counts.implementedInterfaces=${data.implemented.length}`,
    `pack.counts.enums=${data.enums.length}`,
    `pack.counts.enumValues=${data.enumValues.length}`,
  ];

  // Build-environment observation block: explicitly NOT provenance. The export
  // revision may predate or postdate the machine that generated this pack.
  if (observedHostVersion || observedHostProduct) {
    header.push(
      `pack.buildEnvironment.illustratorProduct=${observedHostProduct ?? ''}`,
      `pack.buildEnvironment.illustratorProductVersion=${observedHostVersion ?? ''}`,
      'pack.buildEnvironment.authoritative=false',
      'pack.buildEnvironment.note=build-machine observation only; not bound to the inventory export revision',
    );
  } else {
    header.push(
      'pack.buildEnvironment.illustratorProduct=',
      'pack.buildEnvironment.illustratorProductVersion=',
      'pack.buildEnvironment.authoritative=false',
    );
  }

  header.push(`pack.bodyBytes=${bodyBytes}`, `pack.bodySha256=${bodySha256}`);

  return `${header.join('\n')}\n\n${body}`;
}

export function generate(options) {
  const manifest = loadManifest(options.manifest);
  const sqliteExe = resolveSqlite(options.sqlite);

  if (!existsSync(options.db)) {
    fail('database_not_found', `SQLite index not found: ${options.db}`);
  }

  const databaseSha256Before = sha256File(options.db);
  const databaseSizeBefore = statSync(options.db).size;
  const query = openReadOnlyQuery(sqliteExe, options.db);
  const meta = loadMeta(query);

  crossCheckAgainstManifest(meta, manifest);

  const data = extract(query);

  verifyRowCounts(query, {
    interfaces: manifest.interfaces,
    methods: manifest.methods,
    methodParameters: data.parameters.length,
    properties: manifest.properties,
    propertyAccessors: manifest.property_accessors,
    accessorParameters: data.accessorParameters.length,
    parameters: manifest.parameters,
    implementedInterfaces: manifest.coclass_implemented_interfaces,
    enums: manifest.enums,
    enumValues: manifest.enum_values,
  });

  const databaseSha256After = sha256File(options.db);
  if (databaseSha256After !== databaseSha256Before || statSync(options.db).size !== databaseSizeBefore) {
    fail(
      'database_mutated',
      'Refusing to publish: the SQLite index changed while it was being read. '
      + `before=${databaseSha256Before} after=${databaseSha256After}`);
  }

  const jsonOnDiskSha256 = existsSync(options.json) ? sha256File(options.json) : undefined;

  const pack = buildHeader({
    meta,
    manifest,
    data,
    databaseSha256: databaseSha256Before,
    databasePath: options.db,
    manifestPath: options.manifest,
    jsonOnDiskSha256,
    observedHostVersion: options.observedHostVersion,
    observedHostProduct: options.observedHostProduct,
  });

  return {
    pack,
    databaseSha256: databaseSha256Before,
    jsonOnDiskSha256,
    manifest,
    host: declaredHostVersion(manifest),
    counts: {
      interfaces: data.interfaces.length,
      methods: data.methods.length,
      methodParameters: data.parameters.length,
      properties: data.properties.length,
      propertyAccessors: data.accessors.length,
      accessorParameters: data.accessorParameters.length,
      parameters: data.parameters.length + data.accessorParameters.length,
      implementedInterfaces: data.implemented.length,
      enums: data.enums.length,
      enumValues: data.enumValues.length,
    },
  };
}

function main() {
  const options = parseArgs(process.argv.slice(2));

  let result;
  try {
    result = generate(options);
  } catch (error) {
    if (error instanceof PackGenerationError) {
      process.stderr.write(`${JSON.stringify({ ok: false, kind: error.kind, message: error.message }, null, 2)}\n`);
      return 1;
    }
    throw error;
  }

  const expected = Buffer.from(result.pack, 'utf8');
  const drift = result.jsonOnDiskSha256 !== result.manifest.source_sha256;

  if (options.check) {
    if (!existsSync(options.out)) {
      process.stderr.write(`${JSON.stringify({ ok: false, kind: 'pack_missing', message: `Pack not found: ${options.out}` }, null, 2)}\n`);
      return 1;
    }
    const actual = readFileSync(options.out);
    if (!actual.equals(expected)) {
      process.stderr.write(`${JSON.stringify({
        ok: false,
        kind: 'pack_stale',
        message: 'Committed knowledge pack differs from a fresh generation.',
        expectedSha256: createHash('sha256').update(expected).digest('hex'),
        actualSha256: createHash('sha256').update(actual).digest('hex'),
      }, null, 2)}\n`);
      return 1;
    }
    process.stdout.write(`${JSON.stringify({ ok: true, kind: 'pack_current', path: options.out }, null, 2)}\n`);
    return 0;
  }

  mkdirSync(path.dirname(options.out), { recursive: true });
  writeFileSync(options.out, expected);

  process.stdout.write(`${JSON.stringify({
    ok: true,
    kind: 'pack_written',
    path: options.out,
    bytes: expected.length,
    packFormat: PACK_FORMAT,
    packVersion: PACK_VERSION,
    authoritativeSource: {
      database: options.db,
      databaseSha256: result.databaseSha256,
      manifest: options.manifest,
      manifestSourceSha256: result.manifest.source_sha256,
      manifestGeneratedAtUtc: result.manifest.generated_at_utc,
      interfaceKinds: result.manifest.interface_kinds,
    },
    counts: result.counts,
    packProvenance: {
      authority: 'sqlite-index-hash + inventory-manifest',
      databaseSha256: result.databaseSha256,
      manifestSourceSha256: result.manifest.source_sha256,
      manifestGeneratedAtUtc: result.manifest.generated_at_utc,
      hostFamily: 'illustrator',
      hostVersion: result.host.version,
      hostVersionSource: result.host.key
        ? `inventory_manifest:${result.host.key}`
        : 'none',
    },
    sourceDrift: {
      // Reported, never silently absorbed: the on-disk source JSON is being
      // updated by another lane and no longer hashes to the manifest revision.
      jsonPath: options.json,
      jsonOnDiskSha256: result.jsonOnDiskSha256 ?? null,
      manifestSourceSha256: result.manifest.source_sha256,
      jsonOnDiskMatchesManifest: !drift,
      note: drift
        ? 'The on-disk illustrator_com_commands.json is mid-update and does NOT '
          + 'match the manifest revision this pack was generated from. The pack '
          + 'is authoritative for the manifest revision only.'
        : 'On-disk source JSON matches the manifest revision.',
    },
    buildEnvironmentObservation: {
      note: 'Non-authoritative. Not bound to the inventory export revision.',
      illustratorProduct: options.observedHostProduct ?? null,
      illustratorProductVersion: options.observedHostVersion ?? null,
    },
  }, null, 2)}\n`);

  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  process.exit(main());
}
