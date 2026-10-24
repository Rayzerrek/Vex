import base64
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "kitty_graphics_probe", Path(__file__).resolve().parents[1] / "tools/kitty_graphics_probe.py"
)
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class KittyGraphicsProbeTests(unittest.TestCase):
    def test_query_is_direct_rgb_not_a_file_request(self):
        self.assertEqual(
            probe.query(),
            b"\x1b_Ga=q,t=d,f=24,s=1,v=1,i=314159;/wAA\x1b\\",
        )

    def test_sample_has_bounded_independently_decodable_chunks(self):
        commands = list(probe.sample())
        self.assertGreater(len(commands), 1)
        pixels = bytearray()
        for index, command in enumerate(commands):
            self.assertTrue(command.startswith(b"\x1b_G"))
            self.assertTrue(command.endswith(b"\x1b\\"))
            control, payload = command[3:-2].split(b";", 1)
            self.assertLessEqual(len(payload), 4096)
            self.assertIn(b"m=0" if index == len(commands) - 1 else b"m=1", control)
            if index:
                self.assertIn(control, (b"m=0", b"m=1"))
            else:
                self.assertIn(b"a=T,t=d,f=24,s=128,v=64", control)
            pixels.extend(base64.b64decode(payload, validate=True))
        self.assertEqual(len(pixels), 128 * 64 * 3)
        self.assertEqual(pixels[:3], bytes((0, 0, 160)))
        self.assertEqual(pixels[-3:], bytes((255, 255, 160)))

    def test_reply_requires_matching_id_and_complete_terminator(self):
        self.assertIsNone(probe.REPLY.search(b"\x1b_Gi=1;OK\x1b\\"))
        self.assertIsNone(probe.REPLY.search(b"\x1b_Gi=314159;OK\x1b"))
        for result in (b"OK", b"EINVAL: unsupported format"):
            reply = b"noise\x1b_Gi=314159;" + result + b"\x1b\\tail"
            self.assertEqual(probe.REPLY.search(reply)[1], result)
        self.assertEqual(probe.REPLY.search(b"\x1b_Gi=314159,p=1;OK\x1b\\")[1], b"OK")


if __name__ == "__main__":
    unittest.main()
