#!/usr/bin/env python3
"""Stage and verify the native portable release using only the Python standard library."""

import argparse
import hashlib
from pathlib import Path
import re
import shutil
import struct
import zipfile

APP_DIR = Path(__file__).resolve().parents[1]
REPO_DIR = APP_DIR.parent
PAYLOAD = (
    "Vex.exe",
    "vex-native.exe",
    "vex-update.exe",
    "ghostty-vt.dll",
    "conpty.dll",
    "OpenConsole.exe",
    "app.ico",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
)


def release_version(path=APP_DIR / "VERSION"):
    version = path.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise ValueError(f"Invalid release version: {version!r}")
    if any(
        value > limit
        for value, limit in zip(map(int, version.split(".")), (255, 255, 65535))
    ):
        raise ValueError(f"Version exceeds Windows Installer limits: {version}")
    return version


def artifact_name(version, label=""):
    if label and not re.fullmatch(r"preview-[0-9a-f]{7,40}", label):
        raise ValueError("Only preview-<commit SHA> labels are supported")
    suffix = f"-{label}" if label else ""
    return f"Vex-Native-{version}{suffix}-win-x64"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_checksum(path):
    path.with_suffix(path.suffix + ".sha256").write_text(
        f"{sha256(path)}  {path.name}\n", encoding="ascii"
    )


def verify_checksum(path):
    expected = f"{sha256(path)}  {path.name}\n"
    if (
        path.with_suffix(path.suffix + ".sha256").read_text(encoding="ascii")
        != expected
    ):
        raise ValueError(f"Checksum mismatch: {path}")


def verify_pe(path):
    data = path.read_bytes()
    if len(data) < 64 or data[:2] != b"MZ":
        raise ValueError(f"Not a Windows executable: {path}")
    offset = struct.unpack_from("<I", data, 60)[0]
    if offset + 6 > len(data) or data[offset : offset + 4] != b"PE\0\0":
        raise ValueError(f"Invalid PE header: {path}")
    if struct.unpack_from("<H", data, offset + 4)[0] != 0x8664:
        raise ValueError(f"Not a Windows x64 executable: {path}")


def package(bin_dir, output_dir, version, label=""):
    bin_dir, output_dir = bin_dir.resolve(), output_dir.resolve()
    app_dir = output_dir / "app"
    archive_path = output_dir / (artifact_name(version, label) + ".zip")
    if bin_dir.is_relative_to(output_dir) or output_dir.is_relative_to(bin_dir):
        raise ValueError("Build output and publish directories must not overlap")
    sources = {}
    for name in PAYLOAD:
        source_name = "vex-native.exe" if name == "Vex.exe" else name
        source = (
            REPO_DIR / name
            if name in ("LICENSE", "THIRD-PARTY-NOTICES.md")
            else bin_dir / source_name
        )
        if not source.is_file():
            raise FileNotFoundError(f"Missing release payload: {source}")
        if source.suffix in (".exe", ".dll"):
            verify_pe(source)
        sources[name] = source

    # Validate every source before replacing staging; never harvest arbitrary build output or symbols.
    output_dir.mkdir(parents=True, exist_ok=True)
    if app_dir.exists():
        shutil.rmtree(app_dir)
    for name, source in sources.items():
        destination = app_dir / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)

    with zipfile.ZipFile(
        archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
    ) as archive:
        for name in sorted(PAYLOAD):
            # Stable ZIP metadata keeps checksums independent of host, source mtimes and executable permissions.
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, (app_dir / name).read_bytes())
    write_checksum(archive_path)
    verify(output_dir, version, label)
    return archive_path


def verify(output_dir, version, label="", require_msi=False):
    archive_path = output_dir / (artifact_name(version, label) + ".zip")
    verify_checksum(archive_path)
    with zipfile.ZipFile(archive_path) as archive:
        if sorted(archive.namelist()) != sorted(PAYLOAD):
            raise ValueError(
                "ZIP payload is incomplete, duplicated or contains unexpected files"
            )
        if archive.testzip() is not None:
            raise ValueError("ZIP CRC validation failed")
        for name in PAYLOAD:
            if archive.read(name) != (output_dir / "app" / name).read_bytes():
                raise ValueError(f"ZIP differs from staged payload: {name}")
    if require_msi:
        msi = archive_path.with_suffix(".msi")
        if msi.stat().st_size < 1024 * 1024:
            raise ValueError("MSI is unexpectedly small; check WiX harvesting")
        verify_checksum(msi)


def write_update_manifest(output_dir, version):
    """Publish an update feed only after both stable release artifacts have verified."""
    msi = output_dir / (artifact_name(version) + ".msi")
    if msi.stat().st_size > 512 * 1024 * 1024:
        raise ValueError("MSI exceeds the update size limit (512 MiB)")
    verify(output_dir, version, require_msi=True)
    manifest = output_dir / "vex-update.txt"
    manifest.write_text(
        f"VEX-UPDATE-1\n{version}\n{msi.stat().st_size}\n{sha256(msi)}\n",
        encoding="ascii",
        newline="\n",
    )
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bin-dir", type=Path, default=APP_DIR / "zig-out/bin")
    parser.add_argument("--output-dir", type=Path, default=APP_DIR / "publish")
    parser.add_argument("--version", help="Expected VERSION, never an override")
    parser.add_argument("--label", default="")
    parser.add_argument("--verify", action="store_true")
    parser.add_argument("--require-msi", action="store_true")
    parser.add_argument("--update-manifest", action="store_true")
    args = parser.parse_args()
    version = release_version()
    if args.version and args.version != version:
        parser.error(f"Expected {args.version}, but app/VERSION is {version}")
    if args.require_msi and not args.verify:
        parser.error("--require-msi requires --verify")
    if args.update_manifest:
        if args.label or not args.verify or not args.require_msi:
            parser.error("--update-manifest requires --verify --require-msi and no preview label")
        print(write_update_manifest(args.output_dir, version))
    elif args.verify:
        verify(args.output_dir, version, args.label, args.require_msi)
        print("Release payload and checksums verified")
    else:
        print(package(args.bin_dir, args.output_dir, version, args.label))


if __name__ == "__main__":
    main()
