"""
Length-prefixed JSON framing for the Python <-> Unity Editor bridge socket.

Wire format (identical on both ends):
    [4-byte big-endian uint32 length][UTF-8 JSON payload of that length]

This module intentionally has zero dependency on the MCP SDK — it only knows
about the Unity bridge socket, so it can be unit-tested (or reused by a future
non-MCP client) in isolation.
"""
import asyncio
import json
import struct
from typing import Optional

HEADER_SIZE = 4
MAX_FRAME_SIZE = 16 * 1024 * 1024  # 16 MB — mirrors the guard on the Unity side


def encode_message(message: dict) -> bytes:
    payload = json.dumps(message).encode("utf-8")
    if len(payload) > MAX_FRAME_SIZE:
        raise ValueError(f"Message of {len(payload)} bytes exceeds MAX_FRAME_SIZE ({MAX_FRAME_SIZE})")
    header = struct.pack(">I", len(payload))
    return header + payload


async def read_message(reader: asyncio.StreamReader) -> Optional[dict]:
    """Reads one framed message. Returns None on clean EOF (peer closed the connection)."""
    header = await _read_exact(reader, HEADER_SIZE)
    if header is None:
        return None

    (length,) = struct.unpack(">I", header)
    if length <= 0 or length > MAX_FRAME_SIZE:
        raise ValueError(f"Invalid frame length received from Unity: {length}")

    payload = await _read_exact(reader, length)
    if payload is None:
        return None

    return json.loads(payload.decode("utf-8"))


async def _read_exact(reader: asyncio.StreamReader, count: int) -> Optional[bytes]:
    try:
        return await reader.readexactly(count)
    except asyncio.IncompleteReadError:
        return None
