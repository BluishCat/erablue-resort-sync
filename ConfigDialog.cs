using System.Text;

namespace erablue_resort同期アプリ;

/// <summary>
/// 設定ファイル config.txt の中身。更新アプリと共有しているので書式は元のまま、
/// 同期アプリ用の項目は最初の見出し（OverwriteFolders=）より前に置く。
/// 更新アプリは見出し前の知らない行を読み飛ばすので、旧更新アプリでもそのまま読める。
/// ファイル上のパスは「/」区切りで持つ
/// </summary>
class UpdaterConfig
{
    public static readonly string[] DefaultOverwrite = ["resources"];
    public static readonly string[] DefaultNoAction = ["sav", "emuera.config", "CSV_EDITFILE", "CSV/ローカル追加用CSV", "ERB_EDITFILE", "ERB/ローカル改造用ERB"];

    public string SourceUrl { get; set; } = ConnectionSettings.DefaultBodyUrl;
    public List<string> OverwriteFolders { get; set; } = [.. DefaultOverwrite];
    public List<string> NoActionFolders { get; set; } = [.. DefaultNoAction];
    // 同期アプリ用の「キー=値」（書いた順に保つ）
    public List<KeyValuePair<string, string>> Extras { get; set; } = [];

    public string? Get(string key) => Extras.Where(kv => kv.Key == key).Select(kv => kv.Value).FirstOrDefault();

    public static UpdaterConfig Load(string path)
    {
        var c = new UpdaterConfig();
        if (!File.Exists(path)) return c;
        c.OverwriteFolders.Clear();
        c.NoActionFolders.Clear();
        List<string>? current = null;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (t.StartsWith("SourceUrl=")) { c.SourceUrl = t["SourceUrl=".Length..].Trim(); current = null; }
            else if (t == "OverwriteFolders=") current = c.OverwriteFolders;
            else if (t == "NoActionFolders=") current = c.NoActionFolders;
            else if (t.Contains('='))
            {
                int eq = t.IndexOf('=');
                c.Extras.Add(new(t[..eq].Trim(), t[(eq + 1)..].Trim()));
                current = null;
            }
            else current?.Add(t);
        }
        return c;
    }

    // 更新アプリが読める元の書式のまま書き出す
    public void Save(string path)
    {
        var sb = new StringBuilder();
        sb.Append("# ダウンロード先URL\r\n");
        sb.Append("SourceUrl=").Append(SourceUrl).Append("\r\n\r\n");
        if (Extras.Count > 0)
        {
            sb.Append("# ---- 同期アプリの設定（更新アプリはここを読み飛ばします）----\r\n");
            foreach (var (key, comment) in ExtraComments)
            {
                if (comment != null) sb.Append("# ").Append(comment).Append("\r\n");
                foreach (var kv in Extras.Where(kv => kv.Key == key)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            }
            foreach (var kv in Extras.Where(kv => !ExtraComments.Any(e => e.Key == kv.Key))) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            sb.Append("\r\n");
        }
        sb.Append("# 上書き対象フォルダ\r\nOverwriteFolders=\r\n");
        foreach (var p in OverwriteFolders) sb.Append(p).Append("\r\n");
        sb.Append("\r\n# 保護対象フォルダ/ファイル\r\nNoActionFolders=\r\n");
        sb.Append(string.Join("\r\n", NoActionFolders));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // 書き出す順番と、その前に付ける説明
    private static readonly (string Key, string? Comment)[] ExtraComments =
    [
        ("TargetDir", "同期先（ゲームの erablue_resort フォルダ）"),
        ("ImageUrl", "追加画像の取得元（Dropbox 共有リンク）と、Wiki から自動で探すか"),
        ("ImageAutoFromWiki", null),
        ("BodyDir", "保存先（空なら既定。相対パスはこのフォルダ基準）"),
        ("ResourcesDir", null),
        ("WorkDir", null),
        ("HardLinkResources", "画像（resources）をゲームへコピーせず、ハードリンクにする（容量を二重に使わない）"),
        ("GameExe", "まとめて更新の後に起動するゲーム（指定先からの相対パス）と、起動するか"),
        ("LaunchAfterUpdate", null),
        ("BodyCommit", "前回取り込んだ本体の版（自動で書き換わります。空にすると次回は丸ごと取り直します）"),
    ];

    /// <summary>入力行を整えて重複を除く。使えない行があれば理由を返す</summary>
    public static (List<string> paths, string? error) Normalize(IEnumerable<string> lines, string label)
    {
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var p = raw.Trim().Replace('\\', '/').Trim('/');
            if (p.Length == 0 || p.StartsWith('#')) continue;
            if (Path.IsPathRooted(p) || p.Contains(':'))
                return (result, $"{label}の「{raw.Trim()}」は erablue_resort フォルダからの相対パスで書いてください。");
            if (p.Split('/').Any(part => part is "." or ".."))
                return (result, $"{label}の「{raw.Trim()}」に「..」は使えません。");
            if (p.Contains('='))
                return (result, $"{label}の「{raw.Trim()}」に「=」は使えません。");
            if (!result.Contains(p, StringComparer.OrdinalIgnoreCase)) result.Add(p);
        }
        return (result, null);
    }
}

/// <summary>
/// config.txt を画面で編集するダイアログ
/// </summary>
class ConfigDialog : Form
{
    private readonly string _configPath;
    private readonly string _appDir;
    private readonly string _targetRoot;
    private readonly ConnectionSettings _original;
    private readonly TextBox _urlBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _bodyDirBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _resDirBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _workDirBox = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _hardLinkCheck = new() { Text = "画像（resources）をハードリンクで同期する", AutoSize = true };
    private readonly CheckBox _appUpdateCheck = new() { Text = "起動時にアプリの新しい版を確認する", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };

    /// <summary>保存後の設定（取得元・保存先を反映済み）</summary>
    public ConnectionSettings Result { get; private set; }
    private readonly TextBox _overwriteBox = NewListBox();
    private readonly TextBox _noActionBox = NewListBox();

    // 取り込まない画像（保存を押したときに反映する）
    private readonly string _statePath;
    private readonly ImageState _imageState;
    private readonly ListBox _excludedList = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
    private readonly List<string> _excludedKeys = [];
    private readonly List<(string Full, string Rel, long Size, string Zip)> _toExclude = [];
    private readonly List<string> _toUnexclude = [];

    public string SourceUrl => _urlBox.Text.Trim();

    // フォルダ選択で相対パスを求める基準（本体の保存先・指定先）
    private string[] Roots =>
        new[] { Draft().BodyPath(_appDir), _targetRoot }.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath).ToArray();

    public ConfigDialog(string configPath, ConnectionSettings settings, string appDir, string targetRoot, string imageStatePath)
    {
        _configPath = configPath;
        _statePath = imageStatePath;
        _imageState = ImageState.Load(imageStatePath, settings.ResourcesPath(appDir));
        _appDir = appDir;
        _targetRoot = targetRoot;
        _original = settings.Clone();
        Result = settings.Clone();

        Text = "詳細設定";
        Size = new Size(760, 860);
        MinimumSize = new Size(600, 700);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var c = UpdaterConfig.Load(configPath);
        _urlBox.Text = string.IsNullOrWhiteSpace(settings.BodyUrl) ? c.SourceUrl : settings.BodyUrl;
        _bodyDirBox.Text = settings.BodyDir;
        _resDirBox.Text = settings.ResourcesDir;
        _workDirBox.Text = settings.WorkDir;
        _hardLinkCheck.Checked = settings.HardLinkResources;
        _appUpdateCheck.Checked = settings.AppUpdateCheck;
        _bodyDirBox.PlaceholderText = "既定: " + ConnectionSettings.DefaultBodyPath(appDir);
        _resDirBox.PlaceholderText = "既定: " + ConnectionSettings.DefaultResourcesPath(appDir);
        _workDirBox.PlaceholderText = "既定: " + ConnectionSettings.DefaultWorkPath(appDir);
        SetLines(_overwriteBox, c.OverwriteFolders);
        SetLines(_noActionBox, c.NoActionFolders);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddRow(layout, new Label { Text = "保存先", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) }, 2);
        var dirs = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        dirs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddDirRow(dirs, "本体（同期元）", _bodyDirBox, "本体の保存先を選んでください");
        AddDirRow(dirs, "追加画像（resources）", _resDirBox, "追加画像（顔グラ）の保存先を選んでください");
        AddDirRow(dirs, "作業フォルダ", _workDirBox, "ダウンロードしたファイルを一時的に置く場所を選んでください");
        AddRow(layout, dirs, 2);
        AddRow(layout, Note("空欄なら既定の場所。相対パスはアプリのフォルダ基準。作業フォルダには数百 MB の zip を一時的に置き、終わったら消します。\n"
            + "保存先を変えたときは、今の中身を新しい場所へ移すか確認します。", 720), 2);

        AddRow(layout, _hardLinkCheck, 2);
        AddRow(layout, Note("オンにすると、ゲーム側の resources の画像をコピーではなく、こちらの画像と同じ実体（ハードリンク）にします。"
            + "同じ画像を二重に持たなくなり（今はおよそ 2 GB 以上）、同期も速くなります。オンにしたとき、今ある画像もまとめてつなぎ直します。\n"
            + "本体と指定先が同じドライブ（NTFS）のときだけ使えます。ゲーム側の画像を直接編集すると、こちらの画像も同じく変わります（ERB 等はつなぎません）。", 720), 2);

        AddRow(layout, _appUpdateCheck, 2);
        AddRow(layout, Note("オンにすると、起動したときに GitHub でこのアプリの新しい版を確かめ、あれば更新内容を見せて更新するか聞きます。"
            + "オフでも、画面下の「アプリの更新を確認」からいつでも確かめられます。", 720), 2);

        AddRow(layout, new Label { Text = "本体の取得元（SourceUrl）", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold), Margin = new Padding(3, 10, 3, 3) }, 2);
        AddRow(layout, _urlBox, 2);
        AddRow(layout, Note("GitHub のリポジトリ URL（…/tree/ブランチ名 も可）か zip の直リンク", 680), 2);

        int listRow = layout.RowCount;
        layout.Controls.Add(Section("上書き対象（OverwriteFolders）",
            "更新で新しいファイルは入るが、配布元に無いファイルは消さない場所。自分で足した画像を置く resources など", _overwriteBox, false), 0, listRow);
        layout.Controls.Add(Section("保護対象（NoActionFolders）",
            "更新でも同期でも一切触らない場所。セーブ（sav）や自分で改造したファイルなど", _noActionBox, true), 1, listRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowCount++;

        int exRow = layout.RowCount;
        var exPanel = ExcludedSection();
        layout.Controls.Add(exPanel, 0, exRow);
        layout.SetColumnSpan(exPanel, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowCount++;
        RefreshExcludedList();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "保存", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "初期値に戻す", AutoSize = true };
        var open = new Button { Text = "config.txt をメモ帳で開く", AutoSize = true };
        bottom.Controls.AddRange([ok, cancel, reset, open]);
        AddRow(layout, bottom, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        // 空欄の保存先は「既定: …」の表示を見せたいので、最初は入力欄にフォーカスを置かない
        Shown += (_, _) => ActiveControl = ok;

        ok.Click += (_, _) => SaveAndClose();
        reset.Click += (_, _) =>
        {
            _urlBox.Text = ConnectionSettings.DefaultBodyUrl;
            _bodyDirBox.Text = _resDirBox.Text = _workDirBox.Text = "";
            _appUpdateCheck.Checked = true;
            SetLines(_overwriteBox, UpdaterConfig.DefaultOverwrite);
            SetLines(_noActionBox, UpdaterConfig.DefaultNoAction);
        };
        open.Click += (_, _) =>
        {
            if (!File.Exists(_configPath)) new UpdaterConfig().Save(_configPath);
            System.Diagnostics.Process.Start("notepad.exe", $"\"{_configPath}\"");
        };
    }

    private void SaveAndClose()
    {
        var (overwrite, e1) = UpdaterConfig.Normalize(_overwriteBox.Lines, "上書き対象");
        var (noAction, e2) = UpdaterConfig.Normalize(_noActionBox.Lines, "保護対象");
        var draft = Draft();
        var urlCheck = new ConnectionSettings { BodyUrl = SourceUrl, ImageAutoFromWiki = true }.Validate();
        string? error = draft.ValidateDirs(_appDir, _targetRoot) ?? urlCheck ?? e1 ?? e2;
        var both = overwrite.Intersect(noAction, StringComparer.OrdinalIgnoreCase).ToList();
        if (error == null && both.Count > 0) error = "上書き対象と保護対象の両方に入っています: " + string.Join(", ", both);
        if (error != null)
        {
            MessageBox.Show(this, error, "設定を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!noAction.Contains("sav", StringComparer.OrdinalIgnoreCase)
            && MessageBox.Show(this, "保護対象に sav（セーブデータ）がありません。\n同期でセーブが消える可能性がありますが、保存しますか？",
                "確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        // 同期アプリ用の項目を消さないよう、読み直してから一覧だけ差し替える
        var config = UpdaterConfig.Load(_configPath);
        config.SourceUrl = SourceUrl;
        config.OverwriteFolders = overwrite;
        config.NoActionFolders = noAction;
        config.Save(_configPath);
        ApplyExclusions();
        Result = draft;
        DialogResult = DialogResult.OK;
        Close();
    }

    private Control ExcludedSection()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(3, 10, 3, 3) };
        panel.Controls.Add(new Label { Text = "取り込まない追加画像", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) });
        panel.Controls.Add(Note("ここにある「版」（同じ名前・同じサイズ）は、まとめ等に入っていても取り込みません。"
            + "同じ名前でも中身の違う版（後で正式に入った画像など）は普通に取り込みます。\n"
            + "取り込んだ画像を手で消すと、次の取得のときに登録するか確認します。", 720));
        panel.Controls.Add(_excludedList);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var add = new Button { Text = "画像を選んで取り込まないようにする（手元からも削除）...", AutoSize = true };
        var remove = new Button { Text = "選んだものを解除（次の取得で取り直す）", AutoSize = true };
        add.Click += (_, _) => PickExcluded();
        remove.Click += (_, _) =>
        {
            foreach (int i in _excludedList.SelectedIndices.Cast<int>().OrderByDescending(i => i))
            {
                string key = _excludedKeys[i];
                // まだ保存していない追加分なら、追加を取り消すだけ
                int pending = _toExclude.FindIndex(t => $"{t.Rel}|{t.Size}" == key);
                if (pending >= 0) _toExclude.RemoveAt(pending);
                else _toUnexclude.Add(key);
            }
            RefreshExcludedList();
        };
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);
        panel.Controls.Add(buttons);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return panel;
    }

    private void RefreshExcludedList()
    {
        _excludedList.BeginUpdate();
        _excludedList.Items.Clear();
        _excludedKeys.Clear();
        foreach (var (key, text) in _imageState.ExcludedForDisplay().Where(e => !_toUnexclude.Contains(e.Key)))
        {
            _excludedKeys.Add(key);
            _excludedList.Items.Add(text);
        }
        foreach (var t in _toExclude)
        {
            _excludedKeys.Add($"{t.Rel}|{t.Size}");
            _excludedList.Items.Add($"{t.Rel}（{t.Size:#,0} バイト / 保存時に削除）");
        }
        _excludedList.EndUpdate();
    }

    // 追加画像の保存先から画像を選ぶ。保存を押したときに手元から消して登録する
    private void PickExcluded()
    {
        string root = _imageState.ResourcesDir;
        using var d = new OpenFileDialog { Title = "取り込まないようにする画像を選んでください（複数可）", InitialDirectory = Directory.Exists(root) ? root : _appDir, Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var outside = new List<string>();
        foreach (string file in d.FileNames)
        {
            string full = Path.GetFullPath(file);
            if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { outside.Add(Path.GetFileName(full)); continue; }
            string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            long size = new FileInfo(full).Length;
            string zip = _imageState.Files.TryGetValue(rel, out var v) ? v.Zip : "手動";
            if (!_toExclude.Any(t => t.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase))) _toExclude.Add((full, rel, size, zip));
        }
        if (outside.Count > 0)
            MessageBox.Show(this, $"追加画像の保存先（{root}）の外のファイルは選べません:\n" + string.Join("\n", outside.Take(10)), "追加できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RefreshExcludedList();
    }

    private void ApplyExclusions()
    {
        if (_toExclude.Count == 0 && _toUnexclude.Count == 0) return;
        foreach (var key in _toUnexclude) _imageState.Unexclude(key);
        foreach (var (full, rel, size, zip) in _toExclude)
        {
            _imageState.Exclude(rel, size, zip);
            if (File.Exists(full)) File.Delete(full);
        }
        _imageState.Save(_statePath);
    }

    // 入力中の内容で作った設定（URL の画像側などは元のまま）
    private ConnectionSettings Draft()
    {
        var s = _original.Clone();
        s.BodyUrl = SourceUrl;
        s.BodyDir = _bodyDirBox.Text.Trim();
        s.ResourcesDir = _resDirBox.Text.Trim();
        s.WorkDir = _workDirBox.Text.Trim();
        s.HardLinkResources = _hardLinkCheck.Checked;
        s.AppUpdateCheck = _appUpdateCheck.Checked;
        return s;
    }

    private void AddDirRow(TableLayoutPanel table, string label, TextBox box, string description)
    {
        int row = table.RowCount;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        table.Controls.Add(box, 1, row);
        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            string current = box.Text.Trim().Length > 0 ? Path.GetFullPath(Path.Combine(_appDir, box.Text.Trim())) : box.PlaceholderText.Replace("既定: ", "");
            using var d = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, SelectedPath = Directory.Exists(current) ? current : _appDir };
            if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
        };
        table.Controls.Add(browse, 2, row);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowCount++;
    }

    private Control Section(string title, string note, TextBox box, bool allowFiles)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) });
        panel.Controls.Add(Note(note + "\n1行に1つ。erablue_resort フォルダからの相対パス"));
        panel.Controls.Add(box);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var addDir = new Button { Text = "フォルダを選んで追加...", AutoSize = true };
        addDir.Click += (_, _) => AddPicked(box, pickFile: false);
        buttons.Controls.Add(addDir);
        if (allowFiles)
        {
            var addFile = new Button { Text = "ファイルを選んで追加...", AutoSize = true };
            addFile.Click += (_, _) => AddPicked(box, pickFile: true);
            buttons.Controls.Add(addFile);
        }
        panel.Controls.Add(buttons);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return panel;
    }

    // 選んだ場所を同期元か指定先からの相対パスにして追加する
    private void AddPicked(TextBox box, bool pickFile)
    {
        string? picked = null;
        var roots = Roots;
        string initial = roots.FirstOrDefault(Directory.Exists) ?? "";
        if (pickFile)
        {
            using var d = new OpenFileDialog { Title = "保護するファイルを選んでください", InitialDirectory = initial };
            if (d.ShowDialog(this) == DialogResult.OK) picked = d.FileName;
        }
        else
        {
            using var d = new FolderBrowserDialog { Description = "追加するフォルダを選んでください", SelectedPath = initial };
            if (d.ShowDialog(this) == DialogResult.OK) picked = d.SelectedPath;
        }
        if (picked == null) return;

        string full = Path.GetFullPath(picked);
        string? root = roots.FirstOrDefault(r => full.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        if (root == null)
        {
            MessageBox.Show(this, "本体の保存先か指定先の erablue_resort フォルダの中から選んでください。", "追加できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        var lines = box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (!lines.Contains(rel, StringComparer.OrdinalIgnoreCase)) lines.Add(rel);
        SetLines(box, lines);
    }

    private static void AddRow(TableLayoutPanel layout, Control control, int span)
    {
        int row = layout.RowCount;
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, span);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowCount++;
    }

    private static TextBox NewListBox() => new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, WordWrap = false };
    private static Label Note(string text, int width = 330) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(width, 0) };
    private static void SetLines(TextBox box, IEnumerable<string> lines) => box.Text = string.Join("\r\n", lines);
}
