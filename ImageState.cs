using System.Text;

namespace erablue_resort同期アプリ;

/// <summary>
/// 追加画像の取り込み記録（同期アプリ_取込済み画像.txt）。
///   #resources=取り込み先            … 取り込み先が変わったら記録は無効
///   名前|サイズ                      … 取り込み済みの Dropbox の zip
///   F|相対パス|サイズ|zip名           … zip から展開したファイル（手元で消されたかを見分けるため）
///   X|相対パス|サイズ|zip名           … 取り込まない画像の「版」
///   G|中のzip名|サイズ|CRC            … 取り込み済みの全まとめ（ギガファイル便）の中の zip
///   N|ファイルID                      … 「新しい全まとめがあります」と知らせ済みのアップロード
/// 取り込まないのは「その名前でそのサイズのもの」だけ。後から同じ名前で中身の違う版
/// （正式な画像など）が来たら普通に取り込む
/// </summary>
class ImageState
{
    private const string Header = "#resources=";

    public string ResourcesDir { get; }
    public HashSet<string> Zips { get; } = new(StringComparer.Ordinal);
    // 相対パス（/ 区切り・小文字比較）→ (サイズ, 由来の zip)
    public Dictionary<string, (long Size, string Zip)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (long Size, string Zip)> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);
    // 取り込み済みの全まとめの中の zip（名前|サイズ|CRC）
    public HashSet<string> BaseParts { get; } = new(StringComparer.Ordinal);
    public string BaseNotified { get; set; } = "";

    private ImageState(string resourcesDir) => ResourcesDir = Path.GetFullPath(resourcesDir);

    public static string ZipKey(string name, long bytes) => $"{name}|{bytes}";
    private static string VersionKey(string rel, long size) => $"{rel}|{size}";

    public bool IsExcluded(string rel, long size) => Excluded.ContainsKey(VersionKey(rel, size));

    public static ImageState Load(string statePath, string resourcesDir, Action<string>? log = null)
    {
        var s = new ImageState(resourcesDir);
        if (!File.Exists(statePath)) return s;
        var lines = File.ReadAllLines(statePath, Encoding.UTF8);
        string? recorded = lines.FirstOrDefault(l => l.StartsWith(Header));
        // 見出しの無い古い記録は、既定の保存先に取り込んだものとして扱う
        bool same = recorded == null
            ? lines.All(l => l.Length == 0) || SamePath(resourcesDir, Path.Combine(AppContext.BaseDirectory, "resources"))
            : SamePath(recorded[Header.Length..], resourcesDir);
        if (!same)
        {
            log?.Invoke("追加画像の保存先が変わったため、全部取り込み直します");
            return s;
        }
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split('|');
            if (p.Length == 4 && p[0] == "G")
            {
                s.BaseParts.Add(line[2..]);
            }
            else if (p.Length == 2 && p[0] == "N")
            {
                s.BaseNotified = p[1];
            }
            else if (p.Length == 4 && (p[0] == "F" || p[0] == "X") && long.TryParse(p[2], out long size))
            {
                if (p[0] == "F") s.Files[p[1]] = (size, p[3]);
                else s.Excluded[VersionKey(p[1], size)] = (size, p[3]);
            }
            else if (p.Length == 2)
            {
                s.Zips.Add(line);
            }
        }
        return s;
    }

    public void Save(string statePath)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append(ResourcesDir).Append("\r\n");
        foreach (var z in Zips) sb.Append(z).Append("\r\n");
        foreach (var g in BaseParts.Order(StringComparer.Ordinal)) sb.Append("G|").Append(g).Append("\r\n");
        if (BaseNotified.Length > 0) sb.Append("N|").Append(BaseNotified).Append("\r\n");
        foreach (var (rel, v) in Excluded.OrderBy(kv => kv.Key)) sb.Append("X|").Append(rel[..rel.LastIndexOf('|')]).Append('|').Append(v.Size).Append('|').Append(v.Zip).Append("\r\n");
        foreach (var (rel, v) in Files.OrderBy(kv => kv.Key)) sb.Append("F|").Append(rel).Append('|').Append(v.Size).Append('|').Append(v.Zip).Append("\r\n");
        File.WriteAllText(statePath, sb.ToString(), new UTF8Encoding(false));
    }

    public void RecordExtracted(string rel, long size, string zip) => Files[rel] = (size, zip);

    /// <summary>この版を取り込まないようにする（手元のファイルは呼び出し側で消す）</summary>
    public void Exclude(string rel, long size, string zip)
    {
        Excluded[VersionKey(rel, size)] = (size, zip);
        Files.Remove(rel);
    }

    /// <summary>除外を解除する。由来の zip を未取り込みに戻すので、次の取得で取り直される</summary>
    public void Unexclude(string versionKey)
    {
        if (!Excluded.Remove(versionKey, out var v)) return;
        Zips.RemoveWhere(z => z.StartsWith(v.Zip + "|", StringComparison.Ordinal));
    }

    /// <summary>
    /// 展開したはずなのに手元に無いファイル（＝手で消されたもの）。相対パス・サイズ・由来の zip
    /// </summary>
    public List<(string Rel, long Size, string Zip)> FindDeleted()
    {
        var result = new List<(string, long, string)>();
        foreach (var (rel, v) in Files)
        {
            if (!File.Exists(Path.Combine(ResourcesDir, rel.Replace('/', Path.DirectorySeparatorChar)))) result.Add((rel, v.Size, v.Zip));
        }
        return result;
    }

    /// <summary>手で消されたファイルを、取り直すことにする（由来の zip を未取り込みに戻す）</summary>
    public void Refetch(IEnumerable<(string Rel, long Size, string Zip)> deleted)
    {
        foreach (var (rel, _, zip) in deleted)
        {
            Files.Remove(rel);
            Zips.RemoveWhere(z => z.StartsWith(zip + "|", StringComparison.Ordinal));
        }
    }

    /// <summary>画面表示用：取り込まない画像の一覧（キー, 表示）</summary>
    public IEnumerable<(string Key, string Text)> ExcludedForDisplay() =>
        Excluded.OrderBy(kv => kv.Key).Select(kv => (kv.Key, $"{kv.Key[..kv.Key.LastIndexOf('|')]}（{kv.Value.Size:#,0} バイト / {kv.Value.Zip}）"));

    /// <summary>フォルダを移動したときに、記録を新しい場所に付け替える</summary>
    public static void Rebind(string statePath, string oldDir, string newDir)
    {
        if (!File.Exists(statePath)) return;
        var s = Load(statePath, oldDir);
        var moved = new ImageState(newDir);
        foreach (var z in s.Zips) moved.Zips.Add(z);
        foreach (var kv in s.Files) moved.Files[kv.Key] = kv.Value;
        foreach (var kv in s.Excluded) moved.Excluded[kv.Key] = kv.Value;
        foreach (var g in s.BaseParts) moved.BaseParts.Add(g);
        moved.BaseNotified = s.BaseNotified;
        moved.Save(statePath);
    }

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
