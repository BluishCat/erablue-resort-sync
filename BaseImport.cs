using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace erablue_resort同期アプリ;

/// <summary>Wiki に載っている全まとめ（ギガファイル便）のリンク</summary>
record BasePackLink(string PageUrl, string Host, string FileId, string Key, string Label)
{
    public string DownloadUrl => $"https://{Host}/download.php?file={FileId}&dlkey={Uri.EscapeDataString(Key)}";
}

/// <summary>全まとめ（resources.zip）の中の zip 1つ</summary>
record BasePackPart(string Name, long CompressedSize, long Size, uint Crc, long HeaderOffset, int Method)
{
    public string Key => $"{Name}|{Size}|{Crc:x8}";
}

/// <summary>
/// 顔グラ画像の全まとめ。ギガファイル便の resources.zip は「中に zip が入った zip」なので、
/// 末尾の一覧だけを範囲指定で読み、中の zip を1つずつ（途中で切れたら続きから）取ってきて resources に展開する。
/// 手元にある zip の取り込みも同じ展開処理を使う
/// </summary>
static class BaseImport
{
    // 追加画像の保存先の画像がこれより少なければ「新規」とみなし、全まとめを自動で取り込む
    public const int NewUserThreshold = 10_000;

    /// <summary>Wiki から全まとめのリンクと DL キーを探す。無ければ null</summary>
    public static async Task<BasePackLink?> FindLink(Action<string> log)
    {
        string md;
        try
        {
            md = await Http.GetString(ImageFetcher.WikiRawUrl, "Wiki の取得");
        }
        catch (Exception ex)
        {
            log("Wiki の取得に失敗しました（全まとめの確認を飛ばします）: " + ex.Message);
            return null;
        }
        var keyMatch = Regex.Match(md, @"DLパスは\s*[\[［]([^\]］\s]+)[\]］]");
        foreach (string line in md.Split('\n'))
        {
            var m = Regex.Match(line, @"https://(\d+\.gigafile\.nu)/([0-9A-Za-z]+-[0-9A-Za-z]+)");
            if (!m.Success || !line.Contains("まとめ")) continue;
            if (!keyMatch.Success)
            {
                log("Wiki に全まとめの DL パスが見つかりませんでした");
                return null;
            }
            string label = Regex.Replace(line.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "", @"<br\s*/?>", " ").Trim();
            return new BasePackLink(m.Value, m.Groups[1].Value, m.Groups[2].Value, keyMatch.Groups[1].Value, label);
        }
        log("Wiki に全まとめ（ギガファイル便）のリンクが見つかりませんでした");
        return null;
    }

    /// <summary>全まとめの中の zip の一覧（末尾の一覧だけを範囲指定で読む）</summary>
    public static async Task<(long Total, List<BasePackPart> Parts)> ListParts(BasePackLink link)
    {
        // ページを一度開くと、ダウンロードに使う Cookie が配られる
        using (var page = await Http.Client.GetAsync(link.PageUrl)) page.EnsureSuccessStatusCode();

        var (tail, total) = await GetRange(link.DownloadUrl, -65536, 0);
        int eocd = LastIndexOf(tail, [0x50, 0x4b, 0x05, 0x06]);
        if (eocd < 0) throw new InvalidDataException("zip の終わりが見つかりません");
        long count = BitConverter.ToUInt16(tail, eocd + 10);
        long cdSize = BitConverter.ToUInt32(tail, eocd + 12);
        long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
        int z64 = LastIndexOf(tail, [0x50, 0x4b, 0x06, 0x06]);
        if (z64 >= 0)
        {
            count = BitConverter.ToInt64(tail, z64 + 32);
            cdSize = BitConverter.ToInt64(tail, z64 + 40);
            cdOffset = BitConverter.ToInt64(tail, z64 + 48);
        }
        var (cd, _) = await GetRange(link.DownloadUrl, cdOffset, cdOffset + cdSize - 1);

        var parts = new List<BasePackPart>();
        int p = 0;
        for (long i = 0; i < count && p + 46 <= cd.Length; i++)
        {
            if (BitConverter.ToUInt32(cd, p) != 0x02014b50) throw new InvalidDataException("zip の一覧が壊れています");
            int flags = BitConverter.ToUInt16(cd, p + 8);
            int method = BitConverter.ToUInt16(cd, p + 10);
            uint crc = BitConverter.ToUInt32(cd, p + 16);
            long csize = BitConverter.ToUInt32(cd, p + 20);
            long usize = BitConverter.ToUInt32(cd, p + 24);
            int nlen = BitConverter.ToUInt16(cd, p + 28), elen = BitConverter.ToUInt16(cd, p + 30), clen = BitConverter.ToUInt16(cd, p + 32);
            long offset = BitConverter.ToUInt32(cd, p + 42);
            byte[] nameBytes = cd[(p + 46)..(p + 46 + nlen)];
            string name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(nameBytes) : ImageFetcher.DecodeName(Encoding.Latin1.GetString(nameBytes));
            // 4 GB を超えるものは Zip64 の追加情報に本当の値が入っている
            int e = p + 46 + nlen, eEnd = e + elen;
            while (e + 4 <= eEnd)
            {
                int id = BitConverter.ToUInt16(cd, e), len = BitConverter.ToUInt16(cd, e + 2);
                if (id == 1)
                {
                    int q = e + 4;
                    if (usize == 0xFFFFFFFF) { usize = BitConverter.ToInt64(cd, q); q += 8; }
                    if (csize == 0xFFFFFFFF) { csize = BitConverter.ToInt64(cd, q); q += 8; }
                    if (offset == 0xFFFFFFFF) offset = BitConverter.ToInt64(cd, q);
                }
                e += 4 + len;
            }
            if (!name.EndsWith('/')) parts.Add(new BasePackPart(name.Trim('/'), csize, usize, crc, offset, method));
            p += 46 + nlen + elen + clen;
        }
        return (total, parts);
    }

    /// <summary>
    /// 中の zip を1つ取ってきて、展開した zip のパスを返す（呼び出し側で消す）。
    /// 圧縮されたままの部分を作業フォルダに落とし（切れたら続きから）、手元で戻して CRC を確かめる
    /// </summary>
    public static async Task<string> DownloadPart(BasePackLink link, BasePackPart part, string workDir, IProgress<(long done, long total)> progress)
    {
        Directory.CreateDirectory(workDir);
        var (header, _) = await GetRange(link.DownloadUrl, part.HeaderOffset, part.HeaderOffset + 29);
        if (BitConverter.ToUInt32(header, 0) != 0x04034b50) throw new InvalidDataException($"{part.Name} の位置が一覧と合いません");
        long dataStart = part.HeaderOffset + 30 + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);

        string packed = Path.Combine(workDir, "全まとめ_部分.bin");
        string inner = Path.Combine(workDir, "全まとめ_" + SafeName(part.Name));
        if (File.Exists(packed) && new FileInfo(packed).Length > part.CompressedSize) File.Delete(packed);
        try
        {
            await DownloadRange(link.DownloadUrl, dataStart, part.CompressedSize, packed, progress);
            await Task.Run(() =>
            {
                using var src = File.OpenRead(packed);
                using var dst = File.Create(inner);
                if (part.Method == 8)
                {
                    using var inflate = new DeflateStream(src, CompressionMode.Decompress);
                    inflate.CopyTo(dst);
                }
                else if (part.Method == 0)
                {
                    src.CopyTo(dst);
                }
                else
                {
                    throw new InvalidDataException($"{part.Name} は対応していない圧縮方式です（{part.Method}）");
                }
            });
            if (new FileInfo(inner).Length != part.Size || Crc32.Of(inner) != part.Crc)
                throw new InvalidDataException($"{part.Name} の中身が一覧と合いません（壊れている可能性があります）");
            return inner;
        }
        catch
        {
            TryDelete(inner);
            throw;
        }
        finally
        {
            // 最後まで落とせていれば消す（途中なら次回に続きから取る）
            if (File.Exists(packed) && new FileInfo(packed).Length >= part.CompressedSize) TryDelete(packed);
        }
    }

    /// <summary>取り込みの結果</summary>
    public record ImportResult(int Extracted, int Unchanged, int SkippedBody, int SkippedNewer, int Excluded, List<string> PartKeys)
    {
        public static ImportResult Empty => new(0, 0, 0, 0, 0, []);
        public ImportResult Add(ImportResult o) => new(Extracted + o.Extracted, Unchanged + o.Unchanged, SkippedBody + o.SkippedBody,
            SkippedNewer + o.SkippedNewer, Excluded + o.Excluded, [.. PartKeys, .. o.PartKeys]);
        public string Summary => $"展開 {Extracted:#,0} 件 / 同じもの {Unchanged:#,0} 件"
            + (SkippedNewer > 0 ? $" / 手元の方が新しい {SkippedNewer:#,0} 件" : "")
            + (SkippedBody > 0 ? $" / 本体にあるもの {SkippedBody:#,0} 件" : "")
            + (Excluded > 0 ? $" / 取り込まない画像 {Excluded:#,0} 件" : "");
    }

    /// <summary>
    /// zip を追加画像の保存先に展開する。中に zip が入っていれば（全まとめ）、それぞれを展開する。
    /// 本体の resources にある名前（公式の版を隠してしまう）、取り込まない画像、手元の方が新しいものは飛ばす。
    /// 全まとめの画像は数が多いので、取り込み記録（F）には書かない
    /// </summary>
    public static ImportResult ImportZip(string zipPath, ImageState state, string bodyResources, string workDir, Action<string> log, IProgress<(long done, long total)>? progress = null, int depth = 0)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.Latin1);
        var files = zip.Entries
            .Select(e => (Name: ImageFetcher.DecodeName(e.FullName).Replace('\\', '/'), Entry: e))
            .Where(f => !f.Name.EndsWith('/'))
            .ToList();
        var innerZips = files.Where(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (depth == 0 && innerZips.Count > 0 && innerZips.Count * 2 >= files.Count)
        {
            var total = ImportResult.Empty;
            foreach (var (name, entry) in innerZips)
            {
                string temp = Path.Combine(workDir, "全まとめ_" + SafeName(name));
                Directory.CreateDirectory(workDir);
                try
                {
                    log($"  {name}（{entry.Length / 1e6:#,0.0} MB）を展開中...");
                    entry.ExtractToFile(temp, true);
                    var r = ImportZip(temp, state, bodyResources, workDir, log, progress, depth + 1);
                    log($"  {name}: {r.Summary}");
                    total = total.Add(r with { PartKeys = [$"{name.Trim('/')}|{entry.Length}|{entry.Crc32:x8}"] });
                }
                finally
                {
                    TryDelete(temp);
                }
            }
            return total;
        }

        string root = state.ResourcesDir;
        Directory.CreateDirectory(root);
        var names = files.Select(f => f.Name).ToList();
        // 全まとめの中の zip は「resources/キャラ/画像」。それ以外の包みは Dropbox と同じ判定で剥がす
        bool resourcesPrefix = names.Count > 0 && names.All(n => n.StartsWith("resources/", StringComparison.OrdinalIgnoreCase));
        bool strip = resourcesPrefix || ImageFetcher.ShouldStrip(names);

        int extracted = 0, unchanged = 0, skippedBody = 0, skippedNewer = 0, excluded = 0, done = 0;
        foreach (var (name, entry) in files)
        {
            progress?.Report((++done, files.Count));
            string? dest = ImageFetcher.DestPath(root, name, strip);
            if (dest == null) continue;
            string rel = ImageFetcher.RelOf(root, dest);
            if (File.Exists(Path.Combine(bodyResources, rel.Replace('/', Path.DirectorySeparatorChar)))) { skippedBody++; continue; }
            if (state.IsExcluded(rel, entry.Length)) { excluded++; continue; }
            var info = new FileInfo(dest);
            DateTime entryTime = entry.LastWriteTime.DateTime;
            if (info.Exists)
            {
                if (info.Length == entry.Length && Math.Abs((info.LastWriteTime - entryTime).TotalSeconds) <= 2) { unchanged++; continue; }
                // 古いまとめで、後から Dropbox で入った新しい版を戻さない
                if (info.LastWriteTime > entryTime.AddSeconds(2)) { skippedNewer++; continue; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string temp = PathUtil.TempPathFor(dest);
            entry.ExtractToFile(temp, true);
            PathUtil.ReplaceWith(temp, dest);
            extracted++;
        }
        return new ImportResult(extracted, unchanged, skippedBody, skippedNewer, excluded, []);
    }

    /// <summary>追加画像の保存先の画像の数（limit に達したらそこで数えるのをやめる）</summary>
    public static int CountImages(string resourcesDir, int limit)
    {
        if (!Directory.Exists(resourcesDir)) return 0;
        int n = 0;
        foreach (var _ in Directory.EnumerateFiles(resourcesDir, "*", SearchOption.AllDirectories))
            if (++n >= limit) break;
        return n;
    }

    /// <summary>
    /// 全まとめを取り込む。onlyChanged なら取り込み済みの中の zip（名前・サイズ・CRC が同じもの）は飛ばす
    /// </summary>
    public static async Task<ImportResult> DownloadAndImport(BasePackLink link, List<BasePackPart> parts, ImageState state, string statePath,
        string bodyResources, string workDir, Action<string> log, IProgress<(long done, long total)> progress)
    {
        long need = parts.Max(p => p.CompressedSize + p.Size) + 200_000_000;
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(workDir))!).AvailableFreeSpace;
        if (free < need) throw new IOException($"作業フォルダのドライブの空きが足りません（必要 {need / 1e9:0.0} GB / 空き {free / 1e9:0.0} GB）");

        var total = ImportResult.Empty;
        long all = parts.Sum(p => p.CompressedSize), before = 0;
        foreach (var part in parts.OrderBy(p => p.CompressedSize))
        {
            log($"全まとめ: {part.Name}（{part.CompressedSize / 1e6:#,0.0} MB）をダウンロード中...");
            long offset = before;
            string inner = await DownloadPart(link, part, workDir, new Progress<(long done, long total)>(p => progress.Report((offset + p.done, all))));
            try
            {
                var r = await Task.Run(() => ImportZip(inner, state, bodyResources, workDir, log, depth: 1));
                log($"  {part.Name}: {r.Summary}");
                total = total.Add(r);
                state.BaseParts.Add(part.Key);
                state.Save(statePath);
            }
            finally
            {
                TryDelete(inner);
            }
            before += part.CompressedSize;
        }
        return total with { PartKeys = parts.Select(p => p.Key).ToList() };
    }

    // ---- HTTP（範囲指定） ----

    // from が負なら末尾から。中身と全体の大きさを返す
    private static async Task<(byte[] Data, long Total)> GetRange(string url, long from, long to) =>
        await Http.Retry("全まとめの一覧取得", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = from < 0 ? new RangeHeaderValue(null, -from) : new RangeHeaderValue(from, to);
            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not long total)
                throw new InvalidDataException("ギガファイル便からファイルを取得できませんでした（ダウンロード期限切れか、DL パスが変わった可能性があります）");
            return (await response.Content.ReadAsByteArrayAsync(), total);
        });

    // url の from から length バイトを path に落とす。path に途中まであれば続きから取る
    private static async Task DownloadRange(string url, long from, long length, string path, IProgress<(long done, long total)> progress)
    {
        int[] waits = [3, 10, 30, 60, 60];
        for (int attempt = 0; ; attempt++)
        {
            long have = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (have >= length) return;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(from + have, from + length - 1);
                using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new InvalidDataException("ギガファイル便が範囲指定に応じませんでした（ダウンロード期限切れの可能性があります）");
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 20);
                var buffer = new byte[1 << 20];
                using var stall = new CancellationTokenSource();
                while (have < length)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(60));
                    int read = await src.ReadAsync(buffer, stall.Token);
                    if (read == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    have += read;
                    progress.Report((have, length));
                }
                if (have >= length) return;
                throw new IOException($"途中で切れました（{have:#,0} / {length:#,0} バイト）");
            }
            catch (Exception ex) when (attempt < waits.Length && ex is not InvalidDataException)
            {
                Http.Log?.Invoke($"  ダウンロードが途切れたので {waits[attempt]} 秒後に続きから取ります: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(waits[attempt]));
            }
        }
    }

    private static int LastIndexOf(byte[] data, byte[] pattern)
    {
        for (int i = data.Length - pattern.Length; i >= 0; i--)
        {
            int k = 0;
            while (k < pattern.Length && data[i + k] == pattern[k]) k++;
            if (k == pattern.Length) return i;
        }
        return -1;
    }

    private static string SafeName(string name) => string.Concat(Path.GetFileName(name.Replace('/', '\\')).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 作業フォルダに残るだけ */ }
    }
}

/// <summary>zip と同じ CRC-32</summary>
static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Of(string path)
    {
        uint crc = 0xFFFFFFFFu;
        var buffer = new byte[1 << 20];
        using var fs = File.OpenRead(path);
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read; i++) crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
