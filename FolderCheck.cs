using System.Text;

namespace erablue_resort同期アプリ;

/// <summary>
/// 画像フォルダ名のチェック。キャラ CSV（番号＋呼び名）に無いキャラ画像フォルダを見つけ、扱いを分類する。
/// ゲームはフォルダを「番号＋名前」の完全一致で引くので、削除・改名されたキャラの旧フォルダが残っていても
/// 新しいキャラの画像と混ざることはない。ただしセーブは画像フォルダ名を覚えているので、旧フォルダを
/// 消すとそのセーブで画像が出なくなる。そのため動かすのは「中身が全部、残るフォルダにもある」ものだけ
/// </summary>
static class FolderCheck
{
    public enum Kind
    {
        /// <summary>セーブが画像フォルダとして覚えている</summary>
        UsedBySave,
        /// <summary>ERB・CSV に名前が書かれている（差分・特殊フォルダ）</summary>
        UsedByErb,
        /// <summary>中身が全部、残るフォルダにもある（退避してよい）</summary>
        Duplicate,
        /// <summary>どこからも参照されず、中身も別。判断できないので残す</summary>
        Unknown,
    }

    public sealed record Item(string Name, Kind Kind, int Files, long Bytes, bool InBody, string Detail)
    {
        public bool Movable => Kind == Kind.Duplicate && !InBody;
    }

    public sealed record Report(int Expected, int Folders, List<Item> Items, List<string> MissingExpected)
    {
        public IEnumerable<Item> Movable => Items.Where(i => i.Movable);
    }

    /// <summary>
    /// bodyRoot: 本体（CSV・ERB）、appResources: 追加画像の保存先、targetRoot: ゲーム（sav を見る。無ければ null）
    /// </summary>
    public static Report Run(string bodyRoot, string appResources, string? targetRoot, Action<string> log)
    {
        string bodyResources = Path.Combine(bodyRoot, "resources");
        var expected = LoadExpectedFolders(Path.Combine(bodyRoot, "CSV"));
        if (expected.Count == 0) throw new InvalidOperationException("本体の CSV にキャラのデータが見つかりません。先に「本体を更新」を実行してください。");

        // ゲームから見えるのは本体の resources と追加画像の保存先を重ねたもの
        var folders = new Dictionary<string, (bool InApp, bool InBody)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, isBody) in new[] { (appResources, false), (bodyResources, true) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(d);
                folders.TryGetValue(name, out var v);
                folders[name] = isBody ? (v.InApp, true) : (true, v.InBody);
            }
        }

        // キャラ画像フォルダ（数字で始まる）のうち、CSV から作れる名前でないもの
        var candidates = folders.Keys.Where(n => n.Length > 0 && char.IsAsciiDigit(n[0]) && !expected.ContainsKey(n))
            .Order(StringComparer.Ordinal).ToList();
        log($"キャラ CSV {expected.Count:#,0} 人 / 画像フォルダ {folders.Count:#,0} 個のうち、CSV に無い名前のフォルダ {candidates.Count} 個を調べます");

        var savHits = FindInSaves(targetRoot, candidates);
        var erbHits = FindInText(bodyRoot, candidates);

        var keep = new HashSet<string>(expected.Keys.Where(folders.ContainsKey), StringComparer.OrdinalIgnoreCase);
        var kinds = new Dictionary<string, (Kind Kind, string Detail)>(StringComparer.OrdinalIgnoreCase);
        foreach (string c in candidates)
        {
            if (savHits.TryGetValue(c, out var savs)) kinds[c] = (Kind.UsedBySave, "セーブ: " + string.Join(", ", savs.Take(5)) + (savs.Count > 5 ? " ほか" : ""));
            else if (erbHits.TryGetValue(c, out string? file)) kinds[c] = (Kind.UsedByErb, "参照: " + file);
            else if (c.StartsWith("999", StringComparison.Ordinal)) kinds[c] = (Kind.UsedByErb, "ランダムキャラ用（999＋名前）");
            else continue;
            keep.Add(c);
        }

        // 残るフォルダの中身を（ファイル名, サイズ）で引けるようにしておく
        var index = new Dictionary<(string, long), List<string>>();
        foreach (string k in keep)
        {
            foreach (string root in Roots(k, folders[k], appResources, bodyResources))
            {
                foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    var key = (f.Name.ToLowerInvariant(), f.Length);
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new();
                    list.Add(f.FullName);
                }
            }
        }

        var items = new List<Item>();
        foreach (string c in candidates)
        {
            var loc = folders[c];
            var files = Roots(c, loc, appResources, bodyResources)
                .SelectMany(r => new DirectoryInfo(r).EnumerateFiles("*", SearchOption.AllDirectories)).ToList();
            long bytes = files.Sum(f => f.Length);
            if (kinds.TryGetValue(c, out var k))
            {
                items.Add(new Item(c, k.Kind, files.Count, bytes, loc.InBody, k.Detail));
                continue;
            }
            // 全ファイルが、残るフォルダに同じ中身である
            var dupFolders = new SortedSet<string>(StringComparer.Ordinal);
            bool allDup = files.Count > 0;
            foreach (var f in files)
            {
                string? same = index.TryGetValue((f.Name.ToLowerInvariant(), f.Length), out var list)
                    ? list.FirstOrDefault(p => SameContent(p, f.FullName)) : null;
                if (same == null) { allDup = false; break; }
                dupFolders.Add(TopFolder(same, appResources, bodyResources));
            }
            items.Add(allDup
                ? new Item(c, Kind.Duplicate, files.Count, bytes, loc.InBody, "同じ中身: " + string.Join(", ", dupFolders))
                : new Item(c, Kind.Unknown, files.Count, bytes, loc.InBody, ""));
        }

        // CSV の名前のフォルダが無く、同じ番号の別名フォルダだけがある（新しく始めたゲームでは画像が出ない）
        var missing = new List<string>();
        foreach (var (name, number) in expected)
        {
            if (folders.ContainsKey(name) || name.Contains("ダミー")) continue;
            var others = candidates.Where(c => c.StartsWith(number, StringComparison.Ordinal) && c.Length > number.Length && !char.IsAsciiDigit(c[number.Length])).ToList();
            if (others.Count > 0) missing.Add($"{name}（無い。あるのは {string.Join(", ", others)}）");
        }
        return new Report(expected.Count, folders.Count, items, missing);
    }

    /// <summary>フォルダ名 → 番号。番号＋（フォルダ判定キー、無ければ呼び名）</summary>
    public static Dictionary<string, string> LoadExpectedFolders(string csvDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(csvDir)) return result;
        foreach (string path in Directory.EnumerateFiles(csvDir, "Chara*.csv", SearchOption.AllDirectories))
        {
            string? no = null, call = null, key = null;
            foreach (string raw in ReadText(path).Split('\n'))
            {
                string line = raw.Split(';')[0];
                var c = line.Split(',').Select(s => s.Trim()).ToArray();
                if (c.Length < 2) continue;
                if (c[0] == "番号" || c[0].Equals("NO", StringComparison.OrdinalIgnoreCase)) no = c[1];
                else if (c[0] == "呼び名" || c[0].Equals("CALLNAME", StringComparison.OrdinalIgnoreCase)) call = c[1];
                else if (c[0] == "CSTR" && c.Length >= 3 && c[1] == "フォルダ判定キー") key = c[2];
            }
            if (no != null && no.All(char.IsAsciiDigit)) result[no + (string.IsNullOrEmpty(key) ? call ?? "" : key)] = no;
        }
        return result;
    }

    /// <summary>
    /// 選んだフォルダを追加画像の保存先から退避先へ移し、中のファイルを「取り込まない版」に登録する
    /// （Dropbox・全まとめから取り直したり、ゲーム側に残った分を同期で取り込み直したりしないため）
    /// </summary>
    public static (int Folders, int Files) Move(IEnumerable<Item> items, string appResources, string evacuateDir, ImageState state, Action<string> log)
    {
        int folders = 0, files = 0;
        foreach (var item in items.Where(i => i.Movable))
        {
            string src = Path.Combine(appResources, item.Name);
            if (!Directory.Exists(src)) continue;
            foreach (var f in new DirectoryInfo(src).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(appResources, f.FullName).Replace(Path.DirectorySeparatorChar, '/');
                string zip = state.Files.TryGetValue(rel, out var v) ? v.Zip : "画像フォルダ名チェック";
                state.Exclude(rel, f.Length, zip);
                files++;
            }
            Directory.CreateDirectory(evacuateDir);
            PathUtil.MoveDirectory(src, Path.Combine(evacuateDir, item.Name), log);
            folders++;
            log($"退避しました: {item.Name}（{item.Files} 件 / {item.Detail}）");
        }
        return (folders, files);
    }

    public static string Format(Report r)
    {
        var sb = new StringBuilder();
        sb.Append($"キャラ CSV {r.Expected:#,0} 人 / 画像フォルダ {r.Folders:#,0} 個 / CSV に無い名前のキャラ画像フォルダ {r.Items.Count} 個\r\n");
        foreach (var (kind, title) in new[]
        {
            (Kind.Duplicate, "① 中身が全部ほかのフォルダにもある（退避してよい）"),
            (Kind.UsedBySave, "② セーブが使っている（残す。消すとそのセーブで画像が出なくなる）"),
            (Kind.UsedByErb, "③ ERB・CSV が使っている差分・特殊フォルダ（残す）"),
            (Kind.Unknown, "④ 参照が見つからず、中身も別（判断できないので残す）"),
        })
        {
            var list = r.Items.Where(i => i.Kind == kind).ToList();
            sb.Append($"\r\n{title}: {list.Count} 個\r\n");
            foreach (var i in list)
            {
                sb.Append($"  {i.Name}（{i.Files} 件 / {i.Bytes / 1e6:0.0} MB）");
                if (i.Detail.Length > 0) sb.Append("  ").Append(i.Detail);
                if (i.InBody) sb.Append("  ※本体に含まれる（動かしません）");
                sb.Append("\r\n");
            }
        }
        if (r.MissingExpected.Count > 0)
        {
            sb.Append($"\r\nCSV の名前のフォルダが無いキャラ（新しく始めたゲームでは画像が出ない可能性）: {r.MissingExpected.Count} 人\r\n");
            foreach (string m in r.MissingExpected) sb.Append("  ").Append(m).Append("\r\n");
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Roots(string name, (bool InApp, bool InBody) loc, string appResources, string bodyResources)
    {
        if (loc.InApp) yield return Path.Combine(appResources, name);
        if (loc.InBody) yield return Path.Combine(bodyResources, name);
    }

    private static string TopFolder(string path, string appResources, string bodyResources)
    {
        string root = PathUtil.IsSameOrUnder(path, appResources) ? appResources : bodyResources;
        return Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0];
    }

    // セーブ（バイナリ）には画像フォルダ名が UTF-16 で入っている。念のため UTF-8 でも探す
    private static Dictionary<string, List<string>> FindInSaves(string? targetRoot, List<string> names)
    {
        var hits = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? sav = targetRoot == null ? null : Path.Combine(targetRoot, "sav");
        if (sav == null || !Directory.Exists(sav)) return hits;
        var patterns = names.Select(n => (n, u16: Encoding.Unicode.GetBytes(n), u8: Encoding.UTF8.GetBytes(n))).ToList();
        foreach (string path in Directory.EnumerateFiles(sav, "*.sav", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); } catch (IOException) { continue; }
            foreach (var (n, u16, u8) in patterns)
            {
                if (data.AsSpan().IndexOf(u16) < 0 && data.AsSpan().IndexOf(u8) < 0) continue;
                if (!hits.TryGetValue(n, out var list)) hits[n] = list = new();
                list.Add(Path.GetFileName(path));
            }
        }
        return hits;
    }

    // ERB・CSV にフォルダ名がそのまま書かれているか（最初に見つかったファイル）
    private static Dictionary<string, string> FindInText(string bodyRoot, List<string> names)
    {
        var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".erb", ".erh", ".erd", ".csv" };
        foreach (string sub in new[] { "ERB", "CSV" })
        {
            string dir = Path.Combine(bodyRoot, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!exts.Contains(Path.GetExtension(path))) continue;
                // キャラ CSV そのもの（Chara1037 リーファ.csv の呼び名など）は参照に数えない
                if (Path.GetFileName(path).StartsWith("Chara", StringComparison.OrdinalIgnoreCase)) continue;
                string text = ReadText(path);
                foreach (string n in names)
                {
                    if (!hits.ContainsKey(n) && text.Contains(n, StringComparison.Ordinal))
                        hits[n] = Path.GetRelativePath(bodyRoot, path);
                }
            }
        }
        return hits;
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }
}
