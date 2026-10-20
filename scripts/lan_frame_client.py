"""Dependency-free reference client for DLSS5 Everything's opt-in LAN server.

Pixels are tightly packed RGBA8/sRGB, top row first. This module does not capture
your screen, launch applications, or install a virtual display driver.
"""

import argparse
import getpass
import hashlib
import hmac
import json
import socket
import ssl
import struct
from pathlib import Path


MAX_PIXELS = 64 * 1024 * 1024


class PhoenixClient:
    def __init__(self, host, port, fingerprint, token, width, height, settings=None):
        if not 1 <= width <= 16384 or not 1 <= height <= 16384 or width * height * 4 > MAX_PIXELS:
            raise ValueError("Invalid frame dimensions")
        expected = bytes.fromhex(fingerprint.replace(":", "").strip())
        if len(expected) != 32 or len(bytes.fromhex(token)) != 32:
            raise ValueError("A SHA-256 certificate fingerprint and 32-byte token are required")
        self._stream = None
        self._sequence = 0
        self.width, self.height = width, height
        self._pixel_bytes = width * height * 4
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        # A self-signed server is authenticated by the out-of-band SHA-256 pin,
        # checked BEFORE sending any token or pixels. Never omit this check.
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        raw = socket.create_connection((host, port), timeout=10)
        try:
            raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            self._stream = context.wrap_socket(raw, server_hostname=host)
            actual = hashlib.sha256(self._stream.getpeercert(binary_form=True)).digest()
            if not hmac.compare_digest(actual, expected):
                raise ssl.SSLError("Server certificate fingerprint mismatch")
            self._stream.settimeout(130)
            self._send_control({"op": "open", "version": 1, "token": token, "width": width,
                                "height": height, "settings": settings or {}})
            reply = self._control_reply()
            if (reply.get("version") != 1 or reply.get("width") != width or reply.get("height") != height
                    or reply.get("format") != "rgba8_srgb" or reply.get("codec") != "raw"):
                raise ValueError("Server returned an incompatible frame contract")
            self.session = reply["session"]
            self.revision = reply["revision"]
            self.settings = reply["settings"]
        except BaseException:
            self.abort()
            raw.close()
            raise

    def _read_exact(self, size):
        data = bytearray(size)
        offset = 0
        while offset < size:
            chunk = self._stream.recv_into(memoryview(data)[offset:])
            if chunk == 0:
                raise EOFError("Server disconnected")
            offset += chunk
        return data

    def _send(self, kind, payload):
        self._stream.sendall(struct.pack("<BI", kind, len(payload)))
        self._stream.sendall(payload)

    def _send_control(self, command):
        self._send(1, json.dumps(command, separators=(",", ":"), allow_nan=False).encode("utf-8"))

    def _reply(self):
        kind, size = struct.unpack("<BI", self._read_exact(5))
        if kind == 1 and 0 < size <= 4096:
            return kind, self._read_exact(size)
        if kind == 3 and size == self._pixel_bytes + 24:
            return kind, self._read_exact(size)
        raise ValueError("Invalid server packet size or type")

    def _control_reply(self):
        kind, body = self._reply()
        if kind != 1:
            raise ValueError("Expected a control reply")
        result = json.loads(body)
        if result.get("ok") is not True:
            raise RuntimeError(result.get("error", "Server rejected request"))
        return result

    def update(self, **settings):
        """Patch enabled/iterations/tone/structure/skin/auto_mask between frames."""
        self._send_control({"op": "update", "settings": settings})
        result = self._control_reply()
        self.revision, self.settings = result["revision"], result["settings"]
        return result

    def ping(self):
        """Call within 30 seconds when idle to retain the session."""
        self._send_control({"op": "ping"})
        return self._control_reply()

    def frame(self, rgba, reset=False):
        """Synchronous backpressure: returns (RGBA bytes, processing microseconds).

        Do not use this client concurrently from multiple threads.
        """
        if len(rgba) != self._pixel_bytes:
            raise ValueError("Frame length does not match session dimensions")
        self._sequence += 1
        self._stream.sendall(struct.pack("<BIQI", 2, self._pixel_bytes + 12, self._sequence, int(bool(reset))))
        self._stream.sendall(rgba)
        kind, body = self._reply()
        if kind == 1:
            raise RuntimeError(json.loads(body).get("error", "Frame processing failed"))
        sequence, revision, microseconds = struct.unpack_from("<QqQ", body)
        if sequence != self._sequence or revision != self.revision:
            raise ValueError("Frame sequence or settings revision mismatch")
        return bytes(memoryview(body)[24:]), microseconds

    def close(self):
        try:
            if self._stream is not None:
                self._send_control({"op": "close"})
                self._control_reply()
        finally:
            self.abort()

    def abort(self):
        if self._stream is not None:
            self._stream.close()
            self._stream = None

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        if exc_type is None:
            self.close()
        else:
            self.abort()


def main():
    parser = argparse.ArgumentParser(description="Process one raw RGBA8 frame using Phoenix over LAN")
    parser.add_argument("host")
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--width", type=int, required=True)
    parser.add_argument("--height", type=int, required=True)
    parser.add_argument("--port", type=int, default=47855)
    parser.add_argument("--fingerprint", required=True)
    args = parser.parse_args()
    size = args.width * args.height * 4
    if not 1 <= args.width <= 16384 or not 1 <= args.height <= 16384 or not 0 < size <= MAX_PIXELS:
        parser.error("Frame dimensions exceed limits")
    if args.input.stat().st_size != size:
        parser.error("Input must contain exactly width * height * 4 raw RGBA8 bytes")
    token = getpass.getpass("Pairing token: ")  # Not a command-line argument or shell-history entry.
    with PhoenixClient(args.host, args.port, args.fingerprint, token, args.width, args.height) as client:
        pixels, elapsed = client.frame(args.input.read_bytes(), reset=True)
        args.output.write_bytes(pixels)
        print(f"Returned {len(pixels)} bytes; host processing {elapsed / 1000:.1f} ms")


if __name__ == "__main__":
    main()
