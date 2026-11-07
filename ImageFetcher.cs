using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace erablue_resort同期アプリ;

/// <summary>
/// Wiki に載っている Dropbox の「○○以降更新分」フォルダから、まだ取り込んでいない zip だけを
/// 1つずつダウンロードして resources に展開する。
/// 一覧が取れないとき（Dropbox 側の仕様変更など）はフォルダ丸ごとの zip にフォールバックする
/// </summary>
static class ImageFetcher
{
    public const string WikiRawUrl = "https://raw.githubusercontent.com/wiki/erablue-resort/erablue_resort/Home.md";

    static ImageFetcher()
    {
        // 配布 zip のファイル名は Shift_JIS
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// autoFromWiki なら Wiki のリンクを優先し、見つからなければ manualUrl を使う。
    /// 実際に使ったリンク（共有リンクの形）を返す
    /// </summary>
    /// <param name="askExclude">
    /// 前回までに取り込んだ画像が手元から消されていたときに呼ぶ。true ならその版を今後取り込まない、false なら取り直す
    /// </param>
    public static async Task<string> Run(string resourcesDir, string statePath, string workDir, bool autoFromWiki, string manualUrl,
        Func<List<(string Rel, long Size, string Zip)>, bool> askExclude, Action<string> log, IProgress<(long done, long total)> progress)
    {
        var state = ImageState.Load(statePath, resourcesDir, log);
        var deleted = state.FindDeleted();
        if (deleted.Count > 0)
        {
            if (askExclude(deleted))
            {
                foreach (var (rel, size, zip) in deleted) state.Exclude(rel, size, zip);
                log($"手元で消された画像 {deleted.Count} 件を「取り込まない画像」に登録しました（同じ名前でも中身の違う版は取り込みます）");
            }
            else
            {
                state.Refetch(deleted);
                log($"手元で消された画像 {deleted.Count} 件を取り直します");
            }
            state.Save(statePath);
        }

        string? found = autoFromWiki ? await FindDropboxUrl(log) : null;
        string sharedUrl = found ?? manualUrl;
        if (found == null) log("設定の追加画像リンクを使用します");
        Directory.CreateDirectory(workDir);

        // Wiki のリンクが新しい「○○以降更新分」に切り替わったときは、前のフォルダの取り残しを先に取り込む
        // （しばらく起動していなかった間に前のフォルダに上がった分を落とさないため）
        if (found != null && FolderKey(manualUrl) is string oldKey && oldKey != FolderKey(found))
        {
            log("追加画像のフォルダが切り替わったので、前のフォルダの取り残しを先に確かめます");
            try
            {
                await ApplyFolder(manualUrl, state, statePath, workDir, log, progress, allowWhole: false);
            }
            catch (Exception ex)
            {
                log("前のフォルダは確かめられませんでした（消されている可能性があります）: " + ex.Message);
            }
        }
        await ApplyFolder(sharedUrl, state, statePath, workDir, log, progress, allowWhole: true);
        return sharedUrl;
    }

    // 共有フォルダのリンクの識別子（/scl/fo/〇〇/ の部分）。Dropbox のフォルダでなければ null
    private static string? FolderKey(string url)
    {
        var m = Regex.Match(url ?? "", @"dropbox\.com/scl/fo/([^/?#]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // 1つの共有フォルダから、まだ取り込んでいない zip を取り込む（一覧が取れなければ、allowWhole ならフォルダ丸ごと）
    private static async Task ApplyFolder(string sharedUrl, ImageState state, string statePath, string workDir, Action<string> log,
        IProgress<(long done, long total)> progress, bool allowWhole)
    {
        List<RemoteFile>? files = null;
        try
        {
            files = await Http.Retry("Dropbox の一覧取得", () => ListFolder(sharedUrl));
        }
        catch (Exception ex)
        {
            log("Dropbox の一覧を取得できませんでした: " + ex.Message);
        }

        if (files != null && await ApplyFromList(files, state, statePath, workDir, log, progress))
        {
            // 差分だけ取り込み済み
        }
        else if (files == null && !allowWhole)
        {
            // 前のフォルダは消されていることが多いので、丸ごとのダウンロードは試さない
            log("前のフォルダは確かめられませんでした（消されている可能性があります）");
        }
        else
        {
            log(files == null ? "フォルダ丸ごとダウンロードに切り替えます" : "フォルダ丸ごとダウンロードします");
            string zipPath = Path.Combine(workDir, "erablue_resort_images.zip");
            await Http.DownloadFile(ConnectionSettings.ToDropboxDownloadUrl(sharedUrl), zipPath, progress);
            try
            {
                await Task.Run(() => ApplyNew(zipPath, state, statePath, log));
            }
            finally
            {
                File.Delete(zipPath);
            }
        }
    }

    // PreviewUrl は zip の中身一覧（ファイル名とサイズ）を返す。ダウンロードせずに中身を確かめるのに使う
    private record RemoteFile(string Name, long Bytes, long Ts, string Href, string? PreviewUrl);

    // 取り込み済みかどうかの鍵。中身を差し替えるとサイズが変わるのでサイズまで見る
    private static string Key(string name, long bytes) => ImageState.ZipKey(name, bytes);

    /// <summary>
    /// 共有フォルダのファイル一覧を Dropbox の Web 画面と同じ方法で取得する（ログイン不要）
    /// </summary>
    private static async Task<List<RemoteFile>> ListFolder(string sharedUrl)
    {
        var uri = new Uri(sharedUrl);
        var m = Regex.Match(uri.AbsolutePath, @"^/scl/fo/([^/]+)/([^/]+)");
        if (!m.Success) throw new InvalidOperationException("対応していない形式のリンクです");
        string linkKey = m.Groups[1].Value, secureHash = m.Groups[2].Value;
        string rlkey = Regex.Match(uri.Query, @"[?&]rlkey=([^&]+)").Groups[1].Value;

        // 画面を一度開くと、一覧の取得に必要な t（CSRF トークン）が Cookie で配られる
        string pageUrl = $"https://www.dropbox.com/scl/fo/{linkKey}/{secureHash}?rlkey={rlkey}&dl=0";
        using (var page = await Http.Client.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead)) page.EnsureSuccessStatusCode();
        string token = Http.Cookies.GetCookies(new Uri("https://www.dropbox.com/"))["t"]?.Value
            ?? throw new InvalidOperationException("トークンを取得できませんでした");

        var result = new List<RemoteFile>();
        string? voucher = null;
        for (int page = 0; page < 100; page++)
        {
            var form = new Dictionary<string, string>
            {
                ["is_xhr"] = "true", ["t"] = token, ["link_key"] = linkKey, ["link_type"] = "c",
                ["secure_hash"] = secureHash, ["sub_path"] = "", ["rlkey"] = rlkey,
            };
            if (voucher != null) form["voucher"] = voucher;
            using var response = await Http.Client.PostAsync("https://www.dropbox.com/list_shared_link_folder_entries", new FormUrlEncodedContent(form));
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            foreach (var e in root.GetProperty("entries").EnumerateArray())
            {
                if (e.TryGetProperty("is_dir", out var dir) && dir.GetBoolean()) continue;
                string? preview = e.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.Object
                    && pv.TryGetProperty("preview_url", out var pu) && pu.ValueKind == JsonValueKind.String ? pu.GetString() : null;
                result.Add(new RemoteFile(e.GetProperty("filename").GetString()!, e.GetProperty("bytes").GetInt64(),
                    e.TryGetProperty("ts", out var ts) ? ts.GetInt64() : 0, e.GetProperty("href").GetString()!, preview));
            }
            bool more = root.TryGetProperty("has_more_entries", out var hm) && hm.GetBoolean();
            if (!more) return result;
            voucher = root.GetProperty("next_request_voucher").GetString();
        }
        throw new InvalidOperationException("一覧が多すぎます");
    }

    /// <summary>
    /// zip の中身一覧を取り、展開先と同じ名前・同じサイズのファイルが全部そろっているか確かめる。
    /// 「○月○日～○月○日.zip」のようなまとめは、日ごとの zip を取り込み済みなら中身がそろっているので
    /// ダウンロードせずに済む。足りない・違うファイルの数を返す（確かめられなければ null）
    /// </summary>
    private static async Task<int?> CountMissing(RemoteFile f, ImageState state)
    {
        if (f.PreviewUrl == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetString(f.PreviewUrl, $"{f.Name} の中身一覧の取得"));
            var entries = new List<(string Name, long Size)>();
            void Walk(JsonElement node, string prefix)
            {
                if (!node.TryGetProperty("children", out var children)) return;
                foreach (var c in children.EnumerateArray())
                {
                    string path = prefix + c.GetProperty("name").GetString();
                    if (c.TryGetProperty("isDir", out var d) && d.GetBoolean()) Walk(c, path + "/");
                    else entries.Add((path, long.Parse(c.GetProperty("size").ToString())));
                }
            }
            Walk(doc.RootElement, "");
            if (entries.Count == 0) return null;

            string root = state.ResourcesDir;
            bool strip = ShouldStrip(entries.Select(e => e.Name).ToList());
            // この zip がアップロードされた日時。手元の方が新しければ、後の日ごとの zip で更新済みとみなす
            DateTime uploaded = f.Ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(f.Ts).LocalDateTime : DateTime.MinValue;
            int missing = 0;
            foreach (var (name, size) in entries)
            {
                string? dest = DestPath(root, name, strip);
                if (dest == null || state.IsExcluded(RelOf(root, dest), size)) continue;
                var info = new FileInfo(dest);
                if (!info.Exists) missing++;
                else if (info.Length != size && info.LastWriteTime <= uploaded) missing++;
            }
            return missing;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 新しい zip だけを1つずつ取り込む。中身が既にそろっている zip（まとめ等）はダウンロードしない。
    /// 初回など取るものが大半なら、1つずつより丸ごとの方が速いので false を返して丸ごとダウンロードに任せる
    /// </summary>
    private static async Task<bool> ApplyFromList(List<RemoteFile> files, ImageState state, string statePath, string workDir,
        Action<string> log, IProgress<(long done, long total)> progress)
    {
        var applied = state.Zips;
        string resourcesDir = state.ResourcesDir;
        Directory.CreateDirectory(resourcesDir);

        var zips = files.Where(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        var others = files.Except(zips).Select(f => f.Name).ToList();
        if (others.Count > 0) log("  zip 以外は飛ばします: " + string.Join(", ", others));
        // 古い順に展開して、新しい版が最後に上書きするようにする
        var todo = zips.Where(f => !applied.Contains(Key(f.Name, f.Bytes))).OrderBy(f => f.Ts).ToList();
        long totalBytes = todo.Sum(f => f.Bytes);
        log($"Dropbox の zip {zips.Count} 個のうち、未取り込み {todo.Count} 個（{totalBytes / 1e6:0.0} MB）");
        if (todo.Count == 0)
        {
            log("新しい追加画像はありませんでした");
            return true;
        }

        // まとめ等で中身が既にそろっているものは、記録だけ付けてダウンロードしない
        var needed = new List<RemoteFile>();
        long saved = 0;
        // 手元が空（初回）なら確かめるまでもなく全部要る
        bool check = Directory.EnumerateFileSystemEntries(resourcesDir).Any();
        foreach (var f in todo)
        {
            int? missing = check ? await CountMissing(f, state) : null;
            if (missing == 0)
            {
                log($"  スキップ: {f.Name}（中身はすべて取り込み済み / {f.Bytes / 1e6:0.0} MB 節約）");
                saved += f.Bytes;
                applied.Add(Key(f.Name, f.Bytes));
                state.Save(statePath);
                continue;
            }
            if (missing != null && todo.Count <= 10) log($"  {f.Name}: 新規・変更 {missing} ファイル");
            needed.Add(f);
        }
        todo = needed;
        totalBytes = todo.Sum(f => f.Bytes);
        if (todo.Count == 0)
        {
            log($"新しい追加画像はありませんでした（{saved / 1e6:0.0} MB 分のダウンロードを省略）");
            return true;
        }
        log($"ダウンロード: {todo.Count} 個（{totalBytes / 1e6:0.0} MB）" + (saved > 0 ? $" / 省略: {saved / 1e6:0.0} MB" : ""));
        if (todo.Count >= 10 && totalBytes > zips.Sum(f => f.Bytes) / 2) return false;

        long doneBefore = 0;
        int fileCount = 0;
        foreach (var f in todo)
        {
            // 共有フォルダ内のファイルは dl=1 だと画面が返るので raw=1 で中身を直接取る
            string url = Regex.Replace(f.Href, @"([?&])dl=[^&]*", "${1}raw=1");
            if (!url.Contains("raw=1")) url += (url.Contains('?') ? "&" : "?") + "raw=1";
            string zipPath = Path.Combine(workDir, "erablue_resort_image_part.zip");
            long offset = doneBefore;
            // 大きさは一覧で分かっているので、途中で切れたら自動でやり直す
            await Http.DownloadFile(url, zipPath, new Progress<(long done, long total)>(p => progress.Report((offset + p.done, totalBytes))), f.Bytes);
            try
            {
                if (new FileInfo(zipPath).Length != f.Bytes) throw new InvalidDataException($"{f.Name} のサイズが一覧と違います（途中で切れた可能性があります）");
                var (n, skipped) = await Task.Run(() =>
                {
                    using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.Latin1);
                    return ExtractInto(zip, state, f.Name);
                });
                log($"  取り込み: {f.Name}（{f.Bytes / 1e6:0.0} MB / {n} ファイル" + (skipped > 0 ? $" / 取り込まない画像 {skipped} 件を除外" : "") + "）");
                fileCount += n;
                applied.Add(Key(f.Name, f.Bytes));
                state.Save(statePath);
            }
            finally
            {
                File.Delete(zipPath);
            }
            doneBefore += f.Bytes;
        }
        log($"追加画像の取り込みが完了しました（zip {todo.Count} 個 / {fileCount} ファイル）");
        return true;
    }

    private static async Task<string?> FindDropboxUrl(Action<string> log)
    {
        try
        {
            string md = await Http.GetString(WikiRawUrl, "Wiki の取得");
            foreach (string line in md.Split('\n'))
            {
                if (!line.Contains("更新分")) continue;
                var m = Regex.Match(line, @"https://www\.dropbox\.com/scl/fo/[^\s|)\]]+");
                if (!m.Success) continue;
                string label = line.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                log($"Wiki のリンクを使用: {label}");
                return m.Value;
            }
            log("Wiki に追加画像のリンクが見つかりませんでした");
        }
        catch (Exception ex)
        {
            log("Wiki の取得に失敗しました: " + ex.Message);
        }
        return null;
    }

    private static void ApplyNew(string outerZipPath, ImageState state, string statePath, Action<string> log)
    {
        var applied = state.Zips;
        Directory.CreateDirectory(state.ResourcesDir);

        int zipCount = 0, fileCount = 0, skippedTotal = 0;
        using var outer = ZipFile.Open(outerZipPath, ZipArchiveMode.Read, Encoding.Latin1);
        // 古い順に展開して、新しい版が最後に上書きするようにする
        foreach (var entry in outer.Entries
                     .Where(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.LastWriteTime))
        {
            string name = DecodeName(entry.FullName).Trim('/');
            string key = Key(name, entry.Length);
            if (applied.Contains(key)) continue;

            using var buffer = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(buffer);
            buffer.Position = 0;
            int n, skipped;
            using (var inner = new ZipArchive(buffer, ZipArchiveMode.Read, false, Encoding.Latin1))
            {
                (n, skipped) = ExtractInto(inner, state, name);
            }
            log($"  取り込み: {name}（{n} ファイル）");
            zipCount++;
            fileCount += n;
            skippedTotal += skipped;
            applied.Add(key);
            state.Save(statePath);
        }
        log(zipCount == 0 ? "新しい追加画像はありませんでした"
            : $"追加画像の取り込みが完了しました（zip {zipCount} 個 / {fileCount} ファイル）" + (skippedTotal > 0 ? $"（取り込まない画像 {skippedTotal} 件を除外）" : ""));
    }

    // 1つの zip の中でも UTF-8 と Shift_JIS の名前が混ざっていることがあるので、
    // Latin1 で生のバイト列のまま読み、エントリごとに判定して復号する
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    internal static string DecodeName(string rawName)
    {
        if (rawName.Any(c => c > 0xFF)) return rawName; // 既に正しく復号されている
        byte[] bytes = Encoding.Latin1.GetBytes(rawName);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(932).GetString(bytes); }
    }

    // 「10月13日以降顔グラ画像まとめ/キャラ/画像」のような包みフォルダは剥がす。
    // キャラフォルダ直下には画像があるので、全ファイルが2階層以上かどうかで見分ける
    internal static bool ShouldStrip(List<string> fileNames)
    {
        var tops = fileNames.Select(n => n.Split('/')[0]).Distinct().ToList();
        return tops.Count == 1 && fileNames.All(n => n.Count(c => c == '/') >= 2)
            && (Regex.IsMatch(tops[0], @"まとめ|以降|^\d+月\d+日") || !Regex.IsMatch(tops[0], @"^\d"));
    }

    // zip 内の名前 → 展開先のフルパス（resources の外に出るものは null）
    internal static string? DestPath(string root, string name, bool strip)
    {
        var parts = name.Split('/').Skip(strip ? 1 : 0).Where(p => p is not ("" or "." or "..")).ToArray();
        if (parts.Length == 0) return null;
        string dest = Path.GetFullPath(Path.Combine([root, .. parts]));
        return dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? dest : null;
    }

    internal static string RelOf(string root, string dest) => Path.GetRelativePath(root, dest).Replace('\\', '/');

    // 展開した数と、取り込まない画像として飛ばした数を返す
    private static (int Extracted, int Excluded) ExtractInto(ZipArchive zip, ImageState state, string zipName)
    {
        var files = zip.Entries
            .Select(e => (Name: DecodeName(e.FullName), Entry: e))
            .Where(f => !f.Name.EndsWith('/') && !f.Name.EndsWith('\\'))
            .ToList();
        bool strip = ShouldStrip(files.Select(f => f.Name).ToList());

        string root = state.ResourcesDir;
        int count = 0, excluded = 0;
        foreach (var (name, entry) in files)
        {
            string? dest = DestPath(root, name, strip);
            if (dest == null) continue;
            string rel = RelOf(root, dest);
            if (state.IsExcluded(rel, entry.Length)) { excluded++; continue; }
            // 古いまとめを後から取り込んだときに、日ごとの zip で入った新しい版を戻さない
            // （展開したファイルの更新日時は zip 内の日時になるので、それで新旧を比べる）
            if (File.Exists(dest) && File.GetLastWriteTime(dest) > entry.LastWriteTime.DateTime.AddSeconds(2)) continue;
            // 一時ファイルに展開してから差し替える（ハードリンクでつながったゲーム側の画像を直接書き換えない）
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string temp = PathUtil.TempPathFor(dest);
            entry.ExtractToFile(temp, true);
            PathUtil.ReplaceWith(temp, dest);
            state.RecordExtracted(rel, entry.Length, zipName);
            count++;
        }
        return (count, excluded);
    }
}
