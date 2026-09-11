"""Patch the cue index of a large Matroska file in place, without loading the file.

REPRODUCTION TOOL for the audit finding "extraction with a mixed cue index is lossy and unstable"
(knowledge/audit-2026-09-11.md). Usage:

    python3 tests/fixtures/patch_cues.py report  <file.mkv>
    python3 tests/fixtures/patch_cues.py patch   <file.mkv> <dest.mkv> <matroska-track-number> <every_nth>

Turn every second CueRelativePosition of one subtitle track into Void, keeping the payload length so
the rest of the file keeps its byte offsets. The same track of the same episode then extracts as 803
cues complete, and 843 / 401 cues patched (three different answers, no error reported).


Finds the Cues element through the Segment/SeekHead (the small part at the start of the file), reads
just that element, and rewrites every nth CueRelativePosition (0xF0) of one track into Void (0xEC) with
the same payload length. Because the payload length is unchanged, every other byte in the file keeps
its offset and the SeekHead/Cues positions stay valid.

"""
import pathlib
import shutil
import sys

CUES_ID = bytes.fromhex('1C53BB6B')
SEGMENT_ID = bytes.fromhex('18538067')
SEEK_HEAD_ID = bytes.fromhex('114D9B74')
CUE_POINT = 0xBB
CUE_TRACK_POSITIONS = 0xB7
CUE_TRACK = 0xF7
CUE_CLUSTER_POSITION = 0xF1
CUE_RELATIVE_POSITION = 0xF0
VOID = 0xEC
HEAD_BYTES = 4 * 1024 * 1024
MAX_CUES = 64 * 1024 * 1024


def rid(buf, p, limit=None):
    f = buf[p]
    n, m = 1, 0x80
    while not (f & m):
        m >>= 1
        n += 1
        if n > 4:
            return None, 0
    if limit and p + n > limit:
        return None, 0
    return bytes(buf[p:p + n]), n


def rv(buf, p, limit=None):
    f = buf[p]
    if f == 0:
        return None, 0
    n, m = 1, 0x80
    while not (f & m):
        m >>= 1
        n += 1
        if n > 8 or (limit and p + n > limit):
            return None, 0
    v = f & (m - 1)
    for i in range(1, n):
        v = (v << 8) | buf[p + i]
    return v, n


def find_segment_and_cues(head):
    """Returns (segment_data_start, absolute Cues offset, Cues size, Cues header length) from the head bytes."""
    seg = head.find(SEGMENT_ID)
    if seg < 0:
        return None
    _, seg_len = rv(head, seg + 4)
    segment_data = seg + 4 + seg_len
    sh = head.find(SEEK_HEAD_ID, segment_data)
    if sh < 0:
        return None
    sh_size, sh_size_len = rv(head, sh + 4)
    pos, end = sh + 4 + sh_size_len, sh + 4 + sh_size_len + sh_size
    while pos < end:
        eid, id_len = rid(head, pos)
        esize, size_len = rv(head, pos + id_len)
        if eid is None or esize is None:
            break
        if eid == bytes.fromhex('4DBB'):                      # Seek entry
            p, e = pos + id_len + size_len, pos + id_len + size_len + esize
            seek_id, seek_pos = None, None
            while p < e:
                sid, sid_len = rid(head, p)
                ssize, ssize_len = rv(head, p + sid_len)
                if sid is None or ssize is None:
                    break
                val = int.from_bytes(head[p + sid_len + ssize_len: p + sid_len + ssize_len + ssize], 'big')
                if sid == bytes.fromhex('53AB'):      # SeekID
                    seek_id = head[p + sid_len + ssize_len: p + sid_len + ssize_len + ssize]
                if sid == bytes.fromhex('53AC'):      # SeekPosition
                    seek_pos = val
                p += sid_len + ssize_len + ssize
            if seek_id == CUES_ID and seek_pos is not None:
                # The Cues element usually sits at the very end of a large file, well outside the head
                # bytes, so only its offset is read here; the header and payload are fetched in load().
                return segment_data, segment_data + seek_pos, None, 0
        pos += id_len + size_len + esize
    return None


def parse_cues(cues):
    """Per-track counts and the positions of every offset element inside the Cues payload."""
    data = cues
    per_track, positions = {}, []
    pos, end = 0, len(cues)
    points = 0
    while pos < end:
        i_id, i_len = rid(cues, pos)
        if i_id is None:
            break
        i_size, i_size_len = rv(cues, pos + i_len)
        if i_size is None or pos + i_len + i_size_len + i_size > end:
            break
        if i_id == bytes([CUE_POINT]):
            points += 1
            p, e = pos + i_len + i_size_len, pos + i_len + i_size_len + i_size
            while p < e:
                j_id, j_len = rid(cues, p)
                if j_id is None:
                    break
                j_size, j_size_len = rv(cues, p + j_len)
                if j_size is None or p + j_len + j_size_len + j_size > len(cues):
                    break
                if j_id == bytes([CUE_TRACK_POSITIONS]):
                    track, cluster, relative = None, 0, 0
                    q, qe = p + j_len + j_size_len, p + j_len + j_size_len + j_size
                    while q < qe:
                        k_id, k_len = rid(cues, q)
                        if k_id is None:
                            break
                        k_size, k_size_len = rv(cues, q + k_len)
                        if k_size is None:
                            break
                        val_start = q + k_len + k_size_len
                        if k_id == bytes([CUE_TRACK]):
                            track = cues[val_start]
                        if k_id == bytes([CUE_CLUSTER_POSITION]):
                            cluster += 1
                            positions.append((track, 'F1', q, k_size))
                        if k_id == bytes([CUE_RELATIVE_POSITION]):
                            relative += 1
                            positions.append((track, 'F0', q, k_size))
                        q = val_start + k_size
                    per_track.setdefault(track, {'cluster': 0, 'relative': 0})
                    per_track[track]['cluster'] += cluster
                    per_track[track]['relative'] += relative
                p += j_len + j_size_len + j_size
        pos += i_len + i_size_len + i_size
    return points, per_track, positions


def load(src, mode):
    with open(src, 'rb') as fh:
        head = fh.read(HEAD_BYTES)
        found = find_segment_and_cues(head)
        if not found:
            print('no Segment/SeekHead/Cues found in the first', HEAD_BYTES, 'bytes')
            return None
        segment_data, cues_at, _, _ = found
        fh.seek(cues_at)
        header = fh.read(32)
        cue_id, cue_id_len = rid(header, 0)
        cues_size, cue_size_len = rv(header, cue_id_len)
        if cue_id != CUES_ID or cues_size is None or cues_size <= 0 or cues_size > MAX_CUES:
            print('Cues header unusable at', cues_at, header[:16].hex(' '))
            return None
        cues_header = cue_id_len + cue_size_len
        fh.seek(cues_at + cues_header)
        cues = bytearray(fh.read(cues_size))
        print(f'{mode}: Segment data at {segment_data}, Cues at {cues_at}, payload {cues_size} bytes')
        return {'segment_data': segment_data, 'cues_at': cues_at, 'cues_header': cues_header, 'cues': cues}


def main():
    mode, src = sys.argv[1], sys.argv[2]
    info = load(src, mode)
    if not info:
        return
    points, per_track, positions = parse_cues(bytes(info['cues']))
    print('CuePoints:', points)
    for track in sorted(per_track, key=lambda t: (t is None, t)):
        print(f"   track {track}: cluster offsets {per_track[track]['cluster']}, block offsets {per_track[track]['relative']}")

    if mode != 'patch':
        return

    dest, track, every = sys.argv[3], int(sys.argv[4]), int(sys.argv[5])
    print(f'copying to {dest} …')
    shutil.copyfile(src, dest)
    cues = info['cues']
    flipped = kept = 0
    for t, kind, header_pos, payload_len in positions:
        if kind != 'F0' or t != track:
            continue
        if (flipped + kept) % every == 0:
            cues[header_pos] = VOID
            flipped += 1
        else:
            kept += 1
    with open(dest, 'r+b') as fh:
        fh.seek(info['cues_at'] + info['cues_header'])
        fh.write(cues)
    print(f'patched {dest}: {flipped} block offsets removed for track {track}, {kept} left in place')
    points2, per_track2, _ = parse_cues(bytes(cues))
    print('after patching:')
    for t in sorted(per_track2, key=lambda t: (t is None, t)):
        print(f"   track {t}: cluster offsets {per_track2[t]['cluster']}, block offsets {per_track2[t]['relative']}")


if __name__ == '__main__':
    main()
