#!/usr/bin/env python3
"""Builds the one Matroska file whose cue index makes the per-pass dedup key collide.

FIX_PLAN's B24: the cue-indexed loop skips a cue point whose key it has already seen, and the key is
`clusterPosition * 31 + cueRelativePosition` - a hash of two numbers rather than the pair itself. Two cue
points whose (position, relative) pairs differ can therefore share a key, and the second one is skipped
without ever reading its cluster, which loses a subtitle silently.

Reaching a collision needs 31 * (P2 - P1) + (R2 - R1) == 0. Cluster positions are byte offsets, so with
clusters a distance d apart the relative offsets must differ by exactly -31d. This generator takes the
smallest useful d - clusters exactly 1000 bytes apart, which needs no sparse hole and keeps the file 2 KB -
and gives the first cue point a relative offset of 31 000 (far outside its cluster, the kind of value a
foreign or damaged index carries) and the second one 0 (a legitimate offset: its block sits at the very
start of its cluster). Both clusters hold a subtitle block of the same track, so neither cue point is a
"missing block" case the pass would refuse over.

    python3 tests/fixtures/make_collision.py out.mkv [--second-relative 0|1]

`--second-relative 0` is the collision (one cue lost before the fix); `--second-relative 1` is the control:
the same file one byte different, where the keys no longer collide and both cues survive either way.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from make_remux import (  # noqa: E402  (the primitives are shared with the other fixture generator)
    AUDIO, CLUSTER, CUES, CUE_CLUSTER_POSITION, CUE_POINT, CUE_RELATIVE_POSITION, CUE_TIME, CUE_TRACK,
    CUE_TRACK_POSITIONS, DURATION, EBML_HEADER, INFO, MUXING_APP, SEEK, SEEKHEAD, SEEK_ID, SEEK_POSITION,
    SUBS, TIMECODE, TIMECODE_SCALE, TRACKS, VIDEO, element, fixed_uint, simple_block, track_entry, uint_bytes,
    vint_size,
)

VOID = bytes.fromhex('EC')
# The gap between the two clusters and the first cue point's (bogus) relative offset are computed from the
# first cluster's own length: 31 * gap + second_relative == first_relative is what makes the two keys equal.
SECOND_RELATIVE = 0


def ebml_header():
    return element(EBML_HEADER, b''.join([
        element(bytes.fromhex('4286'), uint_bytes(1)),
        element(bytes.fromhex('42F7'), uint_bytes(1)),
        element(bytes.fromhex('42F2'), uint_bytes(4)),
        element(bytes.fromhex('42F3'), uint_bytes(8)),
        element(bytes.fromhex('4282'), b'matroska'),
        element(bytes.fromhex('4287'), uint_bytes(4)),
        element(bytes.fromhex('4285'), uint_bytes(2)),
    ]))


def build(path, second_relative, break_collision=False):
    tracks = element(TRACKS,
                     track_entry(VIDEO, 1, 'V_MPEG4/ISO/ASP') + track_entry(AUDIO, 2, 'A_AAC')
                     + track_entry(SUBS, 17, 'S_TEXT/UTF8'))
    info = element(INFO, element(TIMECODE_SCALE, uint_bytes(1_000_000))
                   + element(DURATION, fixed_uint(2, 8)) + element(MUXING_APP, b'collision'))
    seek_ids = [TRACKS, CUES]
    seek_payload = b''.join(element(SEEK, element(SEEK_ID, eid) + element(SEEK_POSITION, fixed_uint(0, 8)))
                            for eid in seek_ids)
    seek_head = element(SEEKHEAD, seek_payload)

    with open(path, 'wb') as handle:
        handle.write(ebml_header())
        # Segment with an unknown size, like every other fixture this project generates.
        handle.write(bytes.fromhex('18538067') + b'\x01\xFF\xFF\xFF\xFF\xFF\xFF\xFF')
        segment_start = handle.tell()
        handle.write(seek_head)
        # Where the seek entries' target offsets have to be written later.
        # The seek entries' target offsets are patched in place once the file is written; their slot
        # positions come from the payload layout rather than from walking it.
        offset_slots = []
        payload_start = segment_start + len(SEEKHEAD) + len(vint_size(len(seek_payload)))
        slot = 0
        for eid in seek_ids:
            entry_len = len(element(SEEK, element(SEEK_ID, eid) + element(SEEK_POSITION, fixed_uint(0, 8))))
            # The first SEEK's payload begins after its own header; the position field is the last 8 bytes.
            offset_slots.append(payload_start + slot + entry_len - 8)
            slot += entry_len

        info_pos = handle.tell()
        handle.write(info)
        tracks_pos = handle.tell()
        handle.write(tracks)

        cluster_a_pos = handle.tell()
        cluster_a = element(CLUSTER,
                            element(TIMECODE, uint_bytes(5))
                            + simple_block(SUBS, 0, b'first cue, from the colliding cue point')
                            + simple_block(VIDEO, 0, b'\x00' * 8))
        handle.write(cluster_a)
        cluster_b_pos = handle.tell()
        gap = cluster_b_pos - cluster_a_pos

        # The subtitle block sits first, so a relative offset of 0 points exactly at it.
        body_b = (simple_block(SUBS, 0, b'second cue, its cue point is the one that collides')
                  + element(TIMECODE, uint_bytes(6))
                  + simple_block(VIDEO, 0, b'\x00' * 8))
        handle.write(element(CLUSTER, body_b))

        def cue(time, cluster_offset, relative):
            return element(CUE_POINT, element(CUE_TIME, uint_bytes(time))
                           + element(CUE_TRACK_POSITIONS,
                                     element(CUE_TRACK, uint_bytes(SUBS))
                                     + element(CUE_CLUSTER_POSITION, uint_bytes(cluster_offset))
                                     + element(CUE_RELATIVE_POSITION, uint_bytes(relative))))

        # The collision: 31 * gap + second_relative == first_relative, so both cue points hash to one key.
        first_relative = 31 * gap + second_relative + (1 if break_collision else 0)
        cues_pos = handle.tell()
        handle.write(element(CUES, cue(5, cluster_a_pos - segment_start, first_relative)
                             + cue(6, cluster_b_pos - segment_start, second_relative)))

        real = {TRACKS: tracks_pos - segment_start, CUES: cues_pos - segment_start}
        handle.flush()
        for slot_pos, eid in zip(offset_slots, seek_ids):
            handle.seek(slot_pos)
            handle.write(fixed_uint(real[eid], 8))

    print(f'{path}: clusters {gap} bytes apart, cue points at relative {first_relative} and '
          f'{second_relative} -> dedup keys {first_relative} and {31 * gap + second_relative} '
          f'({"collide" if first_relative == 31 * gap + second_relative else "differ"})')
    return first_relative == 31 * gap + second_relative


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('out')
    parser.add_argument('--second-relative', type=int, default=0)
    parser.add_argument('--break-collision', action='store_true',
                        help='nudge the first cue point one byte off, so the keys no longer collide (control)')
    args = parser.parse_args()
    build(args.out, args.second_relative, args.break_collision)


if __name__ == '__main__':
    main()
