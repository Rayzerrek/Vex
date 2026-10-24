#!/usr/bin/env python3
"""Local, interactive Kitty Graphics transport check (not an automated UI test)."""

import argparse
import base64
import contextlib
import os
import re
import sys
import time

IMAGE_ID = 314159
REPLY = re.compile(rb"\x1b_Gi=314159(?:,[^;\x1b]*)?;([^\x1b]*)\x1b\\")


def command(control, payload=b""):
    return b"\x1b_G" + control.encode("ascii") + b";" + payload + b"\x1b\\"


def query():
    return command(f"a=q,t=d,f=24,s=1,v=1,i={IMAGE_ID}", b"/wAA")


def sample():
    """A small RGB gradient, without a PNG decoder or filesystem dependency."""
    width, height = 128, 64
    pixels = bytes(
        component
        for y in range(height)
        for x in range(width)
        for component in (x * 255 // (width - 1), y * 255 // (height - 1), 160)
    )
    payload = base64.b64encode(pixels)
    for offset in range(0, len(payload), 4096):
        chunk = payload[offset:offset + 4096]
        more = int(offset + len(chunk) < len(payload))
        control = f"m={more}"
        if offset == 0:
            control = f"a=T,t=d,f=24,s={width},v={height},i={IMAGE_ID},q=2,c=16,r=4,{control}"
        yield command(control, chunk)


@contextlib.contextmanager
def response_reader():
    """Restore terminal modes even on timeout or Ctrl-C; never open a second PTY."""
    if os.name == "nt":
        import ctypes
        import msvcrt
        from ctypes import wintypes

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetStdHandle.argtypes = [wintypes.DWORD]
        kernel.GetStdHandle.restype = wintypes.HANDLE
        kernel.GetConsoleMode.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        kernel.SetConsoleMode.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        handles = [kernel.GetStdHandle(-10 & 0xffffffff), kernel.GetStdHandle(-11 & 0xffffffff)]
        modes = []
        try:
            for handle in handles:
                mode = wintypes.DWORD()
                if not kernel.GetConsoleMode(handle, ctypes.byref(mode)):
                    raise OSError("console mode is unavailable")
                modes.append(mode.value)
            # Raw input plus VT input; processed VT output on the same console.
            if not kernel.SetConsoleMode(handles[0], (modes[0] & ~7) | 0x200):
                raise OSError("could not enable VT input")
            if not kernel.SetConsoleMode(handles[1], modes[1] | 5):
                raise OSError("could not enable VT output")

            def read_byte():
                if msvcrt.kbhit():
                    char = msvcrt.getwch()
                    if char == "\x03":
                        raise KeyboardInterrupt
                    return char.encode("utf-8")
                time.sleep(0.005)
                return b""

            yield read_byte
        finally:
            for handle, mode in zip(handles, modes):
                kernel.SetConsoleMode(handle, mode)
    else:
        import select
        import termios
        import tty

        fd = sys.stdin.fileno()
        previous = termios.tcgetattr(fd)
        try:
            tty.setraw(fd)

            def read_byte():
                if select.select([fd], [], [], 0.02)[0]:
                    byte = os.read(fd, 1)
                    if byte == b"\x03":
                        raise KeyboardInterrupt
                    return byte
                return b""

            yield read_byte
        finally:
            termios.tcsetattr(fd, termios.TCSANOW, previous)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--show", action="store_true", help="also send a 16 by 4 cell RGB gradient after a successful query")
    parser.add_argument("--timeout", type=float, default=2.0)
    args = parser.parse_args()
    if not 0 < args.timeout <= 30:
        parser.error("--timeout must be between 0 and 30 seconds")
    if not sys.stdin.isatty() or not sys.stdout.isatty():
        parser.error("run directly in an interactive terminal, without redirection")
    print("Checking Kitty Graphics direct RGB transport…", flush=True)
    match = None
    with response_reader() as read_byte:
        sys.stdout.buffer.write(query())
        sys.stdout.buffer.flush()
        deadline = time.monotonic() + args.timeout
        received = b""
        while time.monotonic() < deadline:
            received = (received + read_byte())[-8192:]
            match = REPLY.search(received)
            if match:
                break
        if match and match[1] == b"OK" and args.show:
            for chunk in sample():
                sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()
    if not match:
        print("No reply: graphics may be disabled, unsupported, or stripped by the PTY/multiplexer.")
        return 1
    print("Kitty reply:", match[1].decode("ascii", errors="replace"))
    if match[1] == b"OK" and args.show:
        print("A gradient should be visible. Success replies alone do not prove rendering works.")
    return 0 if match[1] == b"OK" else 1


if __name__ == "__main__":
    raise SystemExit(main())
