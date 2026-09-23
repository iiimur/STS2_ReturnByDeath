#!/usr/bin/env python3
"""Stage runtime files from an STS2 mod directory into an official uploader workspace."""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path


ROOT_FILES = {".dll", ".json", ".pck", ".manifest", ".cfg", ".ini", ".toml", ".txt"}
ROOT_ASSET_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp"}
ASSET_DIRS = {"assets", "images", "localization", "translations", "viewer", "音效", "视频"}
EXCLUDED_NAMES = {"sts2.dll", "0harmony.dll", "godotsharp.dll"}
EXCLUDED_SUFFIXES = {".cs", ".csproj", ".sln", ".pdb", ".deps.json"}
STATE_FILE_PREFIXES = ("deathless-run.", "return-by-death.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mod-dir", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--title", required=True)
    parser.add_argument("--description", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--game-version", required=True)
    parser.add_argument("--author", default="")
    parser.add_argument("--visibility", choices=["private", "public", "unlisted", "friends_only"], default="private")
    parser.add_argument("--change-note", default="")
    parser.add_argument("--preview", type=Path)
    parser.add_argument("--tag", action="append", default=[])
    parser.add_argument("--clean", action="store_true", help="Remove the existing workspace content directory before staging.")
    return parser.parse_args()


def find_manifest(mod_dir: Path) -> tuple[Path, dict]:
    manifests = []
    for path in sorted(mod_dir.glob("*.json")):
        if path.name == "workshop.json":
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(data, dict) and data.get("id"):
            manifests.append((path, data))
    if len(manifests) != 1:
        raise SystemExit(f"Expected exactly one mod manifest JSON in {mod_dir}, found {len(manifests)}")
    return manifests[0]


def copy_runtime(mod_dir: Path, content_dir: Path) -> None:
    for source in sorted(mod_dir.iterdir()):
        if source.name in {"obj", "bin", ".git"} or source.name.startswith("."):
            continue
        if source.is_dir():
            if source.name not in ASSET_DIRS:
                continue
            shutil.copytree(source, content_dir / source.name, dirs_exist_ok=True)
            continue
        lower = source.name.lower()
        if lower in EXCLUDED_NAMES or lower.endswith(".deps.json") or source.suffix.lower() in EXCLUDED_SUFFIXES:
            continue
        # Mutable per-player state must never enter Workshop content. Current
        # ReturnByDeath versions store it outside the install directory, but
        # this also protects staging from old local state files.
        if source.name.lower().startswith(STATE_FILE_PREFIXES):
            continue
        if source.suffix.lower() in ROOT_FILES:
            shutil.copy2(source, content_dir / source.name)
        elif source.suffix.lower() in ROOT_ASSET_SUFFIXES:
            shutil.copy2(source, content_dir / source.name)


def main() -> None:
    args = parse_args()
    mod_dir = args.mod_dir.resolve()
    workspace = args.workspace.resolve()
    if not mod_dir.is_dir():
        raise SystemExit(f"Mod directory does not exist: {mod_dir}")
    manifest_path, manifest = find_manifest(mod_dir)

    workspace.mkdir(parents=True, exist_ok=True)
    content_dir = workspace / "content"
    if args.clean and content_dir.exists():
        shutil.rmtree(content_dir)
    content_dir.mkdir(parents=True, exist_ok=True)
    copy_runtime(mod_dir, content_dir)

    if args.preview:
        if not args.preview.is_file():
            raise SystemExit(f"Preview image does not exist: {args.preview}")
        if args.preview.stat().st_size >= 1_000_000:
            raise SystemExit("Preview image must be smaller than 1 MB")
        shutil.copy2(args.preview, workspace / "image.png")
    elif not (workspace / "image.png").is_file():
        raise SystemExit("A preview image is required for a new workspace; pass --preview")

    workshop = {
        "title": args.title,
        "description": args.description,
        "visibility": args.visibility,
        "changeNote": args.change_note,
        "tags": args.tag,
        "dependencies": manifest.get("dependencies", []),
        "contentDescriptors": [],
    }
    (workspace / "workshop.json").write_text(json.dumps(workshop, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Staged {manifest.get('id')} {args.version} for game {args.game_version}")
    print(f"Manifest: {manifest_path.name}")
    print(f"Workspace: {workspace}")


if __name__ == "__main__":
    main()
