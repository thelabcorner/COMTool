import { createRequire } from "node:module";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const require = createRequire(import.meta.url);
const MAX_RESPONSE_UTF8_BYTES = 4 * 1024 * 1024;
const MAX_EVENTS_PER_COMMAND = 256;
const PUMP_IDLE_SLEEP_MS = 5;
const pumpSleep = new Int32Array(new SharedArrayBuffer(4));
const addonPath = process.env.COMTOOL_ESD_ADDON_PATH
  ? resolve(process.env.COMTOOL_ESD_ADDON_PATH)
  : null;
const expectedAddonSha256 = (process.env.COMTOOL_ESD_ADDON_SHA256 || "")
  .trim()
  .toLowerCase();

function emit(value) {
  process.stdout.write(JSON.stringify(value) + "\n");
}

function fail(error, phase = "before_send") {
  emit({
    ok: false,
    phase,
    error: String(error && error.message ? error.message : error)
  });
}

if (!addonPath || !existsSync(addonPath)) {
  fail("COMTOOL_ESD_ADDON_PATH must point to an existing esdcorelibinterface.node.");
  process.exit(2);
}

if (!/^[0-9a-f]{64}$/.test(expectedAddonSha256)) {
  fail(
    "COMTOOL_ESD_ADDON_SHA256 must be a 64-character lowercase hex SHA-256 digest pinned by the runtime; refusing to load an unpinned debugger addon."
  );
  process.exit(2);
}

// The worker pins exact debugger bytes. Re-hash immediately before require()
// so a swapped, patched, or replaced addon fails closed instead of loading
// different debugger code than the provenance the runtime reported.
const actualAddonSha256 = createHash("sha256")
  .update(readFileSync(addonPath))
  .digest("hex");
if (actualAddonSha256 !== expectedAddonSha256) {
  fail(
    `Debugger addon digest mismatch: expected ${expectedAddonSha256}, found ${actualAddonSha256}. Refusing to load debugger bytes that were not pinned by the runtime.`
  );
  process.exit(2);
}

let addon;
try {
  addon = require(addonPath);
} catch (error) {
  fail(error);
  process.exit(2);
}

function chooseAppSpecifier(explicitSpecifier) {
  if (explicitSpecifier) return explicitSpecifier;

  const installed = addon.esdGetInstalledApplicationSpecifiers();
  const values = Array.isArray(installed)
    ? installed
    : (Array.isArray(installed?.specifiers) ? installed.specifiers : []);
  const running = values.filter((value) => {
    if (typeof value !== "string" || !/^illustrator-/i.test(value)) return false;
    try {
      const state = addon.esdGetApplicationRunning(value);
      if (state && typeof state === "object") {
        return state.status === 0 && state.isRunning === true;
      }
      return Boolean(state);
    } catch {
      return false;
    }
  });

  if (running.length !== 1) {
    throw new Error(
      `Expected exactly one running Illustrator ESD application specifier, found ${running.length}: ${running.join(", ")}`
    );
  }

  return running[0];
}

let appSpec;
try {
  addon.esdInitialize("comtoolv2", process.pid, () => 0);
  appSpec = chooseAppSpecifier(process.argv[2] || null);
} catch (error) {
  try {
    addon.esdCleanup();
  } catch {
  }
  fail(error);
  process.exit(2);
}

let cleaned = false;
function cleanup() {
  if (cleaned) return;
  cleaned = true;
  try {
    addon.esdCleanup();
  } catch {
    // Cleanup is best-effort during process teardown. The parent reports
    // whether graceful child exit was observed.
  }
}

function validateInteger(value, name, min, max, fallback) {
  if (value === undefined || value === null) return fallback;
  if (!Number.isInteger(value) || value < min || value > max) {
    throw new Error(`${name} must be an integer in [${min}, ${max}].`);
  }
  return value;
}

function pumpUntil(maxMs, graceMs) {
  const events = [];
  const start = Date.now();
  let firstEventAt = null;
  let bodyUtf8Bytes = 0;

  for (;;) {
    addon.esdPumpSession((reason, msg) => {
      const body = typeof msg.body === "string" ? msg.body : "";
      bodyUtf8Bytes += Buffer.byteLength(body, "utf8");
      if (
        events.length >= MAX_EVENTS_PER_COMMAND ||
        bodyUtf8Bytes > MAX_RESPONSE_UTF8_BYTES
      ) {
        throw new Error(
          "Debugger response exceeded bounded event/body limits."
        );
      }
      const tagMatch = /^\s*<([A-Za-z0-9_-]+)/.exec(body);
      events.push({
        reason,
        target: msg.target,
        sender: msg.sender,
        type: msg.type,
        serialNumber: msg.serialNumber,
        receivedSerial: msg.receivedSerial,
        resultSerial: msg.resultSerial,
        errorSerial: msg.errorSerial,
        bodyTag: tagMatch ? tagMatch[1] : null,
        body
      });
      if (firstEventAt === null) firstEventAt = Date.now();
      return false;
    });

    const now = Date.now();
    if (firstEventAt !== null && now - firstEventAt >= graceMs) break;
    if (now - start >= maxMs) break;
    // esdPumpSession is non-blocking. Without this bounded wait the bridge
    // would spin a core for the whole pump window on every command. After the
    // first event the grace window drains without sleeping so the caller is
    // never charged the idle interval.
    if (firstEventAt === null) Atomics.wait(pumpSleep, 0, 0, PUMP_IDLE_SLEEP_MS);
  }

  return events;
}

function handleLine(line) {
  let request;
  try {
    request = JSON.parse(line);
    if (!request || typeof request !== "object" || Array.isArray(request)) {
      throw new Error("request must be a JSON object.");
    }
    if (typeof request.body !== "string" || request.body.length === 0) {
      throw new Error("body must be a non-empty XML command string.");
    }
    if (Buffer.byteLength(request.body, "utf8") > 1024 * 1024) {
      throw new Error("body exceeds the 1 MiB UTF-8 transport limit.");
    }
  } catch (error) {
    fail(error);
    return;
  }

  let sendAttempted = false;
  try {
    const timeoutMs = validateInteger(request.timeoutMs, "timeoutMs", 100, 600000, 5000);
    const pumpMs = validateInteger(
      request.pumpMs,
      "pumpMs",
      100,
      600000,
      Math.max(timeoutMs, 5000)
    );
    const graceMs = validateInteger(request.graceMs, "graceMs", 0, 1000, 150);

    sendAttempted = true;
    const sent = addon.esdSendDebugMessage(appSpec, request.body, false, timeoutMs);
    const events = pumpUntil(pumpMs, graceMs);
    const response = { ok: true, appSpec, sent, events };
    const encoded = JSON.stringify(response);
    if (Buffer.byteLength(encoded, "utf8") > MAX_RESPONSE_UTF8_BYTES) {
      throw new Error(
        "Debugger response exceeded the 4 MiB serialized response limit."
      );
    }
    process.stdout.write(encoded + "\n");
  } catch (error) {
    fail(error, sendAttempted ? "send_attempted" : "before_send");
  }
}

let buffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  buffer += chunk;
  let index;
  while ((index = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, index);
    buffer = buffer.slice(index + 1);
    if (line.trim()) handleLine(line);
  }
});

process.stdin.on("end", () => {
  cleanup();
  process.exit(0);
});

process.on("exit", cleanup);

emit({
  ok: true,
  ready: true,
  appSpec,
  addonPath
});
