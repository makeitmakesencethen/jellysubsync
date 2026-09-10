import re

# ------------------------------------------------------------------ SpeechCache
p = 'Jellyfin.Plugin.SubSync/Services/SpeechCache.cs'
s = open(p).read()


def rep(old, new, path=None):
    global s
    assert old in s, 'NOT FOUND: ' + old[:80]
    s = s.replace(old, new, 1)


rep("""    /// <summary>Gets the directory holding cached speech signals.</summary>""",
    """    /// <summary>Cache entries older than this are pruned when a new entry is written.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Cache size cap; oldest entries are dropped beyond this.</summary>
    public const long MaxBytes = 250L * 1024 * 1024;

    /// <summary>Gets the directory holding cached speech signals.</summary>""")

rep("""    /// <summary>Human-readable cache size, for logs and the status line.</summary>""",
    """    /// <summary>
    /// Drops entries that are older than <see cref="MaxAge"/>, then the oldest entries
    /// until the cache fits in <see cref="MaxBytes"/>. Runs after every new entry, so
    /// orphans left behind by replaced/re-encoded media cannot pile up forever.
    /// </summary>
    /// <returns>Number of entries removed.</returns>
    public static int Prune()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Root))
            {
                return 0;
            }

            var cutoff = DateTime.UtcNow - MaxAge;
            var entries = new List<FileInfo>();
            foreach (var file in Directory.EnumerateFiles(Root, "*.npz"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff || info.LastAccessTimeUtc < cutoff)
                {
                    try
                    {
                        info.Delete();
                        removed++;
                        continue;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                }

                entries.Add(info);
            }

            long total = 0;
            foreach (var entry in entries)
            {
                total += entry.Length;
            }

            if (total <= MaxBytes)
            {
                return removed;
            }

            foreach (var entry in entries.OrderBy(e => e.LastWriteTimeUtc))
            {
                if (total <= MaxBytes)
                {
                    break;
                }

                try
                {
                    total -= entry.Length;
                    entry.Delete();
                    removed++;
                }
                catch (IOException)
                {
                    continue;
                }
            }
        }
        catch (Exception)
        {
            // Pruning is best effort; a failed pass must never break a sync.
        }

        return removed;
    }

    /// <summary>Deletes every cached entry (Settings → "Clear speech cache").</summary>
    /// <returns>Number of entries removed.</returns>
    public static int Clear()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Root))
            {
                return 0;
            }

            foreach (var file in Directory.GetFiles(Root))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    continue;
                }
            }
        }
        catch (Exception)
        {
            // Best effort.
        }

        return removed;
    }

    /// <summary>Human-readable cache size, for logs and the status line.</summary>""")

# prune after each freshly written entry
rep("""                SpeechCache.Harvest(referencePath, speechKey);
                SpeechCache.DropLink(speechKey);""",
    """                SpeechCache.Harvest(referencePath, speechKey);
                SpeechCache.DropLink(speechKey);
                SpeechCache.Prune();""")

open(p, 'w').write(s)
print('SpeechCache ok')
