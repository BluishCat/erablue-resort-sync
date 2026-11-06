using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace erablue_resort同期アプリ;

static class Http
{
    // Dropbox の一覧取得で Cookie（t トークン）を使う
    public static readonly CookieContainer Cookies = new();
    public static readonly HttpClient Client = CreateClient();
    // やり直したときに知らせる先（画面のログ）
    public static Action<string>? Log;

    // 一時的なネットの不調はこの間隔でやり直す
    private static readonly int[] RetryDelaysSeconds = [3, 10, 30];
    // これだけ何も届かなければ途中で止まったとみなす
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { CookieContainer = Cookies, UseCookies = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        // Dropbox はブラウザ以外の UA だと画面の返し方が変わることがあるので、ブラウザと同じ形にする
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 erablue_resort-sync/1.0");
        return client;
    }

    /// <summary>一時的な失敗（切断・タイムアウト・5xx・429）なら少し待ってやり直す</summary>
    public static async Task<T> Retry<T>(string what, Func<Task<T>> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < RetryDelaysSeconds.Length && IsTransient(ex))
            {
                int wait = RetryDelaysSeconds[attempt];
                Log?.Invoke($"  {what}に失敗したので {wait} 秒後にやり直します（{attempt + 1}/{RetryDelaysSeconds.Length}回目）: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(wait));
            }
        }
    }

    public static Task Retry(string what, Func<Task> action) => Retry(what, async () => { await action(); return true; });

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode is null or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)h.StatusCode >= 500,
        OperationCanceledException => true, // HttpClient のタイムアウト・途中停止
        InvalidDataException => true,       // 途中で切れた・中身が合わない
        IOException => true,
        _ => false,
    };

    public static Task<string> GetString(string url, string what = "取得") => Retry(what, () => Client.GetStringAsync(url));

    /// <summary>
    /// ダウンロードして destPath に保存する。途中で切れたら最初からやり直す。
    /// expectedSize が分かっていれば、その大きさでなければ失敗とする
    /// </summary>
    public static Task DownloadFile(string url, string destPath, IProgress<(long done, long total)> progress, long expectedSize = -1) =>
        Retry("ダウンロード", async () =>
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            long total = length ?? (expectedSize > 0 ? expectedSize : 0);
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var dest = File.Create(destPath))
            {
                var buffer = new byte[1 << 20];
                using var stall = new CancellationTokenSource();
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read = await source.ReadAsync(buffer, stall.Token);
                    if (read == 0) break;
                    await dest.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    progress.Report((done, total));
                }
            }
            if (length is long l && done != l) throw new InvalidDataException($"途中で切れました（{done:#,0} / {l:#,0} バイト）");
            if (expectedSize >= 0 && done != expectedSize) throw new InvalidDataException($"大きさが一覧と違います（{done:#,0} / {expectedSize:#,0} バイト）");
        });
}

static class PathUtil
{
    // 書きかけのファイルに付ける印。同期ではこの名前のファイルを運ばない
    public const string TempSuffix = ".synctmp~";
    public static string TempPathFor(string destPath) => destPath + TempSuffix;

    // relPath が paths のいずれかと一致するか、その配下にあるか
    public static bool IsUnderAny(string relPath, IEnumerable<string> paths) =>
        paths.Any(p => relPath.Equals(p, StringComparison.OrdinalIgnoreCase)
            || relPath.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    // ディレクトリ relDir の配下に paths のいずれかがあるか
    public static bool ContainsAny(string relDir, IEnumerable<string> paths) =>
        paths.Any(p => p.StartsWith(relDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    public static bool IsSameOrUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEmptyOrMissing(string dir) => !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 書き上がった一時ファイルで destPath を差し替える。
    /// 上書きではなく差し替えなので、ハードリンクでつながったゲーム側のファイルは同期までそのまま
    /// </summary>
    public static void ReplaceWith(string tempPath, string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Move(tempPath, destPath, overwrite: true);
    }

    /// <summary>コピーしてから差し替える（既存のファイルの中身を直接書き換えない）</summary>
    public static void CopyReplace(string source, string dest)
    {
        string temp = TempPathFor(dest);
        File.Copy(source, temp, true);
        ReplaceWith(temp, dest);
    }

    /// <summary>フォルダの中身を丸ごと移す。同じドライブなら一瞬、別ドライブならコピーしてから消す</summary>
    public static void MoveDirectory(string from, string to, Action<string> log)
    {
        from = Path.GetFullPath(from);
        to = Path.GetFullPath(to);
        if (SameVolume(from, to))
        {
            if (Directory.Exists(to)) Directory.Delete(to); // 空であることは呼び出し側で確認済み
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            return;
        }
        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList();
        int done = 0;
        foreach (string file in files)
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
            if (++done % 2000 == 0) log($"  移動中 {done}/{files.Count}");
        }
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        Directory.Delete(from, true);
    }

    public static bool StreamsEqual(Stream a, Stream b)
    {
        byte[] bufA = new byte[81920];
        byte[] bufB = new byte[81920];
        while (true)
        {
            int readA = a.ReadAtLeast(bufA, bufA.Length, throwOnEndOfStream: false);
            int readB = b.ReadAtLeast(bufB, bufB.Length, throwOnEndOfStream: false);
            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readB))) return false;
        }
    }

    // ---- ハードリンク ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    // 260 文字を超えるパスも扱えるようにする
    private static string Long(string path)
    {
        path = Path.GetFullPath(path);
        return path.Length >= 250 && !path.StartsWith(@"\\?\") ? @"\\?\" + path : path;
    }

    /// <summary>
    /// dest を source のハードリンクにする（一時名で作ってから差し替える）。
    /// 作れない場所（別ドライブ・FAT 等）なら false
    /// </summary>
    public static bool TryHardLink(string source, string dest)
    {
        string temp = TempPathFor(dest);
        if (File.Exists(temp)) File.Delete(temp);
        if (!CreateHardLink(Long(temp), Long(source), IntPtr.Zero)) return false;
        try
        {
            File.Move(temp, dest, overwrite: true);
            return true;
        }
        catch
        {
            try { File.Delete(temp); } catch { /* 残っても同期では運ばない */ }
            throw;
        }
    }

    public static (uint Volume, ulong Index, uint Links)? FileId(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(h, out var info)) return null;
            return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>2つのパスが同じ実体（ハードリンク）か</summary>
    public static bool IsSameEntity(string a, string b)
    {
        var x = FileId(a);
        if (x == null || x.Value.Links < 2) return false; // リンクが1つなら別の実体
        var y = FileId(b);
        return y != null && x.Value.Volume == y.Value.Volume && x.Value.Index == y.Value.Index;
    }
}
