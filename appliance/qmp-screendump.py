#!/usr/bin/env python3
"""Capture the QEMU VGA framebuffer via QMP. Usage: qmp-screendump.py <qmp.sock> <out.ppm>"""
import socket
import sys
import time

sock, out = sys.argv[1], sys.argv[2]
try:
    s = socket.socket(socket.AF_UNIX)
    s.connect(sock)
    s.settimeout(5)
    s.recv(65536)  # QMP greeting
    s.sendall(b'{"execute":"qmp_capabilities"}\n')
    s.recv(65536)
    s.sendall(b'{"execute":"screendump","arguments":{"filename":"%s"}}\n' % out.encode())
    s.recv(65536)
    time.sleep(1)
    print("screendump ->", out)
except Exception as e:  # noqa: BLE001 — best-effort diagnostic
    print("qmp screenshot failed:", e)
