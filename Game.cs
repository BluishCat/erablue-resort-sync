using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace erablue_resort同期アプリ;

/// <summary>ゲーム（指定先の exe）の起動と、起動中かどうかの確認</summary>
static class Game
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>指定先のフォルダの中にある exe で、いま動いているもの（ゲーム・ツール等）</summary>
    public static List<string> FindRunning(string targetRoot)
    {
        var result = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string? path = ImagePath(p.Id);
                if (path != null && PathUtil.IsSameOrUnder(path, targetRoot)) result.Add(Path.GetFileName(path));
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> ListExes(string targetRoot) =>
        Directory.Exists(targetRoot)
            ? Directory.GetFiles(targetRoot, "*.exe").Select(f => Path.GetFileName(f)!).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    // 「Emuera.NET 1824+v24+EMv18+EEv55」→「Emuera.NET」（版の番号を除いた系統名）
    private static string Family(string exe) => Regex.Match(Path.GetFileNameWithoutExtension(exe), @"^[A-Za-z.]+").Value;

    /// <summary>
    /// 起動する exe を決める。保存した名前が無くなっていたら（版が上がって名前が変わった等）
    /// 同じ系統で一番新しいもの。それも無ければ Emuera で始まるもの
    /// </summary>
    public static string? Resolve(string targetRoot, string saved)
    {
        var exes = ListExes(targetRoot);
        if (exes.Count == 0) return null;
        string? exact = exes.FirstOrDefault(e => e.Equals(saved, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        string family = saved.Length > 0 ? Family(saved) : "";
        if (family.Length > 0)
        {
            string? same = exes.Where(e => Family(e).Equals(family, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => File.GetLastWriteTime(Path.Combine(targetRoot, e))).FirstOrDefault();
            if (same != null) return same;
        }
        return exes.FirstOrDefault(e => e.StartsWith("Emuera", StringComparison.OrdinalIgnoreCase)) ?? exes[0];
    }

    public static void Launch(string targetRoot, string exe) =>
        Process.Start(new ProcessStartInfo(Path.Combine(targetRoot, exe)) { WorkingDirectory = targetRoot, UseShellExecute = true });
}
