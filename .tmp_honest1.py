"""Make extraction progress honest, and make the storage the suspect measurable.

Two defects, one of them the reason a claim of mine could not be checked:

1. The progress line printed stats that are only filled in after the extraction finishes, so it
   always said "0.0 MB, 0 reads" while it was reading. The counters are now live.
2. Nothing said where the time went. Reading 1.3 MB in 342 small reads is a latency question, so
   the line now carries milliseconds per read, reads per second and subtitles per second, read
   from the kernel (/proc/self/io) when available rather than from our own bookkeeping.

With three workers on one disk, "12 ms/read" versus "50 ms/read" is the difference between a
fast disk and three heads fighting over one, and it is now visible per worker.
"""
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Services/MkvSubtitleExtractor.cs'
s = open(p).read()


def rep(old, new, label):
    global s
    assert old in s, 'NOT FOUND: ' + label
    s = s.replace(old, new, 1)


# --- kernel-level I/O counters -------------------------------------------------
rep("""    /// <summary>Subtitle blocks found.</summary>""",
"""    /// <summary>
    /// Bytes and read calls as the kernel sees them (<c>/proc/self/io</c>), when readable.
    ///
    /// The reader's own counters describe what it asked for; these describe what happened, which
    /// is what a claim about cost has to rest on.
    /// </summary>
    /// <returns>Bytes read and read syscalls, or (-1, -1) when unavailable.</returns>
    public static (long Bytes, long Calls) KernelIo()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (-1, -1);
        }

        try
        {
            long bytes = -1;
            long calls = -1;
            foreach (var line in File.ReadLines("/proc/self/io"))
            {
                if (line.StartsWith("rchar:", StringComparison.Ordinal))
                {
                    bytes = long.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("syscr:", StringComparison.Ordinal))
                {
                    calls = long.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
                }
            }

            return (bytes, calls);
        }
        catch
        {
            return (-1, -1);
        }
    }

    /// <summary>Subtitle blocks found.</summary>""", 'kernel io helper')

open(p, 'w').write(s)
print('kernel io helper added')
