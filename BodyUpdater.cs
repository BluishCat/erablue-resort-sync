using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace erablue_resort同期アプリ;

/// <summary>
/// 本体（GitHub）で同期元フォルダを更新する。
///   1. 最新のコミットを調べ、前回と同じなら何もしない（ダウンロード 0）
///   2. 前回の版が分かっていれば、変わったファイルだけ取ってくる（compare API）
///   3. それができないとき（初回・変更が多すぎる・API 制限など）は zip を丸ごと取って差分更新
/// </summary>
static class BodyUpdater
{
    // compare API が一度に返すファイル数の上限。これ以上は zip に任せる
    private const int CompareFileLimit = 300;
    // 変わったファイルを同時に取る数
    private const int ParallelDownloads = 6;

    /// <summary>
    /// config.txt の「キー=」以下に並んだ行を読む（OverwriteFolders / NoActionFolders）
    /// </summary>
    public static List<string> LoadSection(string configPath, string key)
    {
        var paths = new List<string>();
        bool inSection = false;
        foreach (var line in File.ReadAllLines(configPath, Encoding.UTF8))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#")) continue;
            if (trimmedLine.Contains('='))
            {
                inSection = trimmedLine == key + "=";
                continue;
            }
            if (inSection) paths.Add(trimmedLine.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        }
        return paths;
    }

    private record Commit(string Sha, DateTime Date, string Message);
    private record RemoteChange(string Status, string Path, string? Previous, string? BlobSha);

    /// <summary>
    /// 本体を更新し、取り込んだ版（コミット）を返す。版が分からない取り方をしたときは null
    /// </summary>
    public static async Task<string?> Run(string basePath, string configPath, string bodyUrl, string? lastCommit, string workDir,
        Action<string> log, IProgress<(long done, long total)> progress)
    {
        var overwritePaths = LoadSection(configPath, "OverwriteFolders");
        var noActionPaths = LoadSection(configPath, "NoActionFolders");
        Directory.CreateDirectory(workDir);

        string zipUrl = ConnectionSettings.ToBodyZipUrl(bodyUrl);
        var repo = ConnectionSettings.ParseGitHub(bodyUrl);
        Commit? head = null;
        if (repo is var (owner, name, branch))
        {
            try
            {
                head = await GetHead(owner, name, branch);
            }
            catch (Exception ex)
            {
                log("GitHub で最新版を確認できませんでした（zip を丸ごとダウンロードします）: " + ex.Message);
            }

            if (head != null)
            {
                bool hasBody = !PathUtil.IsEmptyOrMissing(basePath);
                if (hasBody && string.Equals(lastCommit, head.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    log($"本体は最新です（{Describe(head)}）。ダウンロードは省略しました");
                    return head.Sha;
                }
                if (hasBody && !string.IsNullOrEmpty(lastCommit))
                {
                    try
                    {
                        if (await ApplyCompare(owner, name, lastCommit, head, basePath, workDir, overwritePaths, noActionPaths, log, progress)) return head.Sha;
                    }
                    catch (Exception ex)
                    {
                        log("差分の取り込みに失敗したので、zip を丸ごとダウンロードして整えます: " + ex.Message);
                    }
                }
                else
                {
                    log($"最新: {Describe(head)}");
                    log(hasBody ? "前回の版が分からないので zip を丸ごとダウンロードします（次回からは変わったファイルだけになります）"
                                : "本体がまだ無いので zip を丸ごとダウンロードします");
                }
                // 調べた版そのものを取る（途中で更新されても版がずれない）
                zipUrl = $"https://github.com/{owner}/{name}/archive/{head.Sha}.zip";
            }
        }

        string zipPath = Path.Combine(workDir, "erablue_resort_main.zip");
        log("本体をダウンロード中: " + zipUrl);
        await Http.DownloadFile(zipUrl, zipPath, progress);
        try
        {
            long mb = new FileInfo(zipPath).Length;
            var (updated, deleted) = await Task.Run(() => SyncWithZip(zipPath, basePath, overwritePaths, noActionPaths, log));
            log($"本体の更新が完了しました（zip {mb / 1e6:0.0} MB / 更新 {updated} 件 / 削除 {deleted} 件）");
        }
        finally
        {
            File.Delete(zipPath);
        }
        return head?.Sha;
    }

    private static string Describe(Commit c) => $"{c.Date:M/d HH:mm} {c.Message}（{c.Sha[..7]}）";

    private static async Task<JsonDocument> GetJson(string url, string what)
    {
        string text = await Http.Retry(what, async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.Client.SendAsync(req);
            if ((int)res.StatusCode == 403 || (int)res.StatusCode == 429)
                throw new InvalidOperationException("GitHub の回数制限に達しました（1時間ほどで戻ります）");
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        });
        return JsonDocument.Parse(text);
    }

    private static Commit ReadCommit(JsonElement e)
    {
        var c = e.GetProperty("commit");
        var who = c.TryGetProperty("committer", out var cm) && cm.ValueKind == JsonValueKind.Object ? cm : c.GetProperty("author");
        DateTime date = who.TryGetProperty("date", out var d) && d.TryGetDateTime(out var dt) ? dt.ToLocalTime() : DateTime.MinValue;
        string message = (c.GetProperty("message").GetString() ?? "").Split('\n')[0].Trim();
        return new Commit(e.GetProperty("sha").GetString()!, date, message);
    }

    private static async Task<Commit> GetHead(string owner, string name, string branch)
    {
        using var doc = await GetJson($"https://api.github.com/repos/{owner}/{name}/commits/{Uri.EscapeDataString(branch)}", "最新版の確認");
        return ReadCommit(doc.RootElement);
    }

    /// <summary>
    /// 前回の版から今の版までに変わったファイルだけを取り込む。zip に任せるべきときは false
    /// </summary>
    private static async Task<bool> ApplyCompare(string owner, string name, string lastCommit, Commit head, string basePath, string workDir,
        List<string> overwritePaths, List<string> noActionPaths, Action<string> log, IProgress<(long done, long total)> progress)
    {
        using var doc = await GetJson($"https://api.github.com/repos/{owner}/{name}/compare/{lastCommit}...{head.Sha}", "差分の確認");
        var root = doc.RootElement;
        string status = root.GetProperty("status").GetString() ?? "";
        if (status == "identical")
        {
            log($"本体は最新です（{Describe(head)}）");
            return true;
        }
        if (status != "ahead")
        {
            log($"配布元の履歴が前回から書き換えられています（{status}）。zip を丸ごと取って整えます");
            return false;
        }

        // 更新内容（新しい順）
        var commits = root.GetProperty("commits").EnumerateArray().Select(ReadCommit).Reverse().ToList();
        int aheadBy = root.TryGetProperty("ahead_by", out var ab) ? ab.GetInt32() : commits.Count;
        var changelog = new StringBuilder($"更新内容（前回から {aheadBy} 件）:");
        foreach (var c in commits.Take(30)) changelog.Append($"\r\n  {c.Date:M/d HH:mm}  {c.Message}");
        if (aheadBy > 30) changelog.Append($"\r\n  …ほか {aheadBy - 30} 件");

        var changes = root.TryGetProperty("files", out var fs)
            ? fs.EnumerateArray().Select(f => new RemoteChange(
                f.GetProperty("status").GetString() ?? "",
                f.GetProperty("filename").GetString()!,
                f.TryGetProperty("previous_filename", out var pf) ? pf.GetString() : null,
                f.TryGetProperty("sha", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null)).ToList()
            : [];
        // どこが変わったか（フォルダ別の件数）。コミットの説明が短いことが多いので添える
        var byFolder = changes.GroupBy(c => c.Path.Contains('/') ? c.Path[..c.Path.IndexOf('/')] : "（直下）")
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        changelog.Append($"\r\n  変わったファイル: {changes.Count} 件（{string.Join(" / ", byFolder.Take(8))}）");
        log(changelog.ToString());

        if (changes.Count >= CompareFileLimit || aheadBy > 250)
        {
            log("変わったファイルが多いので、zip を丸ごとダウンロードします");
            return false;
        }

        var keepPaths = overwritePaths.Concat(noActionPaths).ToList();
        var downloads = new List<(string Rel, string BlobSha)>();
        var deletes = new List<string>();
        foreach (var c in changes)
        {
            switch (c.Status)
            {
                case "removed":
                    deletes.Add(c.Path);
                    break;
                case "renamed":
                    if (c.Previous != null) deletes.Add(c.Previous);
                    goto default;
                case "unchanged":
                    break;
                default: // added / modified / changed / copied
                    if (c.BlobSha == null) throw new InvalidDataException($"{c.Path} の版が分かりません");
                    downloads.Add((c.Path, c.BlobSha));
                    break;
            }
        }
        string Local(string rel)
        {
            string full = Path.GetFullPath(Path.Combine(basePath, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathUtil.IsSameOrUnder(full, basePath) || PathUtil.IsSameOrUnder(basePath, full)) throw new InvalidDataException("おかしなパスです: " + rel);
            return full;
        }
        string Rel(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);
        // 保護対象は触らない。上書き対象（resources 等）は配布元から消えても手元に残す（zip のときと同じ）
        downloads = downloads.Where(d => !PathUtil.IsUnderAny(Rel(d.Rel), noActionPaths)).ToList();
        deletes = deletes.Where(d => !PathUtil.IsUnderAny(Rel(d), keepPaths)).ToList();

        // まず全部を作業フォルダに取ってきて確かめてから、まとめて入れ替える（途中で切れても本体を半端にしない）
        string staging = Path.Combine(workDir, "本体差分");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            long bytes = 0;
            int done = 0;
            await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = ParallelDownloads }, async (d, _) =>
            {
                string url = $"https://raw.githubusercontent.com/{owner}/{name}/{head.Sha}/" + string.Join('/', d.Rel.Split('/').Select(Uri.EscapeDataString));
                byte[] data = await Http.Retry($"{d.Rel} の取得", async () =>
                {
                    byte[] b = await Http.Client.GetByteArrayAsync(url);
                    // git と同じ計算で中身を確かめる（途中で切れた・違う版をつかんだ、を防ぐ）
                    if (!BlobSha(b).Equals(d.BlobSha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{d.Rel} の中身が版と一致しません");
                    return b;
                });
                string staged = Path.Combine(staging, Rel(d.Rel));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await File.WriteAllBytesAsync(staged, data);
                Interlocked.Add(ref bytes, data.Length);
                progress.Report((Interlocked.Increment(ref done), downloads.Count));
            });

            // 消すのが先（大文字小文字だけの名前変更で、新しい方を消さないように）
            int deleted = 0;
            foreach (string rel in deletes)
            {
                string path = Local(rel);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deleted++;
                log("  削除: " + Rel(rel));
                RemoveEmptyParents(Path.GetDirectoryName(path)!, basePath, keepPaths);
            }
            foreach (var d in downloads)
            {
                // 差し替えなので、ハードリンクでつながったゲーム側の画像は同期まで変わらない
                PathUtil.ReplaceWith(Path.Combine(staging, Rel(d.Rel)), Local(d.Rel));
            }
            log($"本体の更新が完了しました（変わったファイルだけ取得: {downloads.Count} 件 {bytes / 1e6:0.00} MB / 削除 {deleted} 件）");
            return true;
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { /* 次回の取得前に消す */ }
        }
    }

    // git のブロブ SHA-1（"blob 大きさ\0" + 中身）
    private static string BlobSha(byte[] data)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {data.Length}\0");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(header);
        sha.AppendData(data);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static void RemoveEmptyParents(string dir, string basePath, List<string> keepPaths)
    {
        while (!PathUtil.IsSameOrUnder(basePath, dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            string rel = Path.GetRelativePath(basePath, dir);
            if (PathUtil.IsUnderAny(rel, keepPaths) || PathUtil.ContainsAny(rel, keepPaths)) return;
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    /// <summary>
    /// 中身が違うファイルだけ書き換え、リポジトリにないファイルは削除する。
    /// NoAction は一切触らず、Overwrite はリポジトリにないファイルを残す（ユーザー追加の画像など）。
    /// </summary>
    private static (int updated, int deleted) SyncWithZip(string zipPath, string basePath, List<string> overwritePaths, List<string> noActionPaths, Action<string> log)
    {
        var keepPaths = overwritePaths.Concat(noActionPaths).ToList();
        var repoFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repoDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int updated = 0, deleted = 0;
        Directory.CreateDirectory(basePath);

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                // 先頭の「リポジトリ名-ブランチ名/」を除いた相対パス
                string relPath = entry.FullName[(entry.FullName.IndexOf('/') + 1)..].Replace('/', Path.DirectorySeparatorChar);
                repoFiles.Add(relPath);
                for (string? dir = Path.GetDirectoryName(relPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) repoDirs.Add(dir);

                if (PathUtil.IsUnderAny(relPath, noActionPaths)) continue;
                string localPath = Path.Combine(basePath, relPath);
                if (File.Exists(localPath) && IsSameContent(entry, localPath)) continue;

                // 一時ファイルに展開してから差し替える（ハードリンク先のゲーム側を直接書き換えない）
                string temp = PathUtil.TempPathFor(localPath);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                entry.ExtractToFile(temp, true);
                PathUtil.ReplaceWith(temp, localPath);
                updated++;
            }
        }

        foreach (string localPath in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories).ToList())
        {
            string relPath = Path.GetRelativePath(basePath, localPath);
            if (repoFiles.Contains(relPath) || PathUtil.IsUnderAny(relPath, keepPaths)) continue;
            File.Delete(localPath);
            log("  削除: " + relPath);
            deleted++;
        }

        foreach (string localDir in Directory.EnumerateDirectories(basePath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            string relDir = Path.GetRelativePath(basePath, localDir);
            if (repoDirs.Contains(relDir) || PathUtil.IsUnderAny(relDir, keepPaths) || PathUtil.ContainsAny(relDir, keepPaths)) continue;
            Directory.Delete(localDir);
        }
        return (updated, deleted);
    }

    private static bool IsSameContent(ZipArchiveEntry entry, string localPath)
    {
        if (new FileInfo(localPath).Length != entry.Length) return false;
        using var zipStream = entry.Open();
        using var fileStream = File.OpenRead(localPath);
        return PathUtil.StreamsEqual(zipStream, fileStream);
    }
}
