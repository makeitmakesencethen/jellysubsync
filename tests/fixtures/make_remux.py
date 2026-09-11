"""Builds a Blu-ray-remux-shaped Matroska file without needing a real remux.

Shape:
  EBML, Segment(unknown size), SeekHead, Info, Tracks, N clusters, Cues at the end
  track 1 video, track 2 audio, track 3 SRT subtitles
  each cluster: one big video SimpleBlock (payload is a sparse hole), a small audio block,
                and a subtitle block every `sub_every` clusters
  Cues: one entry per video cluster plus one per subtitle block (as mkvmerge/ffmpeg write)

The video payload is left as a sparse hole, so a 60 GB file costs almost no disk space —
but any parser that reads payloads instead of skipping them pays for all 60 GB, which is
exactly the behaviour under test.

Usage:
  python3 make_remux.py out.mkv [--clusters N] [--payload MB] [--sub-every K]
                       [--no-sub-cues] [--no-video-cues] [--no-cues]
"""
import argparse
import os
import struct

VIDEO, AUDIO, SUBS = 1, 2, 3

EBML_HEADER = bytes.fromhex('1A45DFA3')
SEGMENT = bytes.fromhex('18538067')
SEEKHEAD = bytes.fromhex('114D9B74')
SEEK = bytes.fromhex('4DBB')
SEEK_ID = bytes.fromhex('53AB')
SEEK_POSITION = bytes.fromhex('53AC')
INFO = bytes.fromhex('1549A966')
TIMECODE_SCALE = bytes.fromhex('2AD7B1')
DURATION = bytes.fromhex('4489')
MUXING_APP = bytes.fromhex('4D80')
TRACKS = bytes.fromhex('1654AE6B')
TRACK_ENTRY = bytes.fromhex('AE')
TRACK_NUMBER = bytes.fromhex('D7')
TRACK_UID = bytes.fromhex('73C5')
TRACK_TYPE = bytes.fromhex('83')
CODEC_ID = bytes.fromhex('86')
FLAG_LACING = bytes.fromhex('9C')
DEFAULT_DURATION = bytes.fromhex('23E383')
LANGUAGE = bytes.fromhex('22B59C')
CLUSTER = bytes.fromhex('1F43B675')
TIMECODE = bytes.fromhex('E7')
SIMPLE_BLOCK = bytes.fromhex('A3')
CUES = bytes.fromhex('1C53BB6B')
CUE_POINT = bytes.fromhex('BB')
CUE_TIME = bytes.fromhex('B3')
CUE_TRACK_POSITIONS = bytes.fromhex('B7')
CUE_TRACK = bytes.fromhex('F7')
CUE_CLUSTER_POSITION = bytes.fromhex('F1')
CUE_RELATIVE_POSITION = bytes.fromhex('F0')

UNKNOWN_SIZE = b'\x01\xFF\xFF\xFF\xFF\xFF\xFF\xFF'


def vint_size(value, length=None):
    """Size VINT (marker bit included)."""
    if length is None:
        length = 1
        while value >= (1 << (7 * length)) - 1:
            length += 1
    out = bytearray(length)
    for i in range(length - 1, -1, -1):
        out[i] = value & 0xFF
        value >>= 8
    out[0] |= 0x80 >> (length - 1)
    return bytes(out)


def uint_bytes(value):
    return b'\x00' if value == 0 else value.to_bytes((value.bit_length() + 7) // 8, 'big')


def element(element_id, payload):
    return element_id + vint_size(len(payload)) + payload


def fixed_uint(value, width):
    return value.to_bytes(width, 'big')


def track_entry(number, track_type, codec, extra=b''):
    body = (element(TRACK_NUMBER, uint_bytes(number))
            + element(TRACK_UID, uint_bytes(number))
            + element(TRACK_TYPE, uint_bytes(track_type))
            + element(FLAG_LACING, b'\x00')
            + element(CODEC_ID, codec.encode('ascii'))
            + element(LANGUAGE, b'eng')
            + extra)
    return element(TRACK_ENTRY, body)


def simple_block(track, timecode, payload, keyframe=True):
    header = vint_size(track) + struct.pack('>h', timecode) + bytes([0x80 if keyframe else 0x00])
    return element(SIMPLE_BLOCK, header + payload)


def build(path, clusters, payload_mb, sub_every, sub_cues=True, video_cues=True, cues=True,
          rel_pos=True, sub_tracks=1):
    payload = payload_mb * 1024 * 1024
    sub_texts = [f"{i}\n00:00:{i % 60:02d},000 --> 00:00:{(i % 60) + 3:02d},000\nSubtitle line {i}\n\n"
                 for i in range(1, clusters // sub_every + 2)]
    # Extra subtitle tracks carry recognisably different text, so a pass that shares one read of the
    # file across tracks can be checked for putting the right blocks in the right track.
    extra_texts = [[f"{i}\n00:00:{i % 60:02d},000 --> 00:00:{(i % 60) + 3:02d},000\nTrack {k} line {i}\n\n"
                    for i in range(1, clusters // sub_every + 2)]
                   for k in range(1, max(sub_tracks, 1))]
    sub_track_numbers = [SUBS + k for k in range(max(sub_tracks, 1))]

    tracks_elt = element(TRACKS,
                         track_entry(VIDEO, 1, 'V_MPEG4/ISO/ASP', element(DEFAULT_DURATION, uint_bytes(int(2e10))))
                         + track_entry(AUDIO, 2, 'A_AAC')
                         + b''.join(track_entry(num, 17, 'S_TEXT/UTF8')
                                    for num in sub_track_numbers))

    info = element(INFO, element(TIMECODE_SCALE, uint_bytes(1_000_000))
                   + element(DURATION, struct.pack('>d', float(clusters)))
                   + element(MUXING_APP, b'mkvbench'))

    ebml_header = element(EBML_HEADER, b''.join([
        element(bytes.fromhex('4286'), uint_bytes(1)),
        element(bytes.fromhex('42F7'), uint_bytes(1)),
        element(bytes.fromhex('42F2'), uint_bytes(4)),
        element(bytes.fromhex('42F3'), uint_bytes(8)),
        element(bytes.fromhex('4282'), b'matroska'),
        element(bytes.fromhex('4287'), uint_bytes(4)),
        element(bytes.fromhex('4285'), uint_bytes(2)),
    ]))

    # SeekHead with fixed-width offsets so it can be patched in place afterwards.
    seek_entries = [(CUES, 0), (TRACKS, 0), (INFO, 0)]
    if not cues:
        seek_entries = seek_entries[1:]
    sk_payload = b''.join(element(SEEK, element(SEEK_ID, eid) + element(SEEK_POSITION, fixed_uint(0, 8)))
                          for eid, _ in seek_entries)
    sk_element = element(SEEKHEAD, sk_payload)

    with open(path, 'wb') as f:
        f.write(ebml_header)
        f.write(SEGMENT)
        f.write(UNKNOWN_SIZE)
        segment_start = f.tell()

        seekhead_pos = f.tell()
        f.write(sk_element)
        offset_field_positions = []
        # remember where each 8-byte SEEK_POSITION value sits inside the SeekHead
        cursor = seekhead_pos + len(SEEKHEAD) + len(vint_size(len(sk_payload)))
        for _ in seek_entries:
            cursor += len(SEEK) + len(vint_size(len(element(SEEK_ID, _[0]) ) + len(SEEK_POSITION) + len(vint_size(8)) + 8))
            entry_body = len(element(SEEK_ID, _[0])) + len(SEEK_POSITION) + len(vint_size(8))
            # recompute properly below
        # simpler and exact: locate the offsets by scanning the written bytes
        f.flush()
        raw_seekhead = open(path, 'rb').read()  # tiny read of the header area
        offset_field_positions = []
        search_from = seekhead_pos
        while True:
            idx = raw_seekhead.find(SEEK_POSITION, search_from)
            if idx < 0:
                break
            size_bytes = 1 if raw_seekhead[idx + 2] == 0x88 else None
            offset_field_positions.append(idx + 3)
            search_from = idx + 1

        info_pos = f.tell()
        f.write(info)
        tracks_pos = f.tell()
        f.write(tracks_elt)

        video_offsets = []
        sub_marks = []
        sub_index = 0
        for i in range(clusters):
            cluster_time = i * 1000
            inner = element(TIMECODE, uint_bytes(cluster_time))
            inner += simple_block(VIDEO, 0, b'V' * 16)
            inner += simple_block(AUDIO, 0, b'A' * 8)
            write_sub = (i % sub_every == 0)
            sub_rels = []
            if write_sub:
                for k in range(len(sub_track_numbers)):
                    sub_rels.append(len(inner))  # block offset inside the cluster data
                    text = sub_texts[sub_index % len(sub_texts)] if k == 0 \
                        else extra_texts[k - 1][sub_index % len(extra_texts[k - 1])]
                    inner += simple_block(sub_track_numbers[k], 0, text.encode('utf-8'))
                sub_index += 1
            # big video block: header written here, payload left as a sparse hole
            big_header = SIMPLE_BLOCK + vint_size(payload + 4) + vint_size(VIDEO) + struct.pack('>h', 0) + b'\x80'
            inner_len = len(inner) + len(big_header) + payload

            cluster_start = f.tell()
            f.write(CLUSTER + vint_size(inner_len) + inner + big_header)
            f.seek(payload, os.SEEK_CUR)
            video_offsets.append(cluster_start - segment_start)
            if write_sub:
                sub_marks.append((cluster_start - segment_start, sub_rels))

        points = []
        if video_cues:
            points.extend((VIDEO, off, i * 1000, None) for i, off in enumerate(video_offsets))
        if sub_cues:
            for track_index, track_number in enumerate(sub_track_numbers):
                for i, (off, rels) in enumerate(sub_marks):
                    points.append((track_number, off, i * 1000, rels[track_index]))
        points.sort(key=lambda p: (p[2], p[0]))
        cue_payload = bytearray()
        for track, offset, time, relative in points:
            tp = element(CUE_TRACK, uint_bytes(track)) + element(CUE_CLUSTER_POSITION, uint_bytes(offset))
            if relative is not None and rel_pos:
                tp += element(CUE_RELATIVE_POSITION, uint_bytes(relative))
            cue_payload += element(CUE_POINT, element(CUE_TIME, uint_bytes(time))
                                   + element(CUE_TRACK_POSITIONS, tp))
        cues_pos = f.tell()
        if cues:
            f.write(element(CUES, bytes(cue_payload)))
        else:
            # Nothing follows the last cluster: seek() alone does not extend a file, so the
            # final payload hole would not exist. Materialise it.
            f.truncate(cues_pos)

        # patch the SeekHead offsets (same width, so nothing moves)
        real = {CUES: cues_pos - segment_start, TRACKS: tracks_pos - segment_start,
                INFO: info_pos - segment_start}
        f.flush()
        with open(path, 'r+b') as patch:
            for (eid, _), pos in zip(seek_entries, offset_field_positions):
                patch.seek(pos)
                patch.write(fixed_uint(real[eid], 8))

    size = os.path.getsize(path)
    print(f"{path}: {size / 1e9:.2f} GB apparent, {clusters} clusters, "
          f"{len(video_offsets) if video_cues else 0} video cues, "
          f"{len(sub_marks) * len(sub_track_numbers) if sub_cues else 0} subtitle cues "
          f"over {len(sub_track_numbers)} track(s), "
          f"cues element {'yes' if cues else 'NO'}")


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--clusters', type=int, default=12000)
    ap.add_argument('--payload', type=int, default=5)
    ap.add_argument('--sub-every', type=int, default=300)
    ap.add_argument('--no-sub-cues', action='store_true')
    ap.add_argument('--no-video-cues', action='store_true')
    ap.add_argument('--no-cues', action='store_true')
    ap.add_argument('--no-rel-pos', action='store_true', help='omit CueRelativePosition (forces the block walk)')
    ap.add_argument('--sub-tracks', type=int, default=1, help='how many text subtitle tracks to write')
    args = ap.parse_args()
    build(args.out, args.clusters, args.payload, args.sub_every, sub_tracks=args.sub_tracks,
          sub_cues=not args.no_sub_cues, video_cues=not args.no_video_cues, cues=not args.no_cues,
          rel_pos=not args.no_rel_pos)
