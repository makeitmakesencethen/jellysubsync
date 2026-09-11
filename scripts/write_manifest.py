#!/usr/bin/env python3
"""Generate the publishable catalog manifest for one release.

Reads the repository's canonical manifest.json (used as the metadata source)
and writes a dist manifest with a single version entry for the freshly built
zip. Metadata lives in the root manifest.json; this script never invents it.

Usage:
    write_manifest.py <manifest.json> <plugin.zip> <checksum> <base-url> <changelog-url> <output.json> <owner>

The checksum argument is the hex digest Jellyfin expects for downloads: MD5
of the zip (InstallationManager uses MD5.HashDataAsync, unchanged in 12.0).
"""

import json
import sys
from pathlib import Path

TARGET_ABI = "12.0.0.0"
OWNER_PLACEHOLDER = "YOUR_GITHUB_USERNAME"


def main() -> int:
    src, zip_path, checksum, base_url, changelog_url, out, owner = sys.argv[1:8]

    with open(src, encoding="utf-8") as f:
        manifest = json.load(f)

    plugin = manifest[0]
    if plugin.get("owner", "").strip() == OWNER_PLACEHOLDER:
        plugin["owner"] = owner

    zip_name = Path(zip_path).name
    version = zip_name.rsplit("_", 1)[1].removesuffix(".zip")

    plugin["versions"] = [
        {
            "version": version,
            "checksum": checksum,
            "targetAbi": TARGET_ABI,
            "sourceUrl": f"{base_url.rstrip('/')}/{zip_name}",
            "changelog": changelog_url,
        }
    ]

    output = Path(out)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
