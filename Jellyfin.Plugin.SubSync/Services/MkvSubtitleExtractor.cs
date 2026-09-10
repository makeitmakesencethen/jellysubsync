using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Reads text subtitle tracks straight out of a Matroska file by following its cue
/// index, instead of demuxing the whole container.
///
/// ffmpeg (and any generic demuxer) walks every cluster of the file to find a subtitle
/// that occupies a few kilobytes, so extraction costs one full-file read — minutes on a
/// large file over a NAS. Matroska files carry a Cues index; jumping to the clusters the
/// subtitle track actually lives in touches kilobytes instead.
///
/// Only text subtitle codecs are handled (S_TEXT/UTF8, S_TEXT/ASS, S_TEXT/SSA). Anything
/// unexpected — no cues, unknown codec, compressed blocks we cannot read, malformed
/// EBML — returns false and the caller falls back to ffmpeg, so this can only ever be a
/// speed-up, never a correctness risk.
/// </summary>
public static class MkvSubtitleExtractor
{
    private const ulong IdEbml = 0x1A45DFA3;
    private const ulong IdSegment = 0x18538067;
    private const ulong IdTracks = 0x1654AE6B;
    private const ulong IdTrackEntry = 0xAE;
    private const ulong IdTrackNumber = 0xD7;
    private const ulong IdTrackType = 0x83;
    private const ulong IdCodecId = 0x86;
    private const ulong IdCodecPrivate = 0x63A2;
    private const ulong IdContentEncodings = 0x6D80;
    private const ulong IdCues = 0x1C53BB6B;
    private const ulong IdCuePoint = 0xBB;
    private const ulong IdCueTime = 0xB3;
    private const ulong IdCueTrackPositions = 0xB7;
    private const ulong IdCueTrack = 0xF7;
    private const ulong IdCueClusterPosition = 0xF1;
    private const ulong IdCluster = 0x1F43B675;
    private const ulong IdClusterTimecode = 0xE7;
    private const ulong IdSimpleBlock = 0xA3;
    private const ulong IdBlockGroup = 0xA0;
    private const ulong IdBlock = 0xA1;
    private const ulong IdBlockDuration = 0x9B;
    private const ulong IdTimecodeScale = 0x2AD7B1;
    private const ulong IdVoid = 0xEC;


    /// <summary>Cheap pre-check: is this file a Matroska container?</summary>
    /// <param name="path">Candidate media path.</param>
    /// <returns>True when the extension and magic bytes look like EBML/Matroska.</returns>
    public static bool LooksLikeMatroska(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".mka", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".webm", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Span<byte> magic = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(magic) == 4
                && magic[0] == 0x1A && magic[1] == 0x45 && magic[2] == 0xDF && magic[3] == 0xA3;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts one embedded text subtitle track to SRT text.
    /// </summary>
    /// <param name="videoPath">Path of the media file (must be Matroska).</param>
    /// <param name="subtitleOrdinal">
    /// 0-based index among the file's subtitle tracks, matching ffmpeg's <c>0:s:N</c>.
    /// </param>
    /// <param name="srtText">The extracted subtitles when the method returns true.</param>
    /// <param name="reason">Why extraction was skipped, when it returns false.</param>
    /// <returns>True when a complete SRT was produced.</returns>
    public static bool TryExtract(string videoPath, int subtitleOrdinal, out string srtText, out string reason)
    {
        srtText = string.Empty;
        reason = string.Empty;

        try
        {
            using var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            return Extract(stream, subtitleOrdinal, out srtText, out reason);
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool Extract(FileStream stream, int subtitleOrdinal, out string srtText, out string reason)
    {
        srtText = string.Empty;
        reason = string.Empty;

        // --- EBML header (top level: EBML then Segment) ---
        if (!TryReadElementHeader(stream, out var id, out var size, out _))
        {
            reason = "not EBML";
            return false;
        }

        if (id != IdEbml)
        {
            reason = "no EBML header";
            return false;
        }

        Skip(stream, size);
        if (!TryReadElementHeader(stream, out id, out size, out _) || id != IdSegment)
        {
            reason = "no Segment";
            return false;
        }

        var segmentDataStart = stream.Position;
        long segmentDataEnd = size == UnknownSize ? stream.Length : segmentDataStart + (long)size;

        // --- walk top-level children once, remembering the pieces we need ---
        ulong timecodeScale = 1_000_000; // ns per timecode unit (1 ms)
        long tracksPosition = -1;
        long tracksSize = 0;
        long cuesPosition = -1;
        long cuesSize = 0;
        long firstClusterPosition = -1;

        stream.Position = segmentDataStart;
        while (stream.Position < segmentDataEnd)
        {
            var headerPosition = stream.Position;
            if (!TryReadElementHeader(stream, out id, out size, out var headerLength))
            {
                break;
            }

            var dataStart = headerPosition + headerLength;
            if (id == IdTimecodeScale)
            {
                timecodeScale = ReadUnsigned(stream, size);
            }
            else if (id == IdTracks)
            {
                tracksPosition = dataStart;
                tracksSize = (long)size;
            }
            else if (id == IdCues)
            {
                cuesPosition = dataStart;
                cuesSize = (long)size;
            }
            else if (id == IdCluster && firstClusterPosition < 0)
            {
                firstClusterPosition = headerPosition;
            }

            if (size == UnknownSize)
            {
                // Unknown-size elements only legitimately wrap clusters; if one appears
                // before the index we need, give up and let ffmpeg handle the file.
                if (id == IdCluster && (tracksPosition < 0 || cuesPosition < 0))
                {
                    reason = "unknown-size cluster before index";
                    return false;
                }

                stream.Position = dataStart;
                continue;
            }

            stream.Position = dataStart + (long)size;
        }

        if (tracksPosition < 0 || cuesPosition < 0)
        {
            reason = "no Tracks/Cues index";
            return false;
        }

        // --- find the requested subtitle track ---
        stream.Position = tracksPosition;
        var subtitleTracks = ReadSubtitleTracks(stream, tracksSize);
        if (subtitleTracks.Count == 0)
        {
            reason = "no subtitle tracks";
            return false;
        }

        if (subtitleOrdinal < 0 || subtitleOrdinal >= subtitleTracks.Count)
        {
            reason = $"subtitle ordinal {subtitleOrdinal} out of range ({subtitleTracks.Count} tracks)";
            return false;
        }

        var track = subtitleTracks[subtitleOrdinal];
        if (!track.IsText)
        {
            reason = $"codec {track.CodecId} is not text";
            return false;
        }

        if (track.Compressed)
        {
            reason = "compressed subtitle blocks";
            return false;
        }

        // --- cue points for that track: cluster offsets ---
        stream.Position = cuesPosition;
        var clusterOffsets = ReadCueClusterOffsets(stream, cuesSize, track.TrackNumber);
        if (clusterOffsets.Count == 0)
        {
            reason = "no cue points for track";
            return false;
        }

        // --- read only those clusters ---
        var cues = new List<Cue>();
        var assHeaderLines = CountAssHeaderLines(track.CodecPrivate);
        var seenClusters = new HashSet<long>();
        foreach (var offset in clusterOffsets)
        {
            var clusterPosition = segmentDataStart + offset;
            if (clusterPosition < 0 || clusterPosition >= stream.Length || !seenClusters.Add(clusterPosition))
            {
                continue;
            }

            stream.Position = clusterPosition;
            if (!ReadCluster(stream, segmentDataEnd, track, cues))
            {
                reason = "unsupported block encoding";
                return false;
            }
        }

        if (cues.Count == 0)
        {
            reason = "no subtitle blocks decoded";
            return false;
        }

        cues.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        srtText = ToSrt(cues);
        return srtText.Length > 0;
    }

    private sealed class SubtitleTrack
    {
        public ulong TrackNumber { get; set; }

        public string CodecId { get; set; } = string.Empty;

        public bool IsText => CodecId is "S_TEXT/UTF8" or "S_TEXT/ASS" or "S_TEXT/SSA" or "S_SSA/ASS";

        public bool IsAss => CodecId is "S_TEXT/ASS" or "S_TEXT/SSA" or "S_SSA/ASS";

        public bool Compressed { get; set; }

        public byte[]? CodecPrivate { get; set; }
    }

    private readonly struct Cue
    {
        public Cue(long startMs, long endMs, string text)
        {
            StartMs = startMs;
            EndMs = endMs;
            Text = text;
        }

        public long StartMs { get; }

        public long EndMs { get; }

        public string Text { get; }
    }

    private static List<SubtitleTrack> ReadSubtitleTracks(FileStream stream, long tracksSize)
    {
        var result = new List<SubtitleTrack>();
        var end = stream.Position + tracksSize;

        while (stream.Position < end)
        {
            var headerPosition = stream.Position;
            if (!TryReadElementHeader(stream, out var id, out var size, out var headerLength))
            {
                break;
            }

            if (size == UnknownSize)
            {
                break;
            }

            var dataStart = headerPosition + headerLength;
            if (id == IdTrackEntry)
            {
                var track = ReadTrackEntry(stream, dataStart, (long)size);
                if (track is not null && track.IsText)
                {
                    result.Add(track);
                }
                else if (track is not null)
                {
                    // Keep non-text tracks in the list so ordinals still line up with
                    // ffmpeg's 0:s:N numbering (PGS/VobSub occupy slots too).
                    result.Add(track);
                }
            }

            stream.Position = dataStart + (long)size;
        }

        return result;
    }

    private static SubtitleTrack? ReadTrackEntry(FileStream stream, long dataStart, long size)
    {
        var track = new SubtitleTrack();
        var type = -1;
        var end = dataStart + size;
        stream.Position = dataStart;

        while (stream.Position < end)
        {
            var headerPosition = stream.Position;
            if (!TryReadElementHeader(stream, out var id, out var elementSize, out var headerLength))
            {
                return null;
            }

            if (elementSize == UnknownSize)
            {
                return null;
            }

            var elementDataStart = headerPosition + headerLength;
            var elementDataEnd = elementDataStart + (long)elementSize;

            if (id == IdTrackType)
            {
                type = (int)ReadUnsigned(stream, elementSize);
            }
            else if (id == IdTrackNumber)
            {
                track.TrackNumber = ReadUnsigned(stream, elementSize);
            }
            else if (id == IdCodecId)
            {
                track.CodecId = ReadString(stream, elementSize);
            }
            else if (id == IdCodecPrivate)
            {
                track.CodecPrivate = ReadBytes(stream, elementSize);
            }
            else if (id == IdContentEncodings)
            {
                track.Compressed = true;
            }

            stream.Position = elementDataEnd;
        }

        // 0x11 == subtitle
        return type == 0x11 ? track : null;
    }

    private static List<long> ReadCueClusterOffsets(FileStream stream, long cuesSize, ulong trackNumber)
    {
        var offsets = new List<long>();
        var end = stream.Position + cuesSize;

        while (stream.Position < end)
        {
            var headerPosition = stream.Position;
            if (!TryReadElementHeader(stream, out var id, out var size, out var headerLength))
            {
                break;
            }

            if (size == UnknownSize)
            {
                break;
            }

            var dataStart = headerPosition + headerLength;
            if (id == IdCuePoint)
            {
                long clusterPosition = -1;
                var matched = false;
                var pointEnd = dataStart + (long)size;
                stream.Position = dataStart;

                while (stream.Position < pointEnd)
                {
                    var childHeaderPosition = stream.Position;
                    if (!TryReadElementHeader(stream, out var childId, out var childSize, out var childHeaderLength))
                    {
                        break;
                    }

                    if (childSize == UnknownSize)
                    {
                        break;
                    }

                    var childDataStart = childHeaderPosition + childHeaderLength;
                    if (childId == IdCueTrackPositions)
                    {
                        var positionsEnd = childDataStart + (long)childSize;
                        stream.Position = childDataStart;
                        ulong cueTrack = 0;
                        while (stream.Position < positionsEnd)
                        {
                            var posHeaderPosition = stream.Position;
                            if (!TryReadElementHeader(stream, out var posId, out var posSize, out var posHeaderLength))
                            {
                                break;
                            }

                            if (posSize == UnknownSize)
                            {
                                break;
                            }

                            var posDataStart = posHeaderPosition + posHeaderLength;
                            if (posId == IdCueTrack)
                            {
                                cueTrack = ReadUnsigned(stream, posSize);
                            }
                            else if (posId == IdCueClusterPosition)
                            {
                                clusterPosition = (long)ReadUnsigned(stream, posSize);
                            }

                            stream.Position = posDataStart + (long)posSize;
                        }

                        if (cueTrack == trackNumber)
                        {
                            matched = true;
                        }
                    }

                    stream.Position = childDataStart + (long)childSize;
                }

                if (matched && clusterPosition >= 0)
                {
                    offsets.Add(clusterPosition);
                }

                stream.Position = pointEnd;
                continue;
            }

            stream.Position = dataStart + (long)size;
        }

        return offsets;
    }

    private static bool ReadCluster(FileStream stream, long segmentDataEnd, SubtitleTrack track, List<Cue> cues)
    {
        var headerPosition = stream.Position;
        if (!TryReadElementHeader(stream, out var id, out var size, out var headerLength) || id != IdCluster)
        {
            return false;
        }

        var clusterDataStart = headerPosition + headerLength;
        var clusterDataEnd = size == UnknownSize
            ? Math.Min(segmentDataEnd, stream.Length)
            : clusterDataStart + (long)size;

        long clusterTimecode = 0;
        stream.Position = clusterDataStart;

        while (stream.Position < clusterDataEnd)
        {
            var childHeaderPosition = stream.Position;
            if (!TryReadElementHeader(stream, out var childId, out var childSize, out var childHeaderLength))
            {
                return false;
            }

            if (childSize == UnknownSize)
            {
                return false;
            }

            var childDataStart = childHeaderPosition + childHeaderLength;
            var childDataEnd = childDataStart + (long)childSize;

            if (childId == IdClusterTimecode)
            {
                clusterTimecode = (long)ReadUnsigned(stream, childSize);
            }
            else if (childId == IdSimpleBlock)
            {
                if (!ReadBlock(stream, childDataStart, childDataEnd, clusterTimecode, track, cues, hasDuration: false, durationTicks: 0))
                {
                    return false;
                }
            }
            else if (childId == IdBlockGroup)
            {
                long durationTicks = 0;
                var blockStart = -1L;
                var blockEnd = -1L;
                stream.Position = childDataStart;
                while (stream.Position < childDataEnd)
                {
                    var groupHeaderPosition = stream.Position;
                    if (!TryReadElementHeader(stream, out var groupId, out var groupSize, out var groupHeaderLength))
                    {
                        break;
                    }

                    if (groupSize == UnknownSize)
                    {
                        break;
                    }

                    var groupDataStart = groupHeaderPosition + groupHeaderLength;
                    if (groupId == IdBlock)
                    {
                        blockStart = groupDataStart;
                        blockEnd = groupDataStart + (long)groupSize;
                    }
                    else if (groupId == IdBlockDuration)
                    {
                        durationTicks = (long)ReadUnsigned(stream, groupSize);
                    }

                    stream.Position = groupDataStart + (long)groupSize;
                }

                if (blockStart >= 0 && blockEnd > blockStart)
                {
                    if (!ReadBlock(stream, blockStart, blockEnd, clusterTimecode, track, cues, hasDuration: true, durationTicks: durationTicks))
                    {
                        return false;
                    }
                }
            }

            stream.Position = childDataEnd;
        }

        stream.Position = clusterDataEnd;
        return true;
    }

    private static bool ReadBlock(
        FileStream stream,
        long dataStart,
        long dataEnd,
        long clusterTimecode,
        SubtitleTrack track,
        List<Cue> cues,
        bool hasDuration,
        long durationTicks)
    {
        var length = (int)(dataEnd - dataStart);
        if (length <= 4)
        {
            return true; // empty block — nothing to decode
        }

        var buffer = new byte[length];
        stream.Position = dataStart;
        var read = stream.Read(buffer, 0, length);
        if (read < length)
        {
            return false;
        }

        // Block header: track number (VINT) + int16 relative timecode + flags
        var offset = 0;
        if (!TryReadVint(buffer, ref offset, out var trackNumber, out _))
        {
            return false;
        }

        if (trackNumber != track.TrackNumber)
        {
            return true; // a different track's block inside the same cluster
        }

        if (offset + 3 > buffer.Length)
        {
            return false;
        }

        var relative = (short)((buffer[offset] << 8) | buffer[offset + 1]);
        var flags = buffer[offset + 2];
        offset += 3;

        var lacing = (flags >> 1) & 0x03;
        if (lacing != 0)
        {
            // Laced text subtitle blocks are rare; decoding them wrongly would be
            // silent corruption, so hand the whole file back to ffmpeg.
            return false;
        }

        var text = Encoding.UTF8.GetString(buffer, offset, buffer.Length - offset);
        AddCue(cues, clusterTimecode, relative, text, hasDuration, durationTicks, track.IsAss);
        return true;
    }

    private static void AddCue(
        List<Cue> cues,
        long clusterTimecode,
        short relative,
        string rawText,
        bool hasDuration,
        long durationTicks,
        bool isAssTrack)
    {
        var text = NormalizeText(rawText, isAssTrack);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var start = clusterTimecode + relative;
        var end = hasDuration && durationTicks > 0 ? start + durationTicks : start + 2000;

        cues.Add(new Cue(start, end, text));
    }

    /// <summary>
    /// Turns one subtitle block into plain text. Matroska's S_TEXT/ASS blocks hold the
    /// ASS payload (layer, style, margins… then the text) rather than a "Dialogue:" line,
    /// and Comment blocks carry no viewable subtitle.
    /// </summary>
    private static string NormalizeText(string raw, bool isAssTrack)
    {
        var text = raw.Replace("\r\n", "\n").Trim('\n', '\r', ' ', '\t');
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var isDialogueLine = text.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase);
        if (isDialogueLine)
        {
            // "Dialogue: ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text"
            var colon = text.IndexOf(':');
            text = SkipAssFields(text[(colon + 1)..], 9);
        }
        else if (isAssTrack)
        {
            // Matroska ASS payload: ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,
            // Effect then the text — one field fewer than a "Dialogue:" line, which also
            // carries the start/end timestamps.
            text = SkipAssFields(text, 8);
        }

        // ASS formatting: {\i1} style tags out, \N / \n as line breaks.
        var builder = new StringBuilder(text.Length);
        var inTag = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                inTag = true;
                continue;
            }

            if (c == '}')
            {
                inTag = false;
                continue;
            }

            if (inTag)
            {
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == 'N' || text[i + 1] == 'n'))
            {
                builder.Append('\n');
                i++;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Drops the first <paramref name="fields"/> comma-separated ASS fields and returns
    /// the remaining text. Commas inside the removed fields are tolerated because ASS
    /// timestamps ("0:00:05.02") contain colons, not commas, and override tags in the
    /// text are stripped afterwards.
    /// </summary>
    private static string SkipAssFields(string value, int fields)
    {
        var seen = 0;
        var index = 0;
        while (index < value.Length && seen < fields)
        {
            if (value[index] == ',')
            {
                seen++;
            }

            index++;
        }

        return seen == fields ? value[index..] : value;
    }

    private static int CountAssHeaderLines(byte[]? codecPrivate)
    {
        if (codecPrivate is null || codecPrivate.Length == 0)
        {
            return 0;
        }

        try
        {
            var header = Encoding.UTF8.GetString(codecPrivate);
            return header.Split('\n').Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string ToSrt(List<Cue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var end = cue.EndMs;
            // Without a BlockDuration the end is a guess; never let a guess overrun
            // the next cue.
            if (i + 1 < cues.Count && (end > cues[i + 1].StartMs || cue.EndMs - cue.StartMs == 2000))
            {
                var next = cues[i + 1].StartMs;
                end = next > cue.StartMs ? Math.Min(next - 1, cue.StartMs + 7000) : cue.StartMs + 1500;
            }

            if (end <= cue.StartMs)
            {
                continue;
            }

            builder.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatTime(cue.StartMs)).Append(" --> ").Append(FormatTime(end)).Append('\n');
            builder.Append(cue.Text).Append("\n\n");
            index++;
        }

        return builder.ToString();
    }

    private static string FormatTime(long totalMs)
    {
        if (totalMs < 0)
        {
            totalMs = 0;
        }

        var ms = totalMs % 1000;
        var totalSeconds = totalMs / 1000;
        var seconds = totalSeconds % 60;
        var minutes = (totalSeconds / 60) % 60;
        var hours = totalSeconds / 3600;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            hours,
            minutes,
            seconds,
            ms);
    }

    // ------------------------------------------------------------------ EBML helpers

    private const ulong UnknownSize = ulong.MaxValue;

    private static bool TryReadElementHeader(FileStream stream, out ulong id, out ulong size, out int headerLength)
    {
        id = 0;
        size = 0;
        headerLength = 0;
        var start = stream.Position;

        var first = stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        var idLength = LeadingZeroBytes((byte)first) + 1;
        if (idLength > 4)
        {
            stream.Position = start;
            return false;
        }

        // EBML element IDs keep their length-marker bits: the ID is the full byte
        // sequence (e.g. 0x1A45DFA3 for the EBML header), only the *size* vint has its
        // marker stripped.
        id = (ulong)(byte)first;
        for (var i = 1; i < idLength; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                stream.Position = start;
                return false;
            }

            id = (id << 8) | (ulong)(byte)b;
        }

        var sizeFirst = stream.ReadByte();
        if (sizeFirst < 0)
        {
            stream.Position = start;
            return false;
        }

        var sizeLength = LeadingZeroBytes((byte)sizeFirst) + 1;
        if (sizeLength > 8)
        {
            stream.Position = start;
            return false;
        }

        var value = (ulong)(sizeFirst & ((1 << (8 - sizeLength)) - 1));
        var allOnes = value == (ulong)((1 << (8 - sizeLength)) - 1);
        for (var i = 1; i < sizeLength; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                stream.Position = start;
                return false;
            }

            value = (value << 8) | (ulong)(byte)b;
            if (b != 0xFF)
            {
                allOnes = false;
            }
        }

        size = allOnes ? UnknownSize : value;
        headerLength = (int)(stream.Position - start);
        return true;
    }

    private static int LeadingZeroBytes(byte value)
    {
        var count = 0;
        for (var mask = 0x80; mask != 0 && (value & mask) == 0; mask >>= 1)
        {
            count++;
        }

        return count;
    }

    private static bool TryReadVint(byte[] buffer, ref int offset, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (offset >= buffer.Length)
        {
            return false;
        }

        var first = buffer[offset++];
        var zeroes = LeadingZeroBytes(first);
        if (zeroes > 7)
        {
            return false;
        }

        length = zeroes + 1;
        value = (ulong)(first & ((1 << (8 - length)) - 1));
        for (var i = 1; i < length; i++)
        {
            if (offset >= buffer.Length)
            {
                return false;
            }

            value = (value << 8) | buffer[offset++];
        }

        return true;
    }

    private static bool TryReadVint(byte[] buffer, ref int offset, out ulong value, out bool negative, out ulong bias)
    {
        // EBML lacing variant: the first vint is unsigned, the rest are signed.
        var start = offset;
        if (!TryReadVint(buffer, ref offset, out value, out var length))
        {
            negative = false;
            bias = 0;
            return false;
        }

        if (start == offset - length && length > 0)
        {
            // signed interpretation: bias is (2^(7*length-1) - 1)
            bias = (1UL << (7 * length - 1)) - 1;
            negative = value < bias;
        }
        else
        {
            bias = 0;
            negative = false;
        }

        return true;
    }

    private static ulong ReadUnsigned(FileStream stream, ulong size)
    {
        var length = (int)Math.Min(size, 8);
        ulong value = 0;
        for (var i = 0; i < length; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                break;
            }

            value = (value << 8) | (ulong)(byte)b;
        }

        return value;
    }

    private static string ReadString(FileStream stream, ulong size)
    {
        var length = (int)Math.Min(size, 4096);
        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    private static byte[] ReadBytes(FileStream stream, ulong size)
    {
        var length = (int)Math.Min(size, 1 << 20);
        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return buffer;
    }

    private static void Skip(FileStream stream, ulong size)
    {
        if (size == UnknownSize)
        {
            return;
        }

        stream.Position += (long)size;
    }
}
