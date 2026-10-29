using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Vesper.Core;

public sealed record SongNameQuery(string Title, string ArtistHint = "");

public static class SongNameMatching
{
    private const string Noise = @"\b(?:live|concert|official|mv|mtv|ktv|hd|uhd|flac|mp3|aac|hevc|avc|h\.?26[45]|\d{3,4}[pi]|[248]k)\b|现场|演唱会|伴奏|原伴|原唱|修复|高清|超清|无损|蓝光|字幕|国语|粤语";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        StringBuilder destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);

    public static string CleanTitle(string value)
    {
        string text = Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\.(?:mkv|mp4|m4v|avi|wmv|mov|mp3|m4a|flac|wav)$", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[\[【(（]([^\]】)）]*)[\]】)）]", match =>
            Regex.IsMatch(match.Groups[1].Value, Noise, RegexOptions.IgnoreCase) ? " " : match.Value);
        text = Regex.Replace(text, @"^\s*\d{1,5}[._、]\s*", "");
        text = Regex.Replace(text, @"[-_]\d{5,}\s*$", "");
        text = Regex.Replace(text, @"\[\s*\]", "");
        // 中文名称后常见的裸 Live/MV 后缀；英文标题本身的 Live、Live Forever 等保留。
        string suffix = @"(?:\s*[-_—–]+\s*|\s+)(?:live(?:\s+(?:version|版|at\b.*|in\b.*))?|现场版?|演唱会版?|official(?:\s+music\s+video)?|music\s+video|原MV原伴|原MV|原伴|无原唱|原唱|伴奏|mtv|mv|ktv|\d{3,4}[pi]|[248]k(?:修复)?|高清|超清|无损|国语|粤语)\s*$";
        for (int i = 0; i < 6; i++)
        {
            var match = Regex.Match(text, suffix, RegexOptions.IgnoreCase);
            if (!match.Success) break;
            bool bareEnglishLive = !Regex.IsMatch(text, @"[\p{IsCJKUnifiedIdeographs}]")
                && !Regex.IsMatch(match.Value, @"[-_—–]") && match.Value.Trim().Equals("live", StringComparison.OrdinalIgnoreCase);
            if (bareEnglishLive) break;
            text = text[..match.Index].Trim(' ', '-', '_', '—', '–');
        }
        return Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '_', '—', '–');
    }

    public static IReadOnlyList<SongNameQuery> Plan(string fileName, string knownTitle = "", string knownArtist = "")
    {
        var queries = new List<SongNameQuery>();
        void Add(string title, string artist)
        {
            title = CleanTitle(title).Trim('《', '》', '「', '」', '[', ']', '【', '】');
            if (title.Length == 0 || !title.Any(char.IsLetterOrDigit)) return;
            if (!queries.Any(query => Normalize(query.Title) == Normalize(title))) queries.Add(new(title, artist.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(knownTitle)) Add(knownTitle, knownArtist);
        string cleaned = CleanTitle(fileName);
        var quoted = Regex.Match(cleaned, "[《「](.*?)[》」]");
        if (quoted.Success)
            Add(quoted.Groups[1].Value, knownArtist.Length > 0 ? knownArtist : cleaned[..quoted.Index].Trim(' ', '-', '_'));
        var parts = Regex.Split(cleaned, @"\s+[-—–]+\s+|_+|(?<=[\p{IsCJKUnifiedIdeographs}])[-—–]+|[-—–]+(?=[\p{IsCJKUnifiedIdeographs}])")
            .Select(CleanTitle).Where(part => part.Length > 0).ToArray();
        if (parts.Length == 2)
        {
            Add(parts[1], knownArtist.Length > 0 ? knownArtist : parts[0]);
            Add(parts[0], knownArtist.Length > 0 ? knownArtist : parts[1]);
        }
        else if (parts.Length > 2)
            for (int index = parts.Length - 1; index >= 0; index--)
                Add(parts[index], knownArtist.Length > 0 ? knownArtist : string.Join(" / ", parts.Where((_, i) => i != index)));
        else if (parts.Length == 1 && cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, var second])
        {
            // 空格也可能属于完整歌名，完整名称先查，两种分词只作为后备线索。
            Add(cleaned, knownArtist); Add(second, first); Add(first, second);
        }
        else Add(cleaned, knownArtist);
        return queries.Take(4).ToList();
    }

    public static string Normalize(string value)
    {
        string text = CleanTitle(value).Normalize(NormalizationForm.FormKC);
        if (OperatingSystem.IsWindows())
        {
            var mapped = new StringBuilder(Math.Max(8, text.Length * 2 + 2));
            if (LCMapStringEx("zh-CN", 0x02000000, text, -1, mapped, mapped.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) > 0)
                text = mapped.ToString();
        }
        if (!text.Any(char.IsLetter)) return string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        return string.Concat(text.Normalize(NormalizationForm.FormD).Where(character => char.IsLetterOrDigit(character)
            && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)).ToUpperInvariant();
    }

    public static bool Same(string first, string second) => Normalize(first) is { Length: > 0 } value && value == Normalize(second);
    public static bool SameArtist(string first, string second)
    {
        if (Same(first, second)) return true;
        string[] separators = [" / ", " & ", ", ", "、", " feat. "];
        return first.Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(artist => second.Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(other => Same(artist, other)));
    }
    public static bool IsLive(string value) => Regex.IsMatch(value, @"(?<![a-z])live(?![a-z])|现场|演唱会|演唱會", RegexOptions.IgnoreCase);

    public static SongMatch? ChooseMetadata(IEnumerable<SongMatch> candidates, IReadOnlyList<SongNameQuery> queries,
        string knownArtist, string fileName, IEnumerable<LyricsCandidate>? lyrics = null)
    {
        var exact = candidates.Where(candidate => candidate.Artist.Length > 0 && queries.Any(query => Same(candidate.Title, query.Title)))
            .DistinctBy(candidate => (candidate.RecordingId, candidate.ReleaseId)).ToList();
        if (exact.Count == 0) return null;
        var artists = exact.GroupBy(candidate => Normalize(candidate.Artist)).ToList();
        var lyricGroups = (lyrics ?? []).Where(candidate => queries.Any(query => Same(candidate.Title, query.Title)))
            .GroupBy(candidate => Normalize(candidate.Artist)).OrderByDescending(group => group.Count()).ToList();
        int Rank(IGrouping<string, SongMatch> group)
        {
            string artist = group.First().Artist;
            if (knownArtist.Length > 0) return SameArtist(artist, knownArtist) ? 100 : -1;
            if (group.Any(candidate => queries.Any(query => Same(candidate.Title, query.Title)
                && query.ArtistHint.Length > 0 && SameArtist(artist, query.ArtistHint)))) return 90;
            if (Normalize(artist).Length >= 2 && group.Any(candidate => Normalize(fileName)
                .Replace(Normalize(candidate.Title), "", StringComparison.Ordinal).Contains(Normalize(artist)))) return 80;
            if (lyricGroups.FirstOrDefault() is { } dominant && dominant.Count() >= 3
                && dominant.Count() >= lyricGroups.Sum(group => group.Count()) * .8 && dominant.Key == Normalize(artist)) return 60;
            return artists.Count == 1 ? 40 : 0;
        }
        var ranked = artists.Select(group => (Group: group, Rank: Rank(group))).OrderByDescending(value => value.Rank).ToList();
        if (ranked[0].Rank <= 0 || ranked.Count > 1 && ranked[0].Rank == ranked[1].Rank) return null;
        return ranked[0].Group.OrderBy(candidate => IsLive(candidate.Title + " " + candidate.Album + " " + candidate.Version))
            .ThenBy(candidate => candidate.ReleaseStatus.Length > 0 && candidate.ReleaseStatus != "Official")
            .ThenBy(candidate => candidate.ReleaseType == "Album" ? 0 : candidate.ReleaseType == "Single" ? 1 : 2)
            .ThenBy(candidate => candidate.Year.Length > 0 ? candidate.Year : "9999")
            .ThenBy(candidate => candidate.ReleaseId, StringComparer.Ordinal).First();
    }
}
