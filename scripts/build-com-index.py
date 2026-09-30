#!/usr/bin/env python3
"""Build a compact SQLite query index from illustrator_com_commands.json."""
from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

SCHEMA = """
PRAGMA journal_mode=OFF;
PRAGMA synchronous=OFF;
PRAGMA temp_store=MEMORY;

DROP TABLE IF EXISTS meta;
DROP TABLE IF EXISTS interfaces;
DROP TABLE IF EXISTS methods;
DROP TABLE IF EXISTS parameters;
DROP TABLE IF EXISTS properties;
DROP TABLE IF EXISTS accessors;
DROP TABLE IF EXISTS accessor_parameters;
DROP TABLE IF EXISTS implemented_interfaces;
DROP TABLE IF EXISTS enums;
DROP TABLE IF EXISTS enum_values;
DROP TABLE IF EXISTS symbols;
DROP TABLE IF EXISTS symbols_fts;

CREATE TABLE meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE interfaces (
  name TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  guid TEXT,
  flags TEXT
);
CREATE TABLE methods (
  id INTEGER PRIMARY KEY,
  interface_name TEXT NOT NULL,
  name TEXT NOT NULL,
  dispid INTEGER,
  invoke_kind TEXT,
  function_kind TEXT,
  return_type TEXT,
  optional_parameter_count INTEGER,
  flags TEXT,
  FOREIGN KEY(interface_name) REFERENCES interfaces(name)
);
CREATE INDEX methods_name_idx ON methods(name COLLATE NOCASE);
CREATE INDEX methods_interface_idx ON methods(interface_name);

CREATE TABLE parameters (
  id INTEGER PRIMARY KEY,
  method_id INTEGER NOT NULL,
  position INTEGER NOT NULL,
  name TEXT,
  type TEXT,
  flags TEXT,
  FOREIGN KEY(method_id) REFERENCES methods(id)
);
CREATE INDEX parameters_method_idx ON parameters(method_id, position);

CREATE TABLE properties (
  id INTEGER PRIMARY KEY,
  interface_name TEXT NOT NULL,
  name TEXT NOT NULL,
  dispid INTEGER,
  FOREIGN KEY(interface_name) REFERENCES interfaces(name)
);
CREATE INDEX properties_name_idx ON properties(name COLLATE NOCASE);
CREATE INDEX properties_interface_idx ON properties(interface_name);

CREATE TABLE accessors (
  id INTEGER PRIMARY KEY,
  property_id INTEGER NOT NULL,
  name TEXT,
  dispid INTEGER,
  invoke_kind TEXT,
  function_kind TEXT,
  return_type TEXT,
  optional_parameter_count INTEGER,
  flags TEXT,
  FOREIGN KEY(property_id) REFERENCES properties(id)
);
CREATE INDEX accessors_property_idx ON accessors(property_id);

CREATE TABLE accessor_parameters (
  id INTEGER PRIMARY KEY,
  accessor_id INTEGER NOT NULL,
  position INTEGER NOT NULL,
  name TEXT,
  type TEXT,
  flags TEXT,
  FOREIGN KEY(accessor_id) REFERENCES accessors(id)
);
CREATE INDEX accessor_parameters_idx ON accessor_parameters(accessor_id, position);

CREATE TABLE implemented_interfaces (
  coclass_name TEXT NOT NULL,
  interface_name TEXT NOT NULL,
  flags TEXT,
  PRIMARY KEY(coclass_name, interface_name)
);
CREATE INDEX implemented_interface_idx ON implemented_interfaces(interface_name);

CREATE TABLE enums (
  name TEXT PRIMARY KEY
);
CREATE TABLE enum_values (
  enum_name TEXT NOT NULL,
  name TEXT NOT NULL,
  value INTEGER,
  PRIMARY KEY(enum_name, name),
  FOREIGN KEY(enum_name) REFERENCES enums(name)
);
CREATE INDEX enum_values_name_idx ON enum_values(name COLLATE NOCASE);

CREATE TABLE symbols (
  id INTEGER PRIMARY KEY,
  symbol TEXT NOT NULL,
  category TEXT NOT NULL,
  owner TEXT,
  details TEXT
);
CREATE INDEX symbols_symbol_idx ON symbols(symbol COLLATE NOCASE);
CREATE INDEX symbols_owner_idx ON symbols(owner COLLATE NOCASE);
CREATE VIRTUAL TABLE symbols_fts USING fts5(
  symbol,
  category,
  owner,
  details,
  content='symbols',
  content_rowid='id',
  tokenize='unicode61 remove_diacritics 2'
);
"""


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def insert_symbol(cur: sqlite3.Cursor, symbol: str, category: str, owner: str | None = None, details: str | None = None) -> None:
    cur.execute(
        "INSERT INTO symbols(symbol, category, owner, details) VALUES (?, ?, ?, ?)",
        (symbol, category, owner, details),
    )


def build(
    source: Path,
    output: Path,
    manifest_path: Path | None = None,
    generated_at_utc: str | None = None,
) -> dict[str, Any]:
    data = json.loads(source.read_text(encoding="utf-8"))
    interfaces: dict[str, dict[str, Any]] = data.get("interfaces", {})
    enums: dict[str, list[dict[str, Any]]] = data.get("enums", {})

    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        output.unlink()
    con = sqlite3.connect(output)
    try:
        cur = con.cursor()
        cur.executescript(SCHEMA)

        method_count = property_count = accessor_count = parameter_count = 0
        source_interface_count = 0

        for interface_name, obj in interfaces.items():
            cur.execute(
                "INSERT INTO interfaces(name, kind, guid, flags) VALUES (?, ?, ?, ?)",
                (interface_name, obj.get("kind", ""), obj.get("guid"), obj.get("flags")),
            )
            insert_symbol(cur, interface_name, obj.get("kind", "interface").lower(), None, obj.get("guid"))

            for impl in obj.get("implementedInterfaces", []) or []:
                flags = impl.get("flags")
                if flags and "FSOURCE" in flags.upper():
                    source_interface_count += 1
                cur.execute(
                    "INSERT OR REPLACE INTO implemented_interfaces(coclass_name, interface_name, flags) VALUES (?, ?, ?)",
                    (interface_name, impl.get("name"), flags),
                )

            for method in obj.get("methods", []) or []:
                method_count += 1
                cur.execute(
                    """INSERT INTO methods(interface_name, name, dispid, invoke_kind, function_kind,
                       return_type, optional_parameter_count, flags) VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                    (
                        interface_name,
                        method.get("name"),
                        method.get("dispid"),
                        method.get("invokeKind"),
                        method.get("functionKind"),
                        method.get("returnType"),
                        method.get("optionalParameterCount", 0),
                        method.get("flags"),
                    ),
                )
                method_id = cur.lastrowid
                details = f"{method.get('returnType', 'VT_VOID')} {interface_name}.{method.get('name')}"
                insert_symbol(cur, method.get("name", ""), "method", interface_name, details)
                for pos, param in enumerate(method.get("parameters", []) or []):
                    parameter_count += 1
                    cur.execute(
                        "INSERT INTO parameters(method_id, position, name, type, flags) VALUES (?, ?, ?, ?, ?)",
                        (method_id, pos, param.get("name"), param.get("type"), param.get("flags")),
                    )

            for prop in obj.get("properties", []) or []:
                property_count += 1
                cur.execute(
                    "INSERT INTO properties(interface_name, name, dispid) VALUES (?, ?, ?)",
                    (interface_name, prop.get("name"), prop.get("dispid")),
                )
                prop_id = cur.lastrowid
                access_kinds: list[str] = []
                return_types: list[str] = []
                for accessor in prop.get("accessors", []) or []:
                    accessor_count += 1
                    access_kinds.append(accessor.get("invokeKind", ""))
                    if accessor.get("returnType") and accessor.get("returnType") != "VT_VOID":
                        return_types.append(accessor.get("returnType"))
                    cur.execute(
                        """INSERT INTO accessors(property_id, name, dispid, invoke_kind, function_kind,
                           return_type, optional_parameter_count, flags) VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                        (
                            prop_id,
                            accessor.get("name"),
                            accessor.get("dispid"),
                            accessor.get("invokeKind"),
                            accessor.get("functionKind"),
                            accessor.get("returnType"),
                            accessor.get("optionalParameterCount", 0),
                            accessor.get("flags"),
                        ),
                    )
                    accessor_id = cur.lastrowid
                    for pos, param in enumerate(accessor.get("parameters", []) or []):
                        parameter_count += 1
                        cur.execute(
                            "INSERT INTO accessor_parameters(accessor_id, position, name, type, flags) VALUES (?, ?, ?, ?, ?)",
                            (accessor_id, pos, param.get("name"), param.get("type"), param.get("flags")),
                        )
                details = ", ".join(dict.fromkeys(return_types + access_kinds))
                insert_symbol(cur, prop.get("name", ""), "property", interface_name, details)

        enum_value_count = 0
        for enum_name, values in enums.items():
            cur.execute("INSERT INTO enums(name) VALUES (?)", (enum_name,))
            insert_symbol(cur, enum_name, "enum", None, None)
            for item in values:
                enum_value_count += 1
                cur.execute(
                    "INSERT INTO enum_values(enum_name, name, value) VALUES (?, ?, ?)",
                    (enum_name, item.get("name"), item.get("value")),
                )
                insert_symbol(cur, item.get("name", ""), "enum_value", enum_name, str(item.get("value")))

        cur.execute("INSERT INTO symbols_fts(symbols_fts) VALUES ('rebuild')")

        kinds: dict[str, int] = {}
        for obj in interfaces.values():
            kinds[obj.get("kind", "UNKNOWN")] = kinds.get(obj.get("kind", "UNKNOWN"), 0) + 1

        manifest = {
            "source_file": source.name,
            "source_sha256": sha256_file(source),
            "generated_at_utc": generated_at_utc or datetime.now(timezone.utc).isoformat(),
            "interfaces": len(interfaces),
            "interface_kinds": kinds,
            "methods": method_count,
            "properties": property_count,
            "property_accessors": accessor_count,
            "parameters": parameter_count,
            "coclass_implemented_interfaces": sum(len((v.get("implementedInterfaces") or [])) for v in interfaces.values()),
            "source_outgoing_interfaces_flagged": source_interface_count,
            "enums": len(enums),
            "enum_values": enum_value_count,
            "sqlite_file": output.name,
        }
        for key, value in manifest.items():
            cur.execute("INSERT INTO meta(key, value) VALUES (?, ?)", (key, json.dumps(value)))
        con.commit()
    finally:
        con.close()

    if manifest_path:
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def main() -> int:
    here = Path(__file__).resolve().parent
    root = here.parent
    data_root = root / "data" / "knowledge-source"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", nargs="?", type=Path, default=data_root / "illustrator_com_commands.json")
    parser.add_argument("--output", type=Path, default=data_root / "illustrator_com.sqlite")
    parser.add_argument("--manifest", type=Path, default=data_root / "inventory_manifest.json")
    parser.add_argument(
        "--generated-at-utc",
        help="explicit provenance timestamp for deterministic rebuilds",
    )
    args = parser.parse_args()
    manifest = build(
        args.source,
        args.output,
        args.manifest,
        generated_at_utc=args.generated_at_utc,
    )
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
