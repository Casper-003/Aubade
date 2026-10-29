using Vesper.Core;
using Vesper.App;

internal static class LyricsDisplayChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string raw = "[id:0]\r\n[hash:]\r\n[sign:测试服务标记]\r\n[total:210774]\r\n测试第一行\r\n\r\n测试第二行";
        check(LyricsDisplay.Clean(raw) == "测试第一行\n\n测试第二行", "清除截图中的 id/hash/sign/total 元数据，保留歌词和段落");
        check(LyricsDisplay.Clean("[00:01.00][00:03.45]测试正文\n[Chorus]\n普通 id: 文本") == "测试正文\n[Chorus]\n普通 id: 文本",
            "连续时间标签可去除，正常段落标记和正文中的冒号不误删");
        var lyrics = LrcLibClient.ToLyrics(new(1, "测试", "艺人", "", 200, raw, "[00:01]原始时间轴"), 200, "测试.mp3");
        var saved = new SongDetails(OnlineLyrics: lyrics);
        var row = new LibrarySongItem(new("id", "测试", []));
        row.ApplyDetails(new(Local: new(Lyrics: raw)));
        check(row.Lyrics == "测试第一行\n\n测试第二行" && saved.OnlineLyrics!.SyncedLyrics == "[00:01]原始时间轴",
            "旧缓存与本地歌词同样清理，原始时间轴保持原样");
        check(LyricsDisplay.Clean("[hash:]\n[id:0]").Length == 0, "只有元数据时不冒充有效歌词正文");
    }
}
