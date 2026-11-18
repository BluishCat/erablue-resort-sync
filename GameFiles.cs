using System.Text;

namespace erablue_resort同期アプリ;

/// <summary>
/// 同期で消さない「ゲーム側にだけある」ファイルの登録（指定先からの相対パス）。
/// Files は本体に無いもの、OverOfficial は公式に同じパスのファイルがあってもゲーム側の版を残す（上書きしない）もの
/// </summary>
record GameKeep(IReadOnlyCollection<string> Files, IReadOnlyCollection<string> OverOfficial)
{
    public static readonly GameKeep Empty = new([], []);
}

/// <summary>
/// 前回の同期で本体からゲームへ配ったファイルの一覧（resources・保護対象・指定先の直下を除く。指定先からの相対パス）。
/// ゲーム側にだけあるファイルが「公式が消した・改名した古いもの」（一覧にある）か、
/// 「自分で置いたもの」（一覧に無い）かを見分けるのに使う
/// </summary>
static class DistributedList
{
    public static HashSet<string>? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            set.Add(line.Replace('/', Path.DirectorySeparatorChar));
        }
        return set;
    }

    public static void Save(string path, IEnumerable<string> relPaths)
    {
        var sb = new StringBuilder();
        sb.Append("# 同期アプリが前回ゲームへ配った本体のファイル（自動で書き換わります。消すと次の同期で今の本体から作り直します）\r\n");
        foreach (var p in relPaths.Order(StringComparer.Ordinal)) sb.Append(p.Replace(Path.DirectorySeparatorChar, '/')).Append("\r\n");
        string temp = path + PathUtil.TempSuffix;
        File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>本体の保存先の今の中身から作る（前回の一覧が無いとき用）</summary>
    public static HashSet<string> FromBody(string sourceRoot, List<string> protectedPaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(sourceRoot)) return set;
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(PathUtil.TempSuffix, StringComparison.Ordinal)) continue;
            string relPath = Path.GetRelativePath(sourceRoot, file);
            if (IsTracked(relPath, protectedPaths)) set.Add(relPath);
        }
        return set;
    }

    /// <summary>
    /// 一覧で見分ける対象か。resources は別の仕組み（追加画像の保存先への取り込み）で扱い、
    /// 指定先の直下は元から消さないので除く
    /// </summary>
    public static bool IsTracked(string relPath, List<string> protectedPaths) =>
        !FolderSync.IsLinkable(relPath)
        && Path.GetDirectoryName(relPath) is { Length: > 0 }
        && !PathUtil.IsUnderAny(relPath, protectedPaths);
}

/// <summary>
/// ゲーム側にだけある（前回配ったものでも、残す登録でもない）ファイルを、残すか退避するか選ぶ画面
/// </summary>
class GameOnlyDialog : Form
{
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly List<string> _files;

    /// <summary>残すと選んだもの（OK のとき）</summary>
    public HashSet<string> Kept { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GameOnlyDialog(IEnumerable<string> files, string targetRoot, bool defaultKeep)
    {
        _files = files.Order(StringComparer.OrdinalIgnoreCase).ToList();
        Text = "ゲーム側にだけあるファイル";
        Size = new Size(760, 560);
        MinimumSize = new Size(520, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            Text = $"本体に無いファイルが、ゲームのフォルダに {_files.Count} 件あります。前回の同期で配ったものではないので、自分で置いたファイルと思われます。\n\n"
                + "残すものにチェックを入れてください。\n"
                + "・チェックしたもの：「残す」に登録し、これからの同期で消しません（次からは聞きません。詳細設定で解除できます）\n"
                + "・チェックしないもの：同期で退避フォルダへ移します",
        });
        if (!defaultKeep)
        {
            layout.Controls.Add(Note("この指定先へは初めての同期です。古い版の本体に入っていたファイルかもしれないので、最初はチェックを外してあります。"));
        }
        if (_files.Any(f => Path.GetExtension(f).ToLowerInvariant() is ".erb" or ".erh" or ".csv"))
        {
            layout.Controls.Add(Note("ERB・ERH・CSV を残すと、公式で消えた古いファイルだった場合に、二重定義などでゲームがエラーになることがあります。"
                + "自分で入れたパッチなどでなければ、チェックしないでください。"));
        }

        _list.BeginUpdate();
        foreach (string rel in _files)
        {
            long size = 0;
            try { size = new FileInfo(Path.Combine(targetRoot, rel)).Length; } catch { }
            _list.Items.Add($"{rel}（{FormatSize(size)}）", defaultKeep);
        }
        _list.EndUpdate();
        layout.Controls.Add(_list);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "チェックしたものを残して同期", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "キャンセル（同期をやめる）", AutoSize = true, DialogResult = DialogResult.Cancel };
        var none = new Button { Text = "すべて外す", AutoSize = true };
        var all = new Button { Text = "すべて選ぶ", AutoSize = true };
        all.Click += (_, _) => SetAll(true);
        none.Click += (_, _) => SetAll(false);
        bottom.Controls.AddRange([ok, cancel, none, all]);
        layout.Controls.Add(bottom);

        // 1列なので、足した順がそのまま行になる。一覧だけ伸ばす
        foreach (Control c in layout.Controls)
            layout.RowStyles.Add(c == _list ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        FormClosing += (_, _) =>
        {
            if (DialogResult != DialogResult.OK) return;
            foreach (int i in _list.CheckedIndices) Kept.Add(_files[i]);
        };
    }

    private void SetAll(bool value)
    {
        _list.BeginUpdate();
        for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, value);
        _list.EndUpdate();
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_000_000 ? $"{bytes / 1e6:0.0} MB" : bytes >= 1000 ? $"{bytes / 1e3:0} KB" : $"{bytes} バイト";

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(720, 0), Margin = new Padding(3, 8, 3, 3) };
}
