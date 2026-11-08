using System.Text;

namespace erablue_resort同期アプリ;

/// <summary>
/// 同期で行う操作の一覧（相対パスは指定先からの相対）。
/// Touched は中身が同じで日時だけ違ったため、日時だけ揃えたファイルの数（次回からは比べずに済む）
/// </summary>
record SyncPlan(List<(string SourcePath, string RelPath)> Copies, List<string> DeleteFiles, List<string> DeleteDirs, int Touched);

/// <summary>同期の結果。Errors が空でなければ一部が終わっていない</summary>
record SyncResult(int Copied, int Linked, int Trashed, int DirsDeleted, List<string> Errors, string? TrashDir);

static class FolderSync
{
    // 退避したファイルを何回分残すか
    public const int TrashGenerations = 10;

    // 設定に関係なく、同期でコピーも削除もしないもの。
    // macro.txt は Emuera がゲームのフォルダ直下に保存するキーマクロ（本体に同じ名前が入っても上書きしない）
    public static readonly string[] AlwaysProtected = ["macro.txt"];

    /// <summary>
    /// 更新アプリの config.txt から NoActionFolders を読む（同期でも触らない場所として共有する）
    /// </summary>
    public static List<string> LoadNoActionPaths(string configPath)
    {
        var noActionPaths = new List<string>();
        bool inNoAction = false;
        foreach (var line in File.ReadAllLines(configPath, Encoding.UTF8))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#")) continue;

            // 「キー=」の行でセクションが切り替わる
            if (trimmedLine.Contains('='))
            {
                inNoAction = trimmedLine == "NoActionFolders=";
                continue;
            }
            if (inNoAction)
            {
                noActionPaths.Add(trimmedLine.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
            }
        }
        foreach (string path in AlwaysProtected)
            if (!noActionPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) noActionPaths.Add(path);
        return noActionPaths;
    }

    // ハードリンクにしてよいのは画像など resources の中だけ。
    // ERB 等はゲーム側で直接編集されることがあり、つながっていると本体側まで書き換わってしまう
    public static bool IsLinkable(string relPath) =>
        relPath.StartsWith("resources" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // 同期後に指定先にあるべきファイル（相対パス → コピー元）
    private static Dictionary<string, string> Desired(string sourceRoot, string resourcesSource, List<string> protectedPaths)
    {
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(PathUtil.TempSuffix, StringComparison.Ordinal)) continue;
            string relPath = Path.GetRelativePath(sourceRoot, file);
            if (PathUtil.IsUnderAny(relPath, protectedPaths)) continue;
            desired[relPath] = file;
        }
        // 同フォルダの resources は本体の resources より優先する
        foreach (string file in Directory.EnumerateFiles(resourcesSource, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(PathUtil.TempSuffix, StringComparison.Ordinal)) continue;
            desired[Path.Combine("resources", Path.GetRelativePath(resourcesSource, file))] = file;
        }
        return desired;
    }

    /// <summary>
    /// 指定先の resources にだけあるファイル（本体にも追加画像の保存先にも無いもの）。指定先からの相対パス。
    /// 本体の resources は上書き対象なので配布元で消されても残る。ここに出るのは、ゲーム側に直接置かれたものか、
    /// 追加画像の保存先から手で消されたもの
    /// </summary>
    public static List<string> FindGameOnlyResources(string sourceRoot, string resourcesSource, string targetRoot, List<string> protectedPaths)
    {
        string targetResources = Path.Combine(targetRoot, "resources");
        if (!Directory.Exists(targetResources) || !Directory.Exists(sourceRoot)) return [];
        var desired = Desired(sourceRoot, resourcesSource, protectedPaths);
        return Directory.EnumerateFiles(targetResources, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(PathUtil.TempSuffix, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(targetRoot, path))
            .Where(relPath => !desired.ContainsKey(relPath) && !PathUtil.IsUnderAny(relPath, protectedPaths))
            .ToList();
    }

    /// <summary>
    /// 指定先の resources にだけあるファイルを、追加画像の保存先へ取り込む（指定先のファイルはそのまま）。
    /// hardLink なら同じ実体にして容量を増やさない（できなければコピー）
    /// </summary>
    public static (int Adopted, List<string> Errors) AdoptGameOnly(IEnumerable<string> relPaths, string targetRoot, string resourcesSource, bool hardLink)
    {
        int adopted = 0;
        var errors = new List<string>();
        bool linkOk = hardLink;
        foreach (string relPath in relPaths)
        {
            try
            {
                string source = Path.Combine(targetRoot, relPath);
                string dest = Path.Combine(resourcesSource, relPath["resources".Length..].TrimStart(Path.DirectorySeparatorChar));
                if (File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!(linkOk && PathUtil.TryHardLink(source, dest)))
                {
                    linkOk = false;
                    PathUtil.CopyReplace(source, dest);
                }
                adopted++;
            }
            catch (Exception ex)
            {
                errors.Add($"{relPath}: {ex.Message}");
            }
        }
        return (adopted, errors);
    }

    /// <summary>
    /// 指定先を「本体＋同フォルダのresources」と一致させるための操作を求める。
    /// protectedPaths の配下はコピーも削除もしない。
    /// </summary>
    public static SyncPlan CreatePlan(string sourceRoot, string resourcesSource, string targetRoot, List<string> protectedPaths)
    {
        var desired = Desired(sourceRoot, resourcesSource, protectedPaths);

        var desiredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relPath in desired.Keys)
        {
            for (string? dir = Path.GetDirectoryName(relPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                desiredDirs.Add(dir);
            }
        }

        int touched = 0;
        var copies = desired
            .Where(kv => !IsSameFile(kv.Value, Path.Combine(targetRoot, kv.Key), ref touched))
            .Select(kv => (kv.Value, kv.Key))
            .ToList();

        // 指定先が新規作成なら消すものはない
        if (!Directory.Exists(targetRoot)) return new SyncPlan(copies, [], [], touched);

        // 指定先の直下にあるファイルはユーザーが置いたものなので、同期元になくても残す
        var deleteFiles = Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(targetRoot, path))
            .Where(relPath => Path.GetDirectoryName(relPath) != "")
            .Where(relPath => !desired.ContainsKey(relPath) && !PathUtil.IsUnderAny(relPath, protectedPaths))
            .ToList();

        // 深い順に並べて、子から消せるようにする
        var deleteDirs = Directory.EnumerateDirectories(targetRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(targetRoot, path))
            .Where(relDir => !desiredDirs.Contains(relDir) && !PathUtil.IsUnderAny(relDir, protectedPaths) && !PathUtil.ContainsAny(relDir, protectedPaths))
            .OrderByDescending(relDir => relDir.Length)
            .ToList();

        return new SyncPlan(copies, deleteFiles, deleteDirs, touched);
    }

    /// <summary>
    /// 計画どおりに同期する。1件失敗しても残りは続け、失敗したものは Errors に入れて返す。
    /// 消すファイルは trashRoot\日時\ に退避する（直近 TrashGenerations 回分を残す）
    /// </summary>
    public static SyncResult Execute(SyncPlan plan, string targetRoot, bool hardLink, string trashRoot, IProgress<int> progress, Action<string> log)
    {
        var errors = new List<string>();
        int done = 0, copied = 0, linked = 0, trashed = 0, dirsDeleted = 0;
        bool linkOk = hardLink;

        foreach (var (sourcePath, relPath) in plan.Copies)
        {
            try
            {
                string destPath = Path.Combine(targetRoot, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                if (linkOk && IsLinkable(relPath))
                {
                    if (PathUtil.TryHardLink(sourcePath, destPath)) linked++;
                    else
                    {
                        linkOk = false;
                        log("  ハードリンクを作れなかったので、以降は普通にコピーします（同じドライブの NTFS でないと使えません）");
                        PathUtil.CopyReplace(sourcePath, destPath);
                        copied++;
                    }
                }
                else
                {
                    PathUtil.CopyReplace(sourcePath, destPath);
                    copied++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"コピー: {relPath}（{ex.Message}）");
            }
            progress.Report(++done);
        }

        string? trashDir = plan.DeleteFiles.Count > 0 ? Path.Combine(trashRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss")) : null;
        foreach (string relPath in plan.DeleteFiles)
        {
            try
            {
                string to = Path.Combine(trashDir!, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(Path.Combine(targetRoot, relPath), to, overwrite: true);
                trashed++;
            }
            catch (Exception ex)
            {
                errors.Add($"削除: {relPath}（{ex.Message}）");
            }
            progress.Report(++done);
        }

        // ファイルは退避し終わっているので空のはず（失敗が残っていれば消さない）
        foreach (string relDir in plan.DeleteDirs)
        {
            try
            {
                string dir = Path.Combine(targetRoot, relDir);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir);
                    dirsDeleted++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"フォルダ削除: {relDir}（{ex.Message}）");
            }
            progress.Report(++done);
        }

        if (trashDir != null) PruneTrash(trashRoot, log);
        return new SyncResult(copied, linked, trashed, dirsDeleted, errors, trashDir);
    }

    // 古い退避から消す
    private static void PruneTrash(string trashRoot, Action<string> log)
    {
        try
        {
            foreach (var old in Directory.GetDirectories(trashRoot).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(TrashGenerations))
            {
                Directory.Delete(old, true);
            }
        }
        catch (Exception ex)
        {
            log("  古い退避ファイルの片付けに失敗しました: " + ex.Message);
        }
    }

    /// <summary>
    /// 指定先の resources にある「こちらと同じ中身の別の実体」をハードリンクに置き換え、容量の二重使いをなくす。
    /// 同じかどうかは同期と同じく大きさと更新日時で見る（違うものは普通の同期で入れ替わる）
    /// </summary>
    public static (int Linked, long Bytes, List<string> Errors) Relink(string sourceRoot, string resourcesSource, string targetRoot,
        List<string> protectedPaths, IProgress<(int done, int total)> progress)
    {
        var errors = new List<string>();
        int linked = 0, done = 0;
        long bytes = 0;
        var items = Desired(sourceRoot, resourcesSource, protectedPaths).Where(kv => IsLinkable(kv.Key)).ToList();
        foreach (var (relPath, sourcePath) in items)
        {
            if (++done % 500 == 0) progress.Report((done, items.Count));
            string destPath = Path.Combine(targetRoot, relPath);
            var s = new FileInfo(sourcePath);
            var d = new FileInfo(destPath);
            if (!d.Exists || s.Length != d.Length || s.LastWriteTimeUtc != d.LastWriteTimeUtc) continue;
            if (PathUtil.IsSameEntity(sourcePath, destPath)) continue;
            try
            {
                if (!PathUtil.TryHardLink(sourcePath, destPath))
                {
                    errors.Add("ハードリンクを作れませんでした（同じドライブの NTFS でないと使えません）");
                    break;
                }
                linked++;
                bytes += s.Length;
            }
            catch (Exception ex)
            {
                errors.Add($"{relPath}（{ex.Message}）");
            }
        }
        progress.Report((items.Count, items.Count));
        return (linked, bytes, errors);
    }

    // サイズと更新日時が同じなら同一とみなし、日時だけ違う場合は中身を比べる。
    // 中身が同じなら日時を揃えておき、次回からは比べずに済むようにする
    private static bool IsSameFile(string sourcePath, string destPath, ref int touched)
    {
        if (!File.Exists(destPath)) return false;

        var source = new FileInfo(sourcePath);
        var dest = new FileInfo(destPath);
        if (source.Length != dest.Length) return false;
        if (source.LastWriteTimeUtc == dest.LastWriteTimeUtc) return true;

        bool same;
        using (var sourceStream = source.OpenRead())
        using (var destStream = dest.OpenRead())
        {
            same = PathUtil.StreamsEqual(sourceStream, destStream);
        }
        if (same)
        {
            try
            {
                File.SetLastWriteTimeUtc(destPath, source.LastWriteTimeUtc);
                touched++;
            }
            catch
            {
                // 読み取り専用などで揃えられなくても、同じ中身なのでそのままでよい
            }
        }
        return same;
    }
}
