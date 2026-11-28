#!/usr/bin/env python3
"""Generate a CycloneDX 1.5 SBOM for WinIslands release assets."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

RELEASE_SUFFIXES = {".dmg", ".exe", ".zip"}
PACKAGE_REPORTS = {"nuget-windows.json", "nuget-macos.json"}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_package_components(artifacts_dir: Path) -> list[dict[str, Any]]:
    components: dict[str, dict[str, Any]] = {}

    for report_path in sorted(artifacts_dir.rglob("*")):
        if not report_path.is_file() or report_path.name not in PACKAGE_REPORTS:
            continue

        try:
            report = json.loads(report_path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError) as exc:
            raise SystemExit(f"Invalid NuGet report {report_path}: {exc}") from exc

        for project in report.get("projects", []):
            for framework in project.get("frameworks", []):
                for key, scope in (("topLevelPackages", "required"), ("transitivePackages", "optional")):
                    for package in framework.get(key, []) or []:
                        name = str(package.get("id", "")).strip()
                        version = str(package.get("resolvedVersion", "")).strip()
                        if not name or not version:
                            continue
                        ref = f"pkg:nuget/{name}@{version}"
                        components[ref] = {
                            "type": "library",
                            "bom-ref": ref,
                            "name": name,
                            "version": version,
                            "purl": ref,
                            "scope": scope,
                        }

    return [components[key] for key in sorted(components)]


def collect_file_components(artifacts_dir: Path, version: str, output_path: Path) -> list[dict[str, Any]]:
    components: list[dict[str, Any]] = []
    resolved_output = output_path.resolve()

    for path in sorted(artifacts_dir.iterdir()):
        if not path.is_file() or path.suffix.lower() not in RELEASE_SUFFIXES:
            continue
        if path.resolve() == resolved_output:
            continue
        digest = sha256_file(path)
        components.append(
            {
                "type": "file",
                "bom-ref": f"file:{path.name}@sha256:{digest}",
                "name": path.name,
                "version": version,
                "hashes": [{"alg": "SHA-256", "content": digest}],
            }
        )
    return components


def build_sbom(artifacts_dir: Path, version: str, output_path: Path) -> dict[str, Any]:
    if not artifacts_dir.is_dir():
        raise SystemExit(f"Artifacts directory does not exist: {artifacts_dir}")

    main_ref = f"pkg:generic/WinIslands@{version}"
    components = load_package_components(artifacts_dir)
    components.extend(collect_file_components(artifacts_dir, version, output_path))
    if not components:
        raise SystemExit("No release files were found for the SBOM")

    refs = [component["bom-ref"] for component in components]
    if len(refs) != len(set(refs)):
        raise SystemExit("Duplicate component references were found")

    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": f"urn:uuid:{uuid.uuid4()}",
        "version": 1,
        "metadata": {
            "timestamp": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "tools": {
                "components": [
                    {
                        "type": "application",
                        "name": "WinIslands SBOM Generator",
                        "version": "1.0.0",
                    }
                ]
            },
            "component": {
                "type": "application",
                "bom-ref": main_ref,
                "name": "WinIslands",
                "version": version,
                "purl": main_ref,
            },
        },
        "components": components,
        "dependencies": [{"ref": main_ref, "dependsOn": refs}],
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", required=True, type=Path, help="Directory containing release assets")
    parser.add_argument("--version", required=True, help="Release version, for example 3.1.9")
    parser.add_argument("--output", required=True, type=Path, help="Output CycloneDX JSON file")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    sbom = build_sbom(args.artifacts, args.version, args.output)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(sbom, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {args.output} with {len(sbom['components'])} components")
    return 0


if __name__ == "__main__":
    sys.exit(main())