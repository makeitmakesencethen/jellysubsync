using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Language-code normalisation and subtitle-kind helpers.
///
/// Jellyfin reports whatever the file carried: two-letter ISO 639-1 ("sv"),
/// three-letter 639-2/B ("swe", "chi"), 639-2/T ("zho", "deu"), regional forms
/// ("sv-SE") or occasionally a language name. Everything is folded onto one
/// canonical code so a language filter or a dropdown entry matches all of them.
/// </summary>
public static class LanguageSupport
{
    /// <summary>Canonical code used when a track has no language at all.</summary>
    public const string Unknown = "und";

    // canonical = aliases (2-letter, 639-2 variants, English names)
    private static readonly string[] Table =
    {
        "eng=en,english", "swe=sv,swedish", "nor=no,nb,nn,norwegian", "dan=da,danish",
        "fin=fi,finnish", "isl=is,ice,icelandic", "deu=de,ger,german", "fra=fr,fre,french",
        "spa=es,spanish", "ita=it,italian", "nld=nl,dut,dutch", "por=pt,portuguese",
        "rus=ru,russian", "pol=pl,polish", "ces=cs,cze,czech", "slk=sk,slo,slovak",
        "hun=hu,hungarian", "ron=ro,rum,romanian", "bul=bg,bulgarian", "hrv=hr,croatian",
        "srp=sr,serbian", "slv=sl,slovenian", "bos=bs,bosnian", "mkd=mk,mac,macedonian",
        "sqi=sq,alb,albanian", "ell=el,gre,greek", "tur=tr,turkish", "ara=ar,arabic",
        "heb=he,iw,hebrew", "fas=fa,per,persian", "urd=ur,urdu", "hin=hi,hindi",
        "ben=bn,bengali", "tam=ta,tamil", "tel=te,telugu", "mal=ml,malayalam",
        "kan=kn,kannada", "mar=mr,marathi", "guj=gu,gujarati", "pan=pa,punjabi",
        "sin=si,sinhala", "nep=ne,nepali", "zho=zh,chi,chinese", "jpn=ja,japanese",
        "kor=ko,korean", "vie=vi,vietnamese", "tha=th,thai", "ind=id,indonesian",
        "msa=ms,may,malay", "tgl=tl,fil,tagalog", "khm=km,khmer", "lao=lo,lao",
        "mya=my,bur,burmese", "mon=mn,mongolian", "bod=bo,tib,tibetan", "uzb=uz,uzbek",
        "kaz=kk,kazakh", "aze=az,azerbaijani", "hye=hy,arm,armenian", "kat=ka,geo,georgian",
        "ukr=uk,ukrainian", "bel=be,belarusian", "est=et,estonian", "lav=lv,latvian",
        "lit=lt,lithuanian", "eus=eu,baq,basque", "cat=ca,catalan", "glg=gl,galician",
        "cym=cy,wel,welsh", "gle=ga,irish", "afr=af,afrikaans", "swa=sw,swahili",
        "amh=am,amharic", "som=so,somali", "hau=ha,hausa", "yor=yo,yoruba",
        "zul=zu,zulu", "xho=xh,xhosa", "kur=ku,kurdish", "pus=ps,pashto",
        "und=unknown,unk"
    };

    private static readonly Dictionary<string, string> Aliases = BuildAliases();
    private static readonly Dictionary<string, string> Names = BuildNames();

    /// <summary>Normalises any reported language code onto its canonical form.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Unknown;
        }

        var code = raw.Trim().ToLowerInvariant();
        var dash = code.IndexOfAny(new[] { '-', '_' });
        if (dash > 0)
        {
            code = code.Substring(0, dash);
        }

        if (Aliases.TryGetValue(code, out var canonical))
        {
            return canonical;
        }

        if (code.Length > 2 && Aliases.TryGetValue(code.Substring(0, 2), out var shortForm))
        {
            return shortForm;
        }

        return code;
    }

    /// <summary>Friendly name for a canonical code (used in messages).</summary>
    public static string Label(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "All languages";
        }

        var canonical = Normalize(code);
        return Names.TryGetValue(canonical, out var name)
            ? name
            : canonical.ToUpperInvariant();
    }

    /// <summary>
    /// True when the track's language is allowed by the configured language filter.
    /// An empty filter allows everything; tracks with no language are skipped once a
    /// filter is set, because they cannot be matched by language.
    /// </summary>
    public static bool MatchesFilter(string? rawLanguage, IReadOnlyCollection<string>? filter)
    {
        if (filter is null || filter.Count == 0)
        {
            return true;
        }

        var normalized = Normalize(rawLanguage);
        if (normalized == Unknown)
        {
            return false;
        }

        foreach (var allowed in filter)
        {
            if (Normalize(allowed) == normalized)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for image-based subtitle codecs, which carry no text and can never be
    /// aligned: Blu-ray PGS, DVD VobSub, DVB, XSUB and bitmap variants.
    /// </summary>
    public static bool IsImageBased(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return false;
        }

        var c = codec.ToLowerInvariant();
        return c.Contains("pgs", StringComparison.Ordinal)
            || c.Contains("dvd", StringComparison.Ordinal)
            || c.Contains("vob", StringComparison.Ordinal)
            || c.Contains("xsub", StringComparison.Ordinal)
            || c.Contains("dvb", StringComparison.Ordinal)
            || c.Contains("bitmap", StringComparison.Ordinal)
            || c.Contains("hdmv", StringComparison.Ordinal);
    }

    /// <summary>Human-readable list of a language filter (for log messages).</summary>
    public static string Describe(IReadOnlyCollection<string>? filter)
    {
        if (filter is null || filter.Count == 0)
        {
            return "all languages";
        }

        return string.Join(" + ", filter.Select(Label).Distinct().OrderBy(x => x, StringComparer.Ordinal));
    }

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Table)
        {
            var split = entry.Split('=', 2);
            var canonical = split[0];
            map[canonical] = canonical;
            foreach (var alias in split[1].Split(','))
            {
                map[alias] = canonical;
            }
        }

        return map;
    }

    private static Dictionary<string, string> BuildNames()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Table)
        {
            var split = entry.Split('=', 2);
            var canonical = split[0];
            var alias = split[1].Split(',').FirstOrDefault(a => a.Length > 3 && a.All(char.IsLetter));
            map[canonical] = alias is null
                ? canonical.ToUpperInvariant()
                : char.ToUpperInvariant(alias[0]) + alias.Substring(1);
        }

        map[Unknown] = "Unknown";
        return map;
    }
}
