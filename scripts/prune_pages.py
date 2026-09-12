#!/usr/bin/env python3
"""Prune the published branch: keep the newest beta zips and the stable release's zip, drop the rest.

Why: the gh-pages branch had grown to 74 zips / 3.14 GB, and GitHub's Pages build processes the whole branch on
every push - so a deploy took minutes and every following push cancelled the one in flight, which is how 2.0.18's
files sat published on the branch while the catalogue URL kept serving 2.0.17.

Policy: the newest KEEP_BETA beta zips, plus the zip the stable manifest.json points at. Superseded builds stay in
git history, so nothing is lost; what goes away is only the ability to install an old version by URL.
"""
import pathlib
import re
import sys

KEEP_BETA = 5
ZIP_RE = re.compile(r"Jellyfin\.Plugin\.SubSync_(\d+(?:\.\d+)+)\.zip$")


def version_key(path):
    m = ZIP_RE.search(path.name)
    return tuple(int(p) for p in m.group(1).split(".")) if m else ()


def main():
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    beta = root / "beta"
    if not beta.is_dir():
        raise SystemExit("no beta/ directory in %s" % root)

    # the beta channel: keep the newest KEEP_BETA
    beta_zips = sorted([p for p in beta.glob("*.zip") if ZIP_RE.search(p.name)], key=version_key, reverse=True)
    keep_beta = beta_zips[:KEEP_BETA]
    drop_beta = beta_zips[KEEP_BETA:]

    # the stable channel: keep whatever zip its manifest points at, by name appearing in the manifest
    stable_manifest = root / "manifest.json"
    keep_root = []
    if stable_manifest.is_file():
        text = stable_manifest.read_text()
        keep_root = [p for p in root.glob("*.zip") if p.name in text]
    drop_root = [p for p in root.glob("*.zip") if p not in keep_root]

    freed = sum(p.stat().st_size for p in drop_beta + drop_root)
    print("beta/: keeping %d, dropping %d" % (len(keep_beta), len(drop_beta)))
    for p in keep_beta:
        print("   keep  beta/%s" % p.name)
    for p in drop_beta[:6]:
        print("   drop  beta/%s" % p.name)
    if len(drop_beta) > 6:
        print("   ... and %d more" % (len(drop_beta) - 6))
    print("root: keeping %s, dropping %d" % ([p.name for p in keep_root] or "nothing", len(drop_root)))
    print("freed %.2f GB" % (freed / 1e9))

    for p in drop_beta + drop_root:
        p.unlink()


if __name__ == "__main__":
    main()
