using Vesper.App;
using Vesper.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

internal static class OverviewChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        byte[] pixels = [0, 0, 0, 255, 255, 255, 255, 255, 40, 80, 190, 255, 42, 82, 192, 255, 255, 0, 0, 0];
        check(CoverPalette.DominantArgb(pixels) == 0xFFBF5129,
            "封面主色忽略透明像素及黑白边缘，采用主要色群的平均颜色");
        check(CoverPalette.DominantArgb([255, 255, 255, 255]) == 0xFFFFFFFF
            && CoverPalette.DominantArgb([]) == 0xFF48545C,
            "单色封面及空图片使用稳定回退色");

        string folder = Path.Combine(root, "overview-media");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < 240; i++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"Track {i:D3}.mp3"), "sample " + i);
        using var model = new MainViewModel(Path.Combine(root, "overview.json"));
        await model.InitializeAsync(); await model.AddDirectoryAsync(folder);
        var original = model.Results.ToArray();
        int notifications = 0;
        model.Results.CollectionChanged += (_, _) => notifications++;
        model.SetPresentationScrolling(true);
        for (int i = 0; i < original.Length; i++)
            model.ApplyLocalMetadata(original[i], original[i].Song.Sources[0], new($"Title {239 - i:D3}", "Artist"));
        check(notifications == 0 && model.Results.SequenceEqual(original) && original[0].Title == "Title 239",
            "滚动期间批量资料回填更新可见文字，不移动或重建歌曲集合");
        await model.SaveOnlineMetadataAsync(original[0].Song.Id, new("AAA online", "Online artist"));
        check(notifications == 0 && original[0].ArtistDetail == "Online artist",
            "联网结果在滚动期间仍可保存和显示，不触发整库重排");
        model.SetPresentationScrolling(false);
        check(model.Results[0] == original[0] && model.Results[1] == original[^1]
            && model.Results.Select(row => row.Title).SequenceEqual(model.Results.Select(row => row.Title).Order(StringComparer.CurrentCultureIgnoreCase)),
            "滚动结束后统一恢复歌名排序，保持原有行对象");
        int settled = notifications;
        model.SetPresentationScrolling(false);
        check(notifications == settled, "空闲状态重复通知不再次触发集合变更");

        int titleNotifications = 0, artistNotifications = 0, requests = 0;
        original[1].PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibrarySongItem.Title)) titleNotifications++;
            if (e.PropertyName == nameof(LibrarySongItem.Artist)) artistNotifications++;
        };
        model.ApplyLocalMetadata(original[1], original[1].Song.Sources[0], new(original[1].Title, "Changed artist"));
        check(titleNotifications == 0 && artistNotifications == 1,
            "只补全艺人时不重复通知歌名与无关字段，避免整行重新布局");
        model.DetailsPresentationInvalidated += (_, _) => requests++;
        var beforeBatch = model.Results.ToArray();
        notifications = 0;
        model.ApplyLocalMetadata(original[1], original[1].Song.Sources[0], new("AAA batched 1", "Changed artist"));
        model.ApplyLocalMetadata(original[2], original[2].Song.Sources[0], new("AAA batched 2", "Changed artist"));
        check(requests == 1 && notifications == 0 && model.Results.SequenceEqual(beforeBatch),
            "空闲时同一批后台标题更新只请求一次重排，不逐个修改集合");
        model.FlushDetailsPresentation();
        check(model.Results[0] == original[1] && model.Results[1] == original[2],
            "合并刷新后采用最终标题排序，资料回填仍使用原行对象");

        CheckMinimalCollectionChanges(check);
    }

    private static void CheckMinimalCollectionChanges(Action<bool, string> check)
    {
        LibrarySongItem Row(int index) => new(new Song($"row-{index}", $"Track {index:D4}", []));
        var items = Enumerable.Range(0, 1007).Select(Row).ToArray();
        var rows = new ObservableCollection<LibrarySongItem>(items);
        int moves = 0, removes = 0, adds = 0, resets = 0;
        rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move) moves++;
            if (e.Action == NotifyCollectionChangedAction.Remove) removes++;
            if (e.Action == NotifyCollectionChangedAction.Add) adds++;
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };
        var shifted = items.Skip(1).Append(items[0]).ToArray();
        int reported = MainViewModel.SyncRows(rows, shifted);
        check(rows.SequenceEqual(shifted) && reported == 1 && moves == 1 && adds == 0 && removes == 0 && resets == 0,
            "1007 首曲库中首项改名移至末尾只发一次 Move，不移动其余 1006 行");
        moves = 0;
        reported = MainViewModel.SyncRows(rows, items);
        check(rows.SequenceEqual(items) && reported == 1 && moves == 1,
            "末项移回首位也只发一次 Move，保留歌曲对象和集合");
        moves = 0;
        var afterRemoval = items.Where(item => !ReferenceEquals(item, items[503])).ToArray();
        reported = MainViewModel.SyncRows(rows, afterRemoval);
        check(rows.SequenceEqual(afterRemoval) && reported == 0 && moves == 0 && removes == 1 && adds == 0 && resets == 0,
            "删除中间歌曲仅发一次 Remove，不让后半张列表逐行 Move");

        var small = items.Take(5).ToArray();
        var mixed = new[] { small[4], Row(2000), small[1], small[3], Row(2001), small[0] };
        var mixedRows = new ObservableCollection<LibrarySongItem>(small);
        reported = MainViewModel.SyncRows(mixedRows, mixed);
        check(mixedRows.SequenceEqual(mixed) && reported == 2,
            "混合插入、移除和重排时保留已有顺序子集，以两次 Move 完成所需排列");

        // Independent quadratic reference for randomized mixed changes guards the binary-search LIS diff.
        bool correct = true;
        var random = new Random(6635);
        for (int pass = 0; pass < 80 && correct; pass++)
        {
            var baseline = Enumerable.Range(0, 37).Select(Row).OrderBy(_ => random.Next()).ToArray();
            var desired = baseline.Where(_ => random.Next(5) != 0).Concat(Enumerable.Range(50, 6).Select(Row))
                .OrderBy(_ => random.Next()).ToArray();
            var oldOrder = baseline.Select((item, index) => (item, index)).ToDictionary(pair => pair.item, pair => pair.index);
            int[] sequence = desired.Where(oldOrder.ContainsKey).Select(item => oldOrder[item]).ToArray();
            int[] longest = new int[sequence.Length];
            for (int i = 0; i < sequence.Length; i++)
            {
                longest[i] = 1;
                for (int j = 0; j < i; j++) if (sequence[j] < sequence[i]) longest[i] = Math.Max(longest[i], longest[j] + 1);
            }
            var actual = new ObservableCollection<LibrarySongItem>(baseline);
            int changes = MainViewModel.SyncRows(actual, desired);
            correct = actual.SequenceEqual(desired) && changes == sequence.Length - longest.DefaultIfEmpty(0).Max();
        }
        check(correct, "随机混合变更保持最终对象顺序，Move 次数达到最长有序子集所需的最小值");
    }
}
