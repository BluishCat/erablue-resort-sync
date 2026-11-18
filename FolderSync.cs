using System.Text;

namespace erablue_resort同期アプリ;

/// <summary>
/// 同期で行う操作の一覧（相対パスは指定先からの相対）。
/// Touched は中身が同じで日時だけ違ったため、日時だけ揃えたファイルの数（次回からは比べずに済む）
/// </summary>
record SyncPlan(List<(string SourcePath, string RelPath)> Copies, List<string> DeleteFiles, List<string> DeleteDirs, int Touched)
{
    // ゲーム側にだけあり、前回配ったものでも残す登録でもないファイル（自分で置いたものと思われるので、消す前に聞く）
    public List<string> UnknownFiles { get; init; } = [];
    // 残す登録のファイルと同じパスに、公式が中身の違うファイルを入れたもの（どちらにするか聞く）
    public List<(string SourcePath, string RelPath)> Conflicts { get; init; } = [];
    // 残す登録のうち、公式に同じ中身のファイルが入ったもの（登録を外してよい）
    public List<string> NowOfficial { get; init; } = [];
    // 残す登録のうち、ゲーム側から無くなっていたもの（登録を外してよい）
    public List<string> MissingKept { get; init; } = [];
    // 同期後に「前回配ったファイル」として覚えておくもの
    public HashSet<string> Distributed { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    // 上書きする前に退避しておくゲーム側のファイル（食い違いで公式版を選んだもの）
    public List<string> BackupBeforeCopy { get; init; } = [];
    // フォルダ削除を計算し直すための材料
    public HashSet<string> DesiredDirs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> KeptFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

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
    /// protectedPaths の配下はコピーも削除もしない。keep に登録したゲーム側のファイルは消さない。
    /// distributedBefore（前回配ったファイル）を渡すと、ゲーム側にだけあって一覧に無いファイルは消さずに UnknownFiles に入れる
    /// （Resolve で残すか消すかを決める）。null なら従来どおり全部消す対象にする
    /// </summary>
    public static SyncPlan CreatePlan(string sourceRoot, string resourcesSource, string targetRoot, List<string> protectedPaths,
        GameKeep? keep = null, HashSet<string>? distributedBefore = null)
    {
        keep ??= GameKeep.Empty;
        var keepFiles = new HashSet<string>(keep.Files, StringComparer.OrdinalIgnoreCase);
        var keepOver = new HashSet<string>(keep.OverOfficial, StringComparer.OrdinalIgnoreCase);
        var desired = Desired(sourceRoot, resourcesSource, protectedPaths);

        var desiredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relPath in desired.Keys)
        {
            for (string? dir = Path.GetDirectoryName(relPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                desiredDirs.Add(dir);
            }
        }
        var distributed = new HashSet<string>(desired.Keys.Where(k => DistributedList.IsTracked(k, protectedPaths)), StringComparer.OrdinalIgnoreCase);

        int touched = 0;
        var copies = new List<(string SourcePath, string RelPath)>();
        var conflicts = new List<(string SourcePath, string RelPath)>();
        var nowOfficial = new List<string>();
        foreach (var (relPath, sourcePath) in desired)
        {
            string destPath = Path.Combine(targetRoot, relPath);
            bool exists = File.Exists(destPath);
            // 公式版より優先すると決めたゲーム側の版は上書きしない
            if (exists && keepOver.Contains(relPath)) continue;
            bool same = IsSameFile(sourcePath, destPath, ref touched);
            if (exists && keepFiles.Contains(relPath))
            {
                if (same) nowOfficial.Add(relPath);
                else conflicts.Add((sourcePath, relPath));
                continue;
            }
            if (!same) copies.Add((sourcePath, relPath));
        }
        var missingKept = keepFiles.Concat(keepOver).Where(rel => !File.Exists(Path.Combine(targetRoot, rel))).ToList();

        // 指定先が新規作成なら消すものはない
        if (!Directory.Exists(targetRoot))
            return new SyncPlan(copies, [], [], touched) { Conflicts = conflicts, NowOfficial = nowOfficial, MissingKept = missingKept, Distributed = distributed, DesiredDirs = desiredDirs };

        // 指定先の直下にあるファイルはユーザーが置いたものなので、同期元になくても残す
        var deleteFiles = new List<string>();
        var unknown = new List<string>();
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(targetRoot, path);
            if (Path.GetDirectoryName(relPath) == "" || desired.ContainsKey(relPath) || PathUtil.IsUnderAny(relPath, protectedPaths)) continue;
            if (keepFiles.Contains(relPath) || keepOver.Contains(relPath)) kept.Add(relPath);
            // 前回配ったもの（公式が消した・改名した古いもの）と resources は従来どおり消す
            else if (distributedBefore == null || !DistributedList.IsTracked(relPath, protectedPaths) || distributedBefore.Contains(relPath)) deleteFiles.Add(relPath);
            else unknown.Add(relPath);
        }

        var deleteDirs = DeleteDirs(targetRoot, desiredDirs, protectedPaths, kept.Concat(unknown));
        return new SyncPlan(copies, deleteFiles, deleteDirs, touched)
        {
            UnknownFiles = unknown,
            Conflicts = conflicts,
            NowOfficial = nowOfficial,
            MissingKept = missingKept,
            Distributed = distributed,
            DesiredDirs = desiredDirs,
            KeptFiles = kept,
        };
    }

    /// <summary>
    /// 聞いた結果を計画に反映する。keepUnknown は残すと答えたもの（ほかの UnknownFiles は退避する）、
    /// useOfficial は公式版にすると答えた食い違い（ゲーム側の版を退避してから上書き。ほかはゲーム側の版を残す）
    /// </summary>
    public static SyncPlan Resolve(SyncPlan plan, string targetRoot, List<string> protectedPaths, ISet<string> keepUnknown, ISet<string> useOfficial)
    {
        var official = plan.Conflicts.Where(c => useOfficial.Contains(c.RelPath)).ToList();
        var kept = new HashSet<string>(plan.KeptFiles.Concat(plan.UnknownFiles.Where(keepUnknown.Contains)), StringComparer.OrdinalIgnoreCase);
        return plan with
        {
            Copies = [.. plan.Copies, .. official],
            DeleteFiles = [.. plan.DeleteFiles, .. plan.UnknownFiles.Where(rel => !keepUnknown.Contains(rel))],
            DeleteDirs = Directory.Exists(targetRoot) ? DeleteDirs(targetRoot, plan.DesiredDirs, protectedPaths, kept) : [],
            BackupBeforeCopy = [.. plan.BackupBeforeCopy, .. official.Select(c => c.RelPath)],
            UnknownFiles = [],
            Conflicts = [],
            KeptFiles = kept,
        };
    }

    // 指定先のフォルダのうち、同期後に要らなくなるもの。深い順に並べて、子から消せるようにする
    private static List<string> DeleteDirs(string targetRoot, HashSet<string> desiredDirs, List<string> protectedPaths, IEnumerable<string> keptFiles)
    {
        var keepDirs = new HashSet<string>(desiredDirs, StringComparer.OrdinalIgnoreCase);
        foreach (string relPath in keptFiles)
        {
            for (string? dir = Path.GetDirectoryName(relPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                keepDirs.Add(dir);
            }
        }
        return Directory.EnumerateDirectories(targetRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(targetRoot, path))
            .Where(relDir => !keepDirs.Contains(relDir) && !PathUtil.IsUnderAny(relDir, protectedPaths) && !PathUtil.ContainsAny(relDir, protectedPaths))
            .OrderByDescending(relDir => relDir.Length)
            .ToList();
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
        string? trashDir = plan.DeleteFiles.Count + plan.BackupBeforeCopy.Count > 0 ? Path.Combine(trashRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss")) : null;

        // 公式版で上書きするゲーム側のファイルは、先に退避フォルダへ写しておく（写せなければ上書きしない）
        var backupFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relPath in plan.BackupBeforeCopy)
        {
            try
            {
                string to = Path.Combine(trashDir!, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(targetRoot, relPath), to, overwrite: true);
                trashed++;
            }
            catch (Exception ex)
            {
                backupFailed.Add(relPath);
                errors.Add($"退避: {relPath}（{ex.Message}。上書きしませんでした）");
            }
        }

        foreach (var (sourcePath, relPath) in plan.Copies)
        {
            if (backupFailed.Contains(relPath))
            {
                progress.Report(++done);
                continue;
            }
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
