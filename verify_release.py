#!/usr/bin/env python3
"""Verify prepared release assets without loading or executing Windows binaries."""
import argparse
import hashlib
import json
import re
import stat
import subprocess
import sys
import zipfile
from pathlib import Path

PACKAGE = "AktiOplaty-update.zip"
MANIFEST = "update-manifest.json"
SIGNATURE = "update-manifest.sig"
BOOTSTRAP = "AktiOplaty-Windows.zip"
EXECUTABLES = {"AktiOplaty.exe", "Updater.exe"}
REQUIRED_ASSETS = {PACKAGE, MANIFEST, SIGNATURE}
MAX_PACKAGE = 25 * 1024 * 1024
MAX_UNCOMPRESSED = 100 * 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON property: " + key)
        result[key] = value
    return result


def read_json(path, maximum):
    require(path.is_file() and not path.is_symlink(), "Expected a regular JSON file: " + path.name)
    require(0 < path.stat().st_size <= maximum, "JSON file size is invalid: " + path.name)
    raw = path.read_bytes()
    require(not raw.startswith(b"\xef\xbb\xbf"), "The signed manifest must not contain a UTF-8 BOM")
    return json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def valid_hash(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-fA-F]{64}", value) is not None


def positive_size(value, maximum):
    return type(value) is int and 0 < value <= maximum


def verify_signature(directory, key, openssl):
    require(key.is_file() and not key.is_symlink() and key.stat().st_size <= 16384, "Pinned public key is missing")
    pem = key.read_text(encoding="ascii").strip()
    require(pem.startswith("-----BEGIN PUBLIC KEY-----") and pem.endswith("-----END PUBLIC KEY-----") and "PRIVATE KEY" not in pem,
            "Expected a public key in PEM format")
    try:
        result = subprocess.run(
            [openssl, "dgst", "-sha256", "-verify", str(key.resolve()), "-signature", str((directory / SIGNATURE).resolve()), str((directory / MANIFEST).resolve())],
            capture_output=True, text=True, timeout=30, check=False,
        )
    except FileNotFoundError as exc:
        raise ValueError("OpenSSL is required to verify the release signature") from exc
    require(result.returncode == 0, "The release signature does not match the pinned public key")


def verify_zip(path, expected, bootstrap=False):
    required_names = set(expected) | ({"Инструкция.txt"} if bootstrap else set())
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        require(len(entries) == len(required_names) and {entry.filename for entry in entries} == required_names,
                "Unexpected ZIP contents: " + path.name)
        total = 0
        for entry in entries:
            require(not entry.is_dir() and not (entry.flag_bits & 1) and not stat.S_ISLNK(entry.external_attr >> 16)
                    and not (entry.external_attr & 0x400), "ZIP contains a directory, link, encrypted file or reparse point")
            if entry.filename == "Инструкция.txt":
                require(0 < entry.file_size <= 256 * 1024, "Bootstrap instructions have an invalid size")
                archive.read(entry)
                continue
            metadata = expected[entry.filename]
            require(entry.file_size == metadata["Size"], "ZIP file size does not match: " + entry.filename)
            total += entry.file_size
            require(total <= MAX_UNCOMPRESSED, "Uncompressed release is too large")
            actual = hashlib.sha256()
            written = 0
            with archive.open(entry) as source:
                while True:
                    chunk = source.read(65536)
                    if not chunk:
                        break
                    written += len(chunk)
                    require(written <= metadata["Size"], "ZIP entry exceeds its signed size")
                    actual.update(chunk)
            require(written == metadata["Size"] and actual.hexdigest() == metadata["Sha256"].lower(),
                    "ZIP file hash does not match: " + entry.filename)


def verify_assets(directory, key, openssl):
    directory = directory.resolve()
    require(directory.is_dir(), "Release assets directory is missing")
    members = list(directory.iterdir())
    names = {member.name for member in members}
    require(REQUIRED_ASSETS <= names and names <= REQUIRED_ASSETS | {BOOTSTRAP}, "Release assets must contain the three signed files and optional bootstrap ZIP only")
    require(all(member.is_file() and not member.is_symlink() for member in members), "Release assets must be regular files")
    require(0 < (directory / MANIFEST).stat().st_size <= 65536 and 0 < (directory / SIGNATURE).stat().st_size <= 1024,
            "Manifest or signature size is invalid")
    verify_signature(directory, key, openssl)
    manifest = read_json(directory / MANIFEST, 65536)
    require(isinstance(manifest, dict) and set(manifest) == {"FormatVersion", "Version", "MinDataVersion", "MaxDataVersion", "PackageFile", "PackageSha256", "PackageSize", "Files"}, "Unexpected manifest fields")
    require(type(manifest["FormatVersion"]) is int and manifest["FormatVersion"] == 1, "Unsupported manifest format")
    version = manifest["Version"]
    require(isinstance(version, str) and re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version), "Version must contain four numeric components")
    components = [int(part) for part in version.split(".")]
    require(all(part <= 65535 for part in components), "Version component exceeds the Windows assembly limit")
    minimum, maximum = manifest["MinDataVersion"], manifest["MaxDataVersion"]
    require(type(minimum) is int and type(maximum) is int and 1 <= minimum <= maximum, "Invalid database compatibility range")
    require(manifest["PackageFile"] == PACKAGE and positive_size(manifest["PackageSize"], MAX_PACKAGE) and valid_hash(manifest["PackageSha256"]), "Invalid package metadata")
    package = directory / PACKAGE
    require(package.stat().st_size == manifest["PackageSize"] and digest(package) == manifest["PackageSha256"].lower(), "Package hash or size does not match")
    files = manifest["Files"]
    require(isinstance(files, list) and len(files) == 2, "Manifest must contain exactly two executable files")
    expected = {}
    for item in files:
        require(isinstance(item, dict) and set(item) == {"Name", "Size", "Sha256"} and item["Name"] in EXECUTABLES and item["Name"] not in expected,
                "Unexpected or repeated executable metadata")
        require(positive_size(item["Size"], MAX_UNCOMPRESSED) and valid_hash(item["Sha256"]), "Invalid executable size or hash")
        expected[item["Name"]] = item
    require(set(expected) == EXECUTABLES and sum(item["Size"] for item in expected.values()) <= MAX_UNCOMPRESSED, "Executable list or total size is invalid")
    verify_zip(package, expected)
    if BOOTSTRAP in names:
        require(positive_size((directory / BOOTSTRAP).stat().st_size, MAX_PACKAGE), "Bootstrap ZIP is too large")
        verify_zip(directory / BOOTSTRAP, expected, bootstrap=True)
    label = ".".join(version.split(".")[:3]) if components[3] == 0 else version
    return {
        "version": version, "display_version": label, "tag": "v" + label,
        "asset_names": sorted(names),
        "asset_sizes": {name: (directory / name).stat().st_size for name in sorted(names)},
        "asset_sha256": {name: digest(directory / name) for name in sorted(names)},
    }


def verify_release_info(path, report):
    release = read_json(path, 1024 * 1024)
    require(isinstance(release, dict) and release.get("tagName") == report["tag"], "Existing release tag differs")
    require(release.get("isDraft") is False and release.get("isPrerelease") is False, "Existing release must already be published and stable")
    assets = release.get("assets")
    require(isinstance(assets, list) and len(assets) == len(report["asset_names"]), "Existing release has a different asset count")
    names = [item.get("name") for item in assets if isinstance(item, dict)]
    require(len(names) == len(assets) and len(set(names)) == len(names) and set(names) == set(report["asset_names"]), "Existing release has different asset names")
    for item in assets:
        require(type(item.get("size")) is int and item["size"] == report["asset_sizes"][item["name"]], "Existing release asset size differs: " + item["name"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets-dir", type=Path, default=Path("release-assets"))
    parser.add_argument("--key", type=Path, default=Path(__file__).resolve().parent / "update-public-key.pem")
    parser.add_argument("--openssl", default="openssl")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--github-output", type=Path)
    parser.add_argument("--release-info", type=Path)
    parser.add_argument("--compare-with", type=Path)
    args = parser.parse_args()
    report = verify_assets(args.assets_dir, args.key, args.openssl)
    if args.release_info:
        verify_release_info(args.release_info, report)
    if args.compare_with:
        original = verify_assets(args.compare_with, args.key, args.openssl)
        require(report == original, "Existing release files differ; they will not be overwritten")
    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8") as output:
            output.write("version=" + report["version"] + "\n")
            output.write("display_version=" + report["display_version"] + "\n")
            output.write("tag=" + report["tag"] + "\n")
            output.write("has_bootstrap=" + str(BOOTSTRAP in report["asset_names"]).lower() + "\n")
    print("Verified signed release " + report["version"] + " (" + report["tag"] + "): " + ", ".join(report["asset_names"]))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.TimeoutExpired, zipfile.BadZipFile, json.JSONDecodeError) as error:
        print("Release verification failed: " + str(error), file=sys.stderr)
        sys.exit(1)
