"""Headless release-payload regressions; run on Linux and Windows, without WiX."""

import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location("vex_package", Path(__file__).parents[1] / "tools/package.py")
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.output = self.root / "publish"
        pe = bytearray(256)
        pe[:2] = b"MZ"
        struct.pack_into("<I", pe, 60, 128)
        pe[128:132] = b"PE\0\0"
        struct.pack_into("<H", pe, 132, 0x8664)
        for name in ("vex-native.exe", "ghostty-vt.dll", "conpty.dll", "OpenConsole.exe", "app.ico"):
            path = self.bin / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(pe if path.suffix in (".exe", ".dll") else b"fixture\n")

    def test_payload_includes_licenses_but_not_external_integration_tests_or_symbols(self):
        (self.bin / "vex-native-tests.exe").write_bytes(b"test binary")
        (self.bin / "vex-conpty-fixture.exe").write_bytes(b"test fixture")
        (self.bin / "vex-native.pdb").write_bytes(b"symbols")
        path = package.package(self.bin, self.output, "1.7.0")
        with zipfile.ZipFile(path) as archive:
            self.assertEqual(sorted(archive.namelist()), sorted(package.PAYLOAD))
            self.assertEqual(archive.read("Vex.exe"), archive.read("vex-native.exe"))
            self.assertFalse(any(name.startswith("ShellIntegration/") for name in archive.namelist()))
        package.verify_checksum(path)
        package.verify(self.output, "1.7.0")

    def test_archive_is_repeatable_and_preview_does_not_use_stable_filename(self):
        path = package.package(self.bin, self.output, "1.7.0", "preview-abcdef0")
        expected = path.read_bytes()
        (self.output / "app/stale.pdb").write_bytes(b"stale")
        package.package(self.bin, self.output, "1.7.0", "preview-abcdef0")
        self.assertEqual(path.read_bytes(), expected)
        self.assertFalse((self.output / "app/stale.pdb").exists())
        self.assertEqual(path.name, "Vex-Native-1.7.0-preview-abcdef0-win-x64.zip")

    def test_missing_icon_fails_before_replacing_staging(self):
        package.package(self.bin, self.output, "1.7.0")
        (self.bin / "app.ico").unlink()
        with self.assertRaises(FileNotFoundError):
            package.package(self.bin, self.output, "1.7.0")
        self.assertTrue((self.output / "app/Vex.exe").is_file())

    def test_bundled_conpty_pair_is_required_before_replacing_staging(self):
        for name in ("conpty.dll", "OpenConsole.exe"):
            with self.subTest(name=name):
                package.package(self.bin, self.output, "1.7.0")
                path = self.bin / name
                original = path.read_bytes()
                path.unlink()
                with self.assertRaises(FileNotFoundError):
                    package.package(self.bin, self.output, "1.7.0")
                self.assertTrue((self.output / "app/Vex.exe").is_file())
                path.write_bytes(original)

    def test_wrong_architecture_and_non_pe_payload_are_rejected(self):
        exe = self.bin / "vex-native.exe"
        data = bytearray(exe.read_bytes())
        struct.pack_into("<H", data, 132, 0xAA64)
        exe.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "x64"):
            package.package(self.bin, self.output, "1.7.0")
        exe.write_bytes(b"not an exe")
        with self.assertRaisesRegex(ValueError, "Windows executable"):
            package.package(self.bin, self.output, "1.7.0")

    def test_checksum_and_payload_tampering_are_detected(self):
        path = package.package(self.bin, self.output, "1.7.0")
        path.with_suffix(".zip.sha256").write_text("wrong hash", encoding="ascii")
        with self.assertRaisesRegex(ValueError, "Checksum"):
            package.verify(self.output, "1.7.0")
        package.write_checksum(path)
        with zipfile.ZipFile(path, "a") as archive:
            archive.writestr("unexpected.pdb", b"symbols")
        package.write_checksum(path)
        with self.assertRaisesRegex(ValueError, "payload"):
            package.verify(self.output, "1.7.0")

    def test_version_limits_labels_and_overlapping_paths(self):
        version_file = self.root / "VERSION"
        for version in ("v1.7.0", "1.7", "1.7.0-preview", "256.0.0", "1.256.0", "1.0.65536", "01.0.0"):
            version_file.write_text(version, encoding="utf-8")
            with self.assertRaises(ValueError):
                package.release_version(version_file)
        version_file.write_text("1.7.0\n", encoding="utf-8")
        self.assertEqual(package.release_version(version_file), "1.7.0")
        with self.assertRaises(ValueError):
            package.artifact_name("1.7.0", "../../escape")
        for output in (self.bin, self.bin / "publish", self.root):
            with self.assertRaises(ValueError):
                package.package(self.bin, output, "1.7.0")

    def test_msi_presence_size_and_checksum_are_required(self):
        path = package.package(self.bin, self.output, "1.7.0")
        msi = path.with_suffix(".msi")
        msi.write_bytes(b"small")
        package.write_checksum(msi)
        with self.assertRaisesRegex(ValueError, "small"):
            package.verify(self.output, "1.7.0", require_msi=True)
        msi.write_bytes(b"\0" * (1024 * 1024))
        package.write_checksum(msi)
        package.verify(self.output, "1.7.0", require_msi=True)


if __name__ == "__main__":
    unittest.main()
