using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Reads text subtitle tracks straight out of an MP4/MOV file through its sample table
/// (<c>stbl</c>), instead of demuxing the whole file.
///
/// mp4 stores an index per track: sample sizes, chunk offsets and durations. Following it
/// means seeking to just the subtitle samples (a few kilobytes) rather than walking every
/// cluster — the same win the Matroska reader gets from <c>Cues</c>. This matters most for
/// large files on slow storage, where ffmpeg otherwise reads the entire file for each
/// extraction.
///
/// Only self-describing text sample formats are handled (<c>tx3g</c>/<c>mov_text</c> and
/// <c>text</c>). Anything else — <c>c608</c>, <c>stpp</c> (TTML), image codecs, bitstream
/// subtitle tracks on fragmented files — returns false so the caller falls back to ffmpeg.
/// </summary>
public static class Mp4SubtitleExtractor
{
    /// <summary>Cheap pre-check: does this path look like an ISO base media file?</summary>
    /// <param name="path">Candidate media path.</param>
    /// <returns>True when the extension and the first box look like MP4/MOV.</returns>
    public static bool LooksLikeMp4(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) != 8)
            {
                return false;
            }

            // First box is normally ftyp for a valid file.
            return header[4] == (byte)'f' && header[5] == (byte)'t'
                && header[6] == (byte)'y' && header[7] == (byte)'p';
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts one embedded text subtitle track to SRT text.
    /// </summary>
    /// <param name="path">Media file.</param>
    /// <param name="subtitleOrdinal">0-based index among the file's subtitle tracks.</param>
    /// <param name="srtText">Extracted subtitles on success.</param>
    /// <param name="reason">Why extraction was skipped, on failure.</param>
    /// <returns>True when a complete SRT was produced.</returns>
    public static bool TryExtract(string path, int subtitleOrdinal, out string srtText, out string reason)
    {
        srtText = string.Empty;
        reason = string.Empty;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var moov = FindBox(stream, 0, stream.Length, "moov");
            if (moov is null)
            {
                reason = "no moov box";
                return false;
            }

            var tracks = ReadSubtitleTracks(stream, moov.Value);
            if (tracks.Count == 0)
            {
                reason = "no subtitle tracks";
                return false;
            }

            if (subtitleOrdinal < 0 || subtitleOrdinal >= tracks.Count)
            {
                reason = $"subtitle ordinal {subtitleOrdinal} out of range ({tracks.Count} tracks)";
                return false;
            }

            var track = tracks[subtitleOrdinal];
            if (track.Codec is not ("tx3g" or "text"))
            {
                // c608/c708 (CEA-608), stpp (TTML), wvtt (WebVTT-in-MP4) and image codecs
                // need the demuxer's interpretation — hand those to ffmpeg.
                reason = $"codec {track.Codec} needs ffmpeg";
                return false;
            }

            if (track.SampleSizes.Count == 0 || track.ChunkOffsets.Count == 0)
            {
                reason = "empty sample table";
                return false;
            }

            var entries = ReadSamples(stream, track);
            if (entries.Count == 0)
            {
                reason = "no subtitle samples decoded";
                return false;
            }

            entries.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
            srtText = SrtWriter.Render(entries);
            return srtText.Length > 0;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private sealed class Track
    {
        public string Codec { get; set; } = string.Empty;

        public long Timescale { get; set; } = 1000;

        public List<long> SampleSizes { get; } = new();

        public List<long> ChunkOffsets { get; } = new();

        public List<(long FirstChunk, long SamplesPerChunk)> Stsc { get; } = new();

        public List<(long Count, long Delta)> Stts { get; } = new();
    }

    private static List<Track> ReadSubtitleTracks(FileStream stream, (long Start, long End) moov)
    {
        var result = new List<Track>();
        foreach (var trak in FindChildren(stream, moov, "trak"))
        {
            var mdia = FindBox(stream, trak.Start, trak.End, "mdia");
            if (mdia is null)
            {
                continue;
            }

            var handler = FindBox(stream, mdia.Value.Start, mdia.Value.End, "hdlr");
            if (handler is null)
            {
                continue;
            }

            stream.Position = handler.Value.Start + 8; // version/flags + predefined
            var handlerType = ReadFourCc(stream);
            var isSubtitle = handlerType is "sbtl" or "subt" or "text" or "clcp" or "subp";
            if (!isSubtitle)
            {
                continue;
            }

            var track = new Track();
            var mdhd = FindBox(stream, mdia.Value.Start, mdia.Value.End, "mdhd");
            if (mdhd is not null)
            {
                track.Timescale = ReadMdhdTimescale(stream, mdhd.Value);
            }

            var minf = FindBox(stream, mdia.Value.Start, mdia.Value.End, "minf");
            var stbl = minf is null ? null : FindBox(stream, minf.Value.Start, minf.Value.End, "stbl");
            if (stbl is null)
            {
                continue;
            }

            ReadStbl(stream, stbl.Value, track);
            result.Add(track);
        }

        return result;
    }

    private static long ReadMdhdTimescale(FileStream stream, (long Start, long End) mdhd)
    {
        stream.Position = mdhd.Start;
        var version = stream.ReadByte();
        stream.Position = mdhd.Start + (version == 1 ? 20 : 12);
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static void ReadStbl(FileStream stream, (long Start, long End) stbl, Track track)
    {
        // stsd: which sample format the track uses
        var stsd = FindBox(stream, stbl.Start, stbl.End, "stsd");
        if (stsd is not null)
        {
            stream.Position = stsd.Value.Start + 4; // version/flags
            stream.Position += 4;                   // entry count
            stream.Position += 4;                   // entry size
            track.Codec = ReadFourCc(stream);
        }

        // stsz: per-sample sizes (or one shared size)
        var stsz = FindBox(stream, stbl.Start, stbl.End, "stsz");
        if (stsz is not null)
        {
            stream.Position = stsz.Value.Start + 4;
            Span<byte> buffer = stackalloc byte[8];
            stream.ReadExactly(buffer);
            var sharedSize = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
            var count = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]);
            if (count > 5_000_000)
            {
                return; // implausible — let ffmpeg deal with it
            }

            for (var i = 0; i < count; i++)
            {
                if (sharedSize > 0)
                {
                    track.SampleSizes.Add(sharedSize);
                    continue;
                }

                stream.ReadExactly(buffer[..4]);
                track.SampleSizes.Add(BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]));
            }
        }

        // stsc: samples per chunk
        var stsc = FindBox(stream, stbl.Start, stbl.End, "stsc");
        if (stsc is not null)
        {
            stream.Position = stsc.Value.Start + 4;
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            var count = (long)BinaryPrimitives.ReadUInt32BigEndian(buffer);
            for (var i = 0L; i < count && i < 1_000_000; i++)
            {
                var entry = new byte[12];
                stream.ReadExactly(entry);
                track.Stsc.Add((
                    BinaryPrimitives.ReadUInt32BigEndian(entry[..4]),
                    BinaryPrimitives.ReadUInt32BigEndian(entry[4..8])));
            }
        }

        // stco / co64: chunk offsets
        var stco = FindBox(stream, stbl.Start, stbl.End, "stco");
        var co64 = stco is null ? FindBox(stream, stbl.Start, stbl.End, "co64") : null;
        var offsetsBox = stco ?? co64;
        if (offsetsBox is not null)
        {
            var wide = stco is null;
            stream.Position = offsetsBox.Value.Start + 4;
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            var count = (long)BinaryPrimitives.ReadUInt32BigEndian(buffer);
            for (var i = 0L; i < count && i < 5_000_000; i++)
            {
                var entry = new byte[8];
                stream.ReadExactly(wide ? entry : entry.AsSpan(0, 4));
                track.ChunkOffsets.Add(wide
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry)
                    : BinaryPrimitives.ReadUInt32BigEndian(entry[..4]));
            }
        }

        // stts: durations
        var stts = FindBox(stream, stbl.Start, stbl.End, "stts");
        if (stts is not null)
        {
            stream.Position = stts.Value.Start + 4;
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            var count = (long)BinaryPrimitives.ReadUInt32BigEndian(buffer);
            for (var i = 0L; i < count && i < 1_000_000; i++)
            {
                var entry = new byte[8];
                stream.ReadExactly(entry);
                track.Stts.Add((
                    BinaryPrimitives.ReadUInt32BigEndian(entry[..4]),
                    BinaryPrimitives.ReadUInt32BigEndian(entry[4..8])));
            }
        }
    }

    private static List<SrtWriter.Entry> ReadSamples(FileStream stream, Track track)
    {
        var entries = new List<SrtWriter.Entry>();
        var durations = ExpandDurations(track);
        var sampleIndex = 0;

        for (var chunk = 0; chunk < track.ChunkOffsets.Count && sampleIndex < track.SampleSizes.Count; chunk++)
        {
            var samplesPerChunk = SamplesPerChunk(track, chunk + 1);
            long offset = track.ChunkOffsets[chunk];
            for (var i = 0; i < samplesPerChunk && sampleIndex < track.SampleSizes.Count; i++)
            {
                var size = track.SampleSizes[sampleIndex];
                if (size > 0 && size < 1 << 22)
                {
                    var buffer = new byte[size];
                    stream.Position = offset;
                    var read = stream.Read(buffer, 0, (int)size);
                    if (read == (int)size)
                    {
                        var text = DecodeSample(buffer);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var startTicks = durations.Count > sampleIndex ? durations[sampleIndex].Start : 0;
                            var durationTicks = durations.Count > sampleIndex ? durations[sampleIndex].Duration : 2000;
                            var startMs = startTicks * 1000 / Math.Max(1, track.Timescale);
                            var endMs = (startTicks + durationTicks) * 1000 / Math.Max(1, track.Timescale);
                            entries.Add(new SrtWriter.Entry(startMs, endMs, text));
                        }
                    }
                }

                offset += size;
                sampleIndex++;
            }
        }

        return entries;
    }

    private static List<(long Start, long Duration)> ExpandDurations(Track track)
    {
        var result = new List<(long, long)>();
        long cursor = 0;
        foreach (var (count, delta) in track.Stts)
        {
            for (long i = 0; i < count && result.Count < 5_000_000; i++)
            {
                result.Add((cursor, delta));
                cursor += delta;
            }
        }

        return result;
    }

    private static int SamplesPerChunk(Track track, long oneBasedChunk)
    {
        var samples = 1;
        foreach (var (firstChunk, perChunk) in track.Stsc)
        {
            if (oneBasedChunk >= firstChunk)
            {
                samples = (int)perChunk;
            }
            else
            {
                break;
            }
        }

        return samples <= 0 ? 1 : samples;
    }

    private static string DecodeSample(byte[] sample)
    {
        // tx3g / text samples: uint16 text length, then the text itself.
        if (sample.Length < 3)
        {
            return string.Empty;
        }

        var declared = BinaryPrimitives.ReadUInt16BigEndian(sample.AsSpan(0, 2));
        var available = sample.Length - 2;
        var length = declared > 0 && declared <= available ? declared : available;
        var payload = sample.AsSpan(2, length);
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        // UTF-16 when a BOM is present, otherwise UTF-8 (the common case).
        string text;
        if (payload.Length >= 2 && payload[0] == 0xFE && payload[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(payload[2..]);
        }
        else if (payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(payload[2..]);
        }
        else
        {
            text = Encoding.UTF8.GetString(payload);
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    // ------------------------------------------------------------------ box helpers

    private static (long Start, long End)? FindBox(FileStream stream, long start, long end, string type)
    {
        foreach (var box in FindChildren(stream, (start, end), type))
        {
            return box;
        }

        return null;
    }

    private static IEnumerable<(long Start, long End)> FindChildren(FileStream stream, (long Start, long End) parent, string type)
    {
        var position = parent.Start;
        while (position + 8 <= parent.End)
        {
            stream.Position = position;
            var header = new byte[8];
            if (stream.Read(header, 0, 8) != 8)
            {
                yield break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var boxType = Encoding.ASCII.GetString(header, 4, 4);
            long headerSize = 8;
            if (size == 1)
            {
                var large = new byte[8];
                if (stream.Read(large, 0, 8) != 8)
                {
                    yield break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(large);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = parent.End - position;
            }

            if (size < headerSize || position + size > parent.End)
            {
                yield break;
            }

            if (boxType == type)
            {
                yield return (position + headerSize, position + size);
            }

            position += size;
        }
    }

    private static string ReadFourCc(FileStream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (stream.Read(buffer) != 4)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(buffer);
    }
}
