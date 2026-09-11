#!/usr/bin/env python3
"""Small EBML surgery helpers used by the fixture builder.

Everything here rewrites an element *in place* — the replacement has exactly the same
length as the original — so every absolute offset in the file (SeekHead, segment size,
Cues) stays valid and the file is still well formed.
"""
import struct

CUES_ID = bytes.fromhex("1C53BB6B")
VOID_ID = bytes.fromhex("EC")
SEEKHEAD_ID = bytes.fromhex("114D9B74")


def _vint(buf, pos):
    first = buf[pos]
    if first == 0:
        return None, 0
    length = 1
    mask = 0x80
    while not (first & mask):
        length += 1
        mask >>= 1
    value = first & (mask - 1)
    for i in range(1, length):
        value = (value << 8) | buf[pos + i]
    return value, length


def _encode_vint(value, length):
    out = bytearray(length)
    for i in range(length - 1, -1, -1):
        out[i] = value & 0xFF
        value >>= 8
    out[0] |= 1 << (8 - length)
    return bytes(out)


def _element_end(buf, pos, size):
    """Handle an unknown-size element by scanning for the next top-level ID we know."""
    if size is not None and size >= 0:
        return None, size
    return None, None


def find_element(buf, elem_id, start=0, end=None):
    """Find the real element with this ID.

    The ID also appears in the SeekHead's index (that is how the muxer points at it), so a plain
    byte search finds the index entry first. Candidates are therefore validated: the size has to
    land inside the file and the payload has to start with a valid child element.

    Returns (id_pos, size_value, size_len, data_pos, data_end) of the last plausible match.
    """
    if end is None:
        end = len(buf)
    hits = []
    pos = start
    while pos < len(buf) - 4:
        if buf[pos:pos + len(elem_id)] == elem_id:
            val, ln = _vint(buf, pos + len(elem_id))
            if val is not None:
                data = pos + len(elem_id) + ln
                stop = data + val
                if stop <= len(buf) and val > 64:
                    hits.append((pos, val, ln, data, stop))
        pos += 1
    if not hits:
        return None
    return max(hits, key=lambda h: h[4])


def strip_cues(path):
    """Replace the Matroska Cues element with a Void of identical length.

    The file keeps every other byte at the same offset; only the cue index disappears,
    which is exactly the 'no index at all' case the extractor has to walk for.
    """
    with open(path, "rb") as f:
        buf = bytearray(f.read())
    found = find_element(buf, CUES_ID)
    if not found:
        raise RuntimeError("no Cues element in %s" % path)
    id_pos, size, size_len, data_pos, data_end = found
    total = data_end - id_pos
    payload = total - 4           # EC (1 byte) + 3-byte size field
    if payload < 0 or payload >= (1 << 21) - 1:
        raise RuntimeError("Cues element too big for a 3-byte void: %d" % total)
    buf[id_pos:id_pos + total] = VOID_ID + _encode_vint(payload, 3) + b"\x00" * payload
    with open(path, "wb") as f:
        f.write(bytes(buf))
    return total


if __name__ == "__main__":
    import sys
    print("stripped %d bytes of Cues from %s" % (strip_cues(sys.argv[1]), sys.argv[1]))
