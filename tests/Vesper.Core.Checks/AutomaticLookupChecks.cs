using System.Net;
using System.Text;
using Vesper.App;
using Vesper.Core;

internal static class AutomaticLookupChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var plan = SongNameMatching.Plan("001_Beyond_真的爱你 (Live) [1080P].mkv");
        check(plan.Any(query => query.Title == "真的爱你" && query.ArtistHint == "Beyond")
            && !plan.Any(query => query.Title.Contains("Live") || query.Title.Contains("1080P")),
            "文件名自动识别去掉序号、格式、画质与 Live 后缀，提取歌名及艺人线索");
        var reversed = SongNameMatching.Plan("真的爱你-Beyond-Live.mkv");
        check(reversed.Any(query => query.Title == "真的爱你" && query.ArtistHint == "Beyond")
            && SongNameMatching.Plan("Beyond《真的愛你》 4K.mkv").First().Title == "真的愛你",
            "同时支持歌名在前、艺人在前、书名号及无空格连接符，不固定假设命名顺序");
        check(SongNameMatching.CleanTitle("Live Forever") == "Live Forever"
            && SongNameMatching.CleanTitle("Love Live") == "Love Live"
            && SongNameMatching.Plan("1984.mp3").Single().Title == "1984",
            "清理后缀时保留英文标题本身的 Live 和纯数字歌名");
        check(!OperatingSystem.IsWindows() || SongNameMatching.Same("真的愛你", "真的爱你"),
            "支持系统语言转换能力下的简繁歌名核对");
        check(SongNameMatching.Plan("-爱情买卖-国语-571673.mkv").First().Title == "爱情买卖"
            && SongNameMatching.Plan("[]林林--柔柔的眼波柔柔的你--国语--原MV原伴.mkv").Any(query => query.Title == "柔柔的眼波柔柔的你")
            && SongNameMatching.Plan("183 Club_七朵花-闭上眼默念3遍(MTV)[国语].mkv").First().Title == "闭上眼默念3遍",
            "实际命名结构中的语言、曲库编号、原MV原伴、MTV与多艺人分隔符均可作为噪声清理");

        SongMatch studio = new("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
            "真的愛你", "Beyond", "BEYOND IV", "1989", "", "Album", "Official");
        var live = studio with { ReleaseId = "33333333-3333-3333-3333-333333333333", Album = "Beyond Live", Year = "2023", Version = "live" };
        var queryLog = new List<string>();
        var lyric = new LyricsCandidate(42, "真的爱你", "Beyond", "BEYOND IV", 277, "测试第一行\n测试第二行", "[00:01.00]测试第一行");
        var service = new AutomaticSongLookup((title, _) =>
        {
            queryLog.Add(title);
            return Task.FromResult<IReadOnlyList<SongMatch>>([live, studio]);
        }, (match, _) => Task.FromResult(new SongMetadata(match.Title, match.Artist, match.Album, Year: "1989",
            SourceUrl: "https://musicbrainz.org/recording/" + match.RecordingId)),
        (_, _) => Task.FromResult<IReadOnlyList<LyricsCandidate>>([lyric]));
        var input = new SongLookupInput("Beyond - 真的爱你 (Live).mkv", new(Local: new(DurationSeconds: 277)));
        var result = await service.LookupAsync(input);
        check(queryLog.First() == "真的爱你" && result.Metadata?.Artist == "Beyond" && result.Metadata.Album == "BEYOND IV"
            && result.MetadataOutcome == LookupOutcome.Matched,
            "自动按歌名查资料，以文件名艺人辅助确认，并优先正式录音室发行而非首条现场合辑");
        check(result.Lyrics?.PlainText.Contains("测试第二行") == true && !result.Lyrics.TimingCompatible,
            "自动获取歌词正文，现场文件与录音室版即使同长也不直接认证时间轴");
        var partialService = new AutomaticSongLookup((_, _) => Task.FromResult<IReadOnlyList<SongMatch>>([studio]),
            (_, _) => throw new TaskCanceledException("secondary metadata timeout"),
            (_, _) => Task.FromResult<IReadOnlyList<LyricsCandidate>>([lyric]));
        var partial = await partialService.LookupAsync(input);
        check(partial.Metadata?.Artist == "Beyond" && partial.Lyrics is not null,
            "发行详情接口超时时保留已确认的基本资料和成功取得的歌词，不丢弃整次结果");
        var noArtistInput = new SongLookupInput("真的爱你.mkv", new());
        var ambiguous = new AutomaticSongLookup((_, _) => Task.FromResult<IReadOnlyList<SongMatch>>(
            [studio, studio with { RecordingId = "44444444-4444-4444-4444-444444444444", Artist = "其他艺人" }]), (_, _) => throw new Exception("不应自动取资料"),
            (_, _) => Task.FromResult<IReadOnlyList<LyricsCandidate>>([lyric, lyric with { Id = 43, Artist = "其他艺人" }]));
        var undecided = await ambiguous.LookupAsync(noArtistInput);
        check(undecided.Metadata is null && undecided.Lyrics is null && undecided.MetadataOutcome == LookupOutcome.NeedsChoice
            && undecided.LyricsOutcome == LookupOutcome.NeedsChoice,
            "无艺人线索且同名多艺人有歧义时保留待处理，不随机采用第一项");
        var unavailable = new AutomaticSongLookup((_, _) => throw new HttpRequestException("offline"),
            (_, _) => throw new Exception("不应取资料"), (_, _) => throw new HttpRequestException("offline"));
        var failed = await unavailable.LookupAsync(noArtistInput);
        check(failed.MetadataOutcome == LookupOutcome.Unavailable && failed.LyricsOutcome == LookupOutcome.Unavailable,
            "服务异常保留可重试状态，不写成永久无匹配");
        var empty = new AutomaticSongLookup((_, _) => Task.FromResult<IReadOnlyList<SongMatch>>([]),
            (_, _) => throw new Exception("不应取资料"), (_, _) => Task.FromResult<IReadOnlyList<LyricsCandidate>>([]));
        var missing = await empty.LookupAsync(noArtistInput);
        check(missing.MetadataOutcome == LookupOutcome.NotFound && missing.LyricsOutcome == LookupOutcome.NotFound,
            "确实无结果时自动结束，保留曲目供用户稍后决定");

        await CheckLyricsClientAsync(check);
        await CheckPersistenceAsync(root, input, result, check);
        using var limitedHandler = new LimitedLyricsHandler();
        using var limitedHttp = new HttpClient(limitedHandler);
        var limitedClient = new LrcLibClient(limitedHttp);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { await limitedClient.SearchAsync("测试限流"); }
            catch (HttpRequestException) { }
        }
        check(limitedHandler.Requests == 1, "歌词服务要求等待重试时遵守 Retry-After，期间不重复发送请求");
    }

    private static async Task CheckLyricsClientAsync(Action<bool, string> check)
    {
        using var http = new HttpClient(new LyricsHandler());
        var entries = await new LrcLibClient(http).SearchAsync("歌名");
        var chosen = LrcLibClient.Choose(entries, [new("歌名")], "艺人", "专辑", 200)!;
        var lyrics = LrcLibClient.ToLyrics(chosen, 200, "歌名.mp3");
        check(entries.Count == 2 && chosen.Id == 12 && lyrics.PlainText == "测试一\n测试二" && lyrics.TimingCompatible,
            "在线歌词解析按歌名、艺人和时长核对，只有 LRC 时去除时间标签供详情阅读");
        check(LrcLibClient.Choose(entries, [new("相似歌名")], "艺人", "", 200) is null
            && LrcLibClient.Choose(entries, [new("歌名")], "别的艺人", "", 200) is null,
            "歌词不接受相似歌名或不符艺人，避免错误绑定");
    }

    private static async Task CheckPersistenceAsync(string root, SongLookupInput original, SongLookupResult result, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "automatic-media"), path = Path.Combine(root, "automatic-state.json");
        Directory.CreateDirectory(folder);
        string media = Path.Combine(folder, original.FileName);
        await File.WriteAllTextAsync(media, "synthetic video");
        using var model = new MainViewModel(path);
        await model.InitializeAsync(); await model.AddDirectoryAsync(folder);
        var row = model.Results.Single();
        model.ApplyLocalMetadata(row, media, original.Details.Local!);
        var input = model.GetLookupInput(row.Song.Id)!;
        await model.SaveAutomaticLookupAsync(row.Song.Id, input, result);
        check(row.HasLyrics && row.Lyrics.Contains("测试第二行") && !model.GetLookupInput(row.Song.Id)!.ShouldLookup(DateTimeOffset.UtcNow),
            "自动资料与歌词保存后即时显示，重复打开复用缓存，不重复请求");
        using var reopened = new MainViewModel(path);
        await reopened.InitializeAsync();
        check(reopened.Results.Single().HasLyrics && reopened.Results.Single().Album == "BEYOND IV",
            "重启离线也能读取缓存的联网资料与歌词正文");
        await model.SaveCorrectionsAsync(row.Song.Id, "用户新歌名", "用户艺人", "用户专辑");
        check(!await model.SaveAutomaticLookupAsync(row.Song.Id, input, result)
            && row.Title == "用户新歌名" && row.ArtistDetail == "用户艺人" && !row.HasLyrics,
            "手动更正曲目身份后丢弃过期自动结果，并清除旧曲目歌词关联");
        var changed = model.GetLookupInput(row.Song.Id)!;
        var changedResult = result with { Metadata = result.Metadata! with { Title = "用户新歌名", Artist = "用户艺人" } };
        Directory.CreateDirectory(path + ".tmp");
        check(!await model.SaveAutomaticLookupAsync(row.Song.Id, changed, changedResult) && !row.HasLyrics
            && model.GetLookupInput(row.Song.Id)!.ShouldLookup(DateTimeOffset.UtcNow),
            "自动缓存写入失败回退歌词和检查状态，下次仍可重试");
        var now = DateTimeOffset.UtcNow;
        var retry = original with { Details = original.Details with { Lookup = new(original.Key, now, LookupOutcome.Unavailable, LookupOutcome.Unavailable) } };
        check(!retry.ShouldLookup(now.AddSeconds(20)) && retry.ShouldLookup(now.AddMinutes(2)),
            "服务失败后有重试间隔，不因前进后退反复轰炸接口");
    }

    private sealed class LyricsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.Query.Contains("track_name=")) throw new InvalidOperationException("必须先按歌名搜索");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [{"id":11,"trackName":"歌名","artistName":"翻唱艺人","albumName":"专辑","duration":200,"plainLyrics":"其他测试文本"},
                     {"id":12,"trackName":"歌名","artistName":"艺人","albumName":"专辑","duration":200,"plainLyrics":null,"syncedLyrics":"[00:01.00]测试一\n[00:02.00]测试二"}]
                    """, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class LimitedLyricsHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        }
    }
}
