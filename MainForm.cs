using System.Diagnostics;
using System.Text;

namespace erablue_resort同期アプリ;

class MainForm : Form
{
    private static readonly string AppDirectory = AppContext.BaseDirectory;
    private static readonly string ConfigPath = Path.Combine(AppDirectory, "config.txt");
    // 取り込み済みの追加画像 zip の記録
    private static readonly string ImageStatePath = Path.Combine(AppDirectory, "同期アプリ_取込済み画像.txt");
    private const string DistributionUrl = "https://github.com/erablue-resort/erablue_resort/wiki";
    // ログファイルがこれより大きくなったら .old に回す
    private const long MaxLogBytes = 2_000_000;

    private ConnectionSettings _settings = new();
    private string SourceRoot => _settings.BodyPath(AppDirectory);
    private string ResourcesSource => _settings.ResourcesPath(AppDirectory);
    private string WorkDir => _settings.WorkPath(AppDirectory);
    private string TrashRoot => Path.Combine(WorkDir, "削除済み");
    private string LogPath => Path.Combine(WorkDir, "同期アプリ_ログ.txt");
    private readonly Label _sourceLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _bodyUrlBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _imageUrlBox = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _imageAutoCheck = new() { Text = "Wiki から自動で探す", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _resetUrlButton = new() { Text = "初期値に戻す", AutoSize = true };
    private readonly Button _configButton = new() { Text = "詳細設定（保存先・config.txt）...", AutoSize = true };
    private readonly ToolTip _toolTip = new();

    private readonly TextBox _targetBox = new() { Dock = DockStyle.Fill };
    private readonly Button _browseButton = new() { Text = "参照...", AutoSize = true };
    private readonly ComboBox _gameExeBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _launchCheck = new() { Text = "まとめて更新の後に起動する", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _allButton = new() { Text = "まとめて更新（本体＋追加画像＋同期）", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) };
    private readonly Button _updateButton = new() { Text = "本体を更新", AutoSize = true };
    private readonly Button _imagesButton = new() { Text = "追加画像を取得", AutoSize = true };
    private readonly Button _syncButton = new() { Text = "同期", AutoSize = true };
    private readonly Button _launchButton = new() { Text = "ゲームを起動", AutoSize = true };
    private readonly Button _openResourcesButton = new() { Text = "resources フォルダを開く", AutoSize = true };
    private readonly LinkLabel _trashLink = new() { Text = "同期で消したファイルの退避先", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly LinkLabel _distributionLink = new() { Text = "本体データ・顔グラ画像の配布元（Wiki）", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ProgressBar _progressBar = new() { Dock = DockStyle.Fill };
    private readonly TextBox _logBox = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private bool _refreshingGameList;
    private bool _logStarted;

    public MainForm()
    {
        Text = "erablue_resort 同期アプリ";
        Size = new Size(1000, 660);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "同期元", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_sourceLabel, 1, 0);
        layout.SetColumnSpan(_sourceLabel, 2);

        layout.Controls.Add(new Label { Text = "指定先", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_targetBox, 1, 1);
        layout.Controls.Add(_browseButton, 2, 1);

        layout.Controls.Add(new Label { Text = "本体の取得元", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_bodyUrlBox, 1, 2);
        var urlButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        urlButtons.Controls.Add(_resetUrlButton);
        urlButtons.Controls.Add(_configButton);
        layout.Controls.Add(urlButtons, 2, 2);

        layout.Controls.Add(new Label { Text = "追加画像の取得元", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(_imageUrlBox, 1, 3);
        layout.Controls.Add(_imageAutoCheck, 2, 3);

        layout.Controls.Add(new Label { Text = "起動するゲーム", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        layout.Controls.Add(_gameExeBox, 1, 4);
        layout.Controls.Add(_launchCheck, 2, 4);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(_allButton);
        buttons.Controls.Add(_updateButton);
        buttons.Controls.Add(_imagesButton);
        buttons.Controls.Add(_syncButton);
        buttons.Controls.Add(_launchButton);
        buttons.Controls.Add(_openResourcesButton);
        buttons.Controls.Add(_trashLink);
        buttons.Controls.Add(_distributionLink);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 3);

        layout.Controls.Add(_progressBar, 0, 6);
        layout.SetColumnSpan(_progressBar, 3);

        layout.Controls.Add(_logBox, 0, 7);
        layout.SetColumnSpan(_logBox, 3);
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        // exe 単体で置かれても動くよう、設定が無ければ初期値で作る
        if (!File.Exists(ConfigPath)) new UpdaterConfig().Save(ConfigPath);
        bool migrated = ConnectionSettings.MigrateLegacy(ConfigPath, AppDirectory);
        LoadSettingsToUi();
        RotateLog();
        Http.Log = Log;
        if (migrated) Log("同期アプリ_設定.txt / 同期アプリ_指定先.txt の内容を config.txt にまとめました");
        _toolTip.SetToolTip(_bodyUrlBox, "GitHub のリポジトリ URL（…/tree/ブランチ名 も可）か zip の直リンク。更新アプリの config.txt と共有します");
        _toolTip.SetToolTip(_imageUrlBox, "Dropbox の共有フォルダのリンク。自動のときは Wiki で見つけたリンクが入ります");
        _toolTip.SetToolTip(_imageAutoCheck, "オンにすると毎回 Wiki の「○○以降更新分」のリンクを使います（見つからなければ左のリンク）");
        _toolTip.SetToolTip(_gameExeBox, "指定先にある exe。版が上がって名前が変わったときは、同じ系統の新しい exe に自動で切り替えます");
        _toolTip.SetToolTip(_launchCheck, "「まとめて更新」が最後まで問題なく終わったら、このゲームを起動します");
        _toolTip.SetToolTip(_trashLink, $"同期で指定先から消したファイルは、ここに日時ごとに退避します（直近 {FolderSync.TrashGenerations} 回分）");
        _imageAutoCheck.CheckedChanged += (_, _) => _imageUrlBox.ReadOnly = _imageAutoCheck.Checked;
        _resetUrlButton.Click += (_, _) =>
        {
            _bodyUrlBox.Text = ConnectionSettings.DefaultBodyUrl;
            _imageUrlBox.Text = ConnectionSettings.DefaultImageUrl;
            _imageAutoCheck.Checked = true;
            TrySaveSettings();
        };
        _configButton.Click += async (_, _) => await OpenConfigDialog();
        _bodyUrlBox.Leave += (_, _) => TrySaveSettings();
        _imageUrlBox.Leave += (_, _) => TrySaveSettings();
        _targetBox.Leave += (_, _) => { TrySaveSettings(); RefreshGameList(); };
        _imageAutoCheck.CheckedChanged += (_, _) => TrySaveSettings();
        _gameExeBox.SelectedIndexChanged += (_, _) => { if (!_refreshingGameList) TrySaveSettings(); };
        _launchCheck.CheckedChanged += (_, _) => TrySaveSettings();

        _browseButton.Click += (_, _) => BrowseTarget();
        _allButton.Click += async (_, _) => await RunWithLock("まとめて更新", RunAll);
        _updateButton.Click += async (_, _) => await RunWithLock("本体の更新", UpdateBody);
        _imagesButton.Click += async (_, _) => await RunWithLock("追加画像の取得", FetchImages);
        _syncButton.Click += async (_, _) => await RunWithLock("同期", async () => await Sync());
        _launchButton.Click += (_, _) => LaunchGame();
        _openResourcesButton.Click += (_, _) => OpenResourcesFolder();
        _trashLink.LinkClicked += (_, _) =>
        {
            Directory.CreateDirectory(TrashRoot);
            Process.Start(new ProcessStartInfo(TrashRoot) { UseShellExecute = true });
        };
        _distributionLink.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(DistributionUrl) { UseShellExecute = true });
    }

    private void LoadSettingsToUi()
    {
        _settings = ConnectionSettings.Load(ConfigPath);
        _targetBox.Text = _settings.TargetDir;
        _bodyUrlBox.Text = _settings.BodyUrl;
        _imageUrlBox.Text = _settings.ImageUrl;
        _imageAutoCheck.Checked = _settings.ImageAutoFromWiki;
        _imageUrlBox.ReadOnly = _settings.ImageAutoFromWiki;
        _launchCheck.Checked = _settings.LaunchAfterUpdate;
        UpdateSourceLabel();
        RefreshGameList();
    }

    private void UpdateSourceLabel()
    {
        _sourceLabel.Text = $"本体: {SourceRoot}　＋　追加画像: {ResourcesSource}";
        _toolTip.SetToolTip(_sourceLabel, "作業フォルダ: " + WorkDir);
    }

    // 指定先の exe を並べ、保存した exe（無くなっていたら同じ系統の新しいもの）を選ぶ
    private void RefreshGameList()
    {
        string target = _targetBox.Text.Trim();
        string? resolved = null;
        var exes = new List<string>();
        try
        {
            if (target.Length > 0)
            {
                exes = Game.ListExes(target);
                resolved = Game.Resolve(target, _settings.GameExe);
            }
        }
        catch
        {
            // 指定先が不正なら空のまま
        }
        _refreshingGameList = true;
        _gameExeBox.BeginUpdate();
        _gameExeBox.Items.Clear();
        foreach (var e in exes) _gameExeBox.Items.Add(e);
        if (resolved != null) _gameExeBox.SelectedItem = resolved;
        _gameExeBox.EndUpdate();
        _refreshingGameList = false;
        if (resolved != null && !resolved.Equals(_settings.GameExe, StringComparison.OrdinalIgnoreCase))
        {
            if (_settings.GameExe.Length > 0) Log($"起動するゲームを {resolved} に切り替えました（{_settings.GameExe} が見つからないため）");
            TrySaveSettings();
        }
    }

    // 画面の入力を設定に反映して保存する。おかしな入力なら理由を返す
    private string? TrySaveSettings()
    {
        // 保存先などダイアログで決めた項目は今の設定から引き継ぐ
        var s = _settings.Clone();
        s.BodyUrl = _bodyUrlBox.Text.Trim();
        s.ImageUrl = _imageUrlBox.Text.Trim();
        s.ImageAutoFromWiki = _imageAutoCheck.Checked;
        s.TargetDir = _targetBox.Text.Trim();
        if (_gameExeBox.SelectedItem is string exe) s.GameExe = exe;
        s.LaunchAfterUpdate = _launchCheck.Checked;
        // 取得元が変わったら前回の版は当てにならない
        if (!s.BodyUrl.Equals(_settings.BodyUrl, StringComparison.OrdinalIgnoreCase)) s.BodyCommit = "";
        string? error = s.Validate() ?? s.ValidateDirs(AppDirectory, null);
        if (error != null) return error;
        _settings = s;
        _settings.Save(ConfigPath);
        return null;
    }

    private async Task OpenConfigDialog()
    {
        TrySaveSettings();
        string oldBody = SourceRoot, oldRes = ResourcesSource;
        bool oldHardLink = _settings.HardLinkResources;
        using var dialog = new ConfigDialog(ConfigPath, _settings, AppDirectory, _targetBox.Text.Trim(), ImageStatePath);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var next = dialog.Result;
        next.ImageUrl = _imageUrlBox.Text.Trim();
        next.ImageAutoFromWiki = _imageAutoCheck.Checked;
        next.TargetDir = _targetBox.Text.Trim();
        if (!next.BodyUrl.Equals(_settings.BodyUrl, StringComparison.OrdinalIgnoreCase)) next.BodyCommit = "";
        string newBody = next.BodyPath(AppDirectory), newRes = next.ResourcesPath(AppDirectory);

        // 中身を移すかどうか。移さなければ新しい場所は空から始まる
        var moves = new List<(string name, string from, string to)>();
        if (!SamePath(oldBody, newBody)) moves.Add(("本体", oldBody, newBody));
        if (!SamePath(oldRes, newRes)) moves.Add(("追加画像", oldRes, newRes));
        foreach (var (name, from, to) in moves.ToList())
        {
            if (PathUtil.IsEmptyOrMissing(from)) { moves.Remove((name, from, to)); continue; }
            if (!PathUtil.IsEmptyOrMissing(to))
            {
                Log($"{name}の新しい保存先には既にファイルがあるため、移動はせずそのまま使います: {to}");
                if (name == "本体") next.BodyCommit = ""; // 中身の版が分からない
                moves.Remove((name, from, to));
                continue;
            }
            // 入れ子（resources を本体の中へ等）の移動は事故の元なので行わない
            if (PathUtil.IsSameOrUnder(to, from) || PathUtil.IsSameOrUnder(from, to)) { moves.Remove((name, from, to)); continue; }
            var answer = MessageBox.Show(this, $"{name}の保存先を変更します。\n\n{from}\n→ {to}\n\n今の中身を新しい場所へ移動しますか？\n（いいえ: 移動せず、新しい場所は空から始めます）",
                "保存先の変更", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) { Log("保存先の変更をキャンセルしました（config.txt は保存済み）"); _bodyUrlBox.Text = next.BodyUrl; TrySaveSettings(); return; }
            if (answer == DialogResult.No) moves.Remove((name, from, to));
        }

        _settings = next;
        _bodyUrlBox.Text = next.BodyUrl;
        _settings.Save(ConfigPath);
        UpdateSourceLabel();

        foreach (var (name, from, to) in moves)
        {
            Log($"{name}を移動中: {from} → {to}");
            await RunWithLock($"{name}の移動", async () =>
            {
                await Task.Run(() => PathUtil.MoveDirectory(from, to, Log));
                if (name == "追加画像") ImageState.Rebind(ImageStatePath, from, to);
                Log($"{name}の移動が完了しました");
            });
        }
        Log("設定を保存しました（保護対象: " + string.Join(", ", FolderSync.LoadNoActionPaths(ConfigPath)) + "）");

        if (!oldHardLink && _settings.HardLinkResources)
        {
            var answer = MessageBox.Show(this, "ハードリンクをオンにしました。\n\n指定先の resources にある、こちらと同じ画像を今すぐハードリンクにつなぎ直しますか？\n"
                + "（中身は同じなので見た目は変わりません。二重に使っていた容量が空きます。数分かかることがあります）\n\n"
                + "いいえ: 今あるものはそのままにして、これから入る画像だけハードリンクにします",
                "ハードリンク", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) await RunWithLock("ハードリンクへのつなぎ直し", Relink);
        }
    }

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private bool CheckSettings()
    {
        string? error = TrySaveSettings();
        if (error == null) return true;
        MessageBox.Show(this, error, "接続先を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // 顔グラ画像を置く場所なので、まだなければ作ってから開く
    private void OpenResourcesFolder()
    {
        Directory.CreateDirectory(ResourcesSource);
        Process.Start(new ProcessStartInfo(ResourcesSource) { UseShellExecute = true });
    }

    private void BrowseTarget()
    {
        using var dialog = new FolderBrowserDialog { Description = "同期先の erablue_resort フォルダを選んでください", SelectedPath = _targetBox.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _targetBox.Text = dialog.SelectedPath;
        TrySaveSettings();
        RefreshGameList();
    }

    // 処理中はボタンを押せないようにし、例外はダイアログで知らせる。かかった時間もログに残す
    private async Task RunWithLock(string name, Func<Task> action)
    {
        SetBusy(true);
        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            Log($"（{name}: {Seconds(sw)}）");
        }
        catch (Exception ex)
        {
            Log($"エラー（{name} / {Seconds(sw)}）: " + ex.Message);
            MessageBox.Show(this, ex.ToString(), "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string Seconds(Stopwatch sw) => sw.Elapsed.TotalSeconds < 60 ? $"{sw.Elapsed.TotalSeconds:0.0} 秒" : $"{(int)sw.Elapsed.TotalMinutes} 分 {sw.Elapsed.Seconds} 秒";

    private void SetBusy(bool busy)
    {
        _browseButton.Enabled = !busy;
        _resetUrlButton.Enabled = !busy;
        _configButton.Enabled = !busy;
        _bodyUrlBox.ReadOnly = busy;
        _imageUrlBox.ReadOnly = busy || _imageAutoCheck.Checked;
        _imageAutoCheck.Enabled = !busy;
        _allButton.Enabled = !busy;
        _updateButton.Enabled = !busy;
        _imagesButton.Enabled = !busy;
        _syncButton.Enabled = !busy;
        _launchButton.Enabled = !busy;
        _gameExeBox.Enabled = !busy;
        _targetBox.ReadOnly = busy;
    }

    // 1つの工程を行い、失敗してもまとめて更新の残りを続けられるよう結果だけ返す
    private async Task<bool> Step(string name, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            Log($"（{name}: {Seconds(sw)}）");
            return true;
        }
        catch (Exception ex)
        {
            Log($"{name}に失敗しました（{Seconds(sw)}）: {ex.Message}");
            return false;
        }
    }

    // 1ボタンで全部。指定先が不正ならダウンロード前に止める
    private async Task RunAll()
    {
        if (!CheckSettings() || !CheckTarget(out _)) return;
        Log($"===== まとめて更新を開始（{DateTime.Now:yyyy/MM/dd HH:mm}） =====");
        bool bodyOk = await Step("本体の更新", UpdateBody);
        // 画像は resources に入るだけなので、本体が失敗しても取っておける
        bool imagesOk = await Step("追加画像の取得", FetchImages);
        if (!bodyOk)
        {
            Log("本体の更新に失敗したため、同期はしませんでした（ゲームは前のままです）。時間をおいてもう一度押してください");
            MessageBox.Show(this, "本体の更新に失敗したため、同期はしませんでした。\nゲームは前のままです。詳しくはログを見てください。", "まとめて更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        bool synced = await Step("同期", async () => { if (!await Sync()) throw new OperationCanceledException("同期が最後まで終わりませんでした"); });
        if (!synced) return;
        if (!imagesOk) Log("※追加画像の取得には失敗しましたが、本体の更新分は同期しました。画像は次回また取りにいきます");
        Log("===== まとめて更新が完了しました =====");
        if (_launchCheck.Checked) LaunchGame();
    }

    // 更新アプリと同じ処理をアプリ内で行う。前回の版が分かれば、変わったファイルだけ取る
    private async Task UpdateBody()
    {
        if (!CheckSettings()) return;
        string? commit = await BodyUpdater.Run(SourceRoot, ConfigPath, _settings.BodyUrl, _settings.BodyCommit, WorkDir, Log, DownloadProgress());
        if ((commit ?? "") != _settings.BodyCommit)
        {
            _settings.BodyCommit = commit ?? "";
            _settings.Save(ConfigPath);
        }
    }

    // 同期で指定先の resources に入るよう、同フォルダの resources に展開する
    private async Task FetchImages()
    {
        if (!CheckSettings()) return;
        string used = await ImageFetcher.Run(ResourcesSource, ImageStatePath, WorkDir, _settings.ImageAutoFromWiki, _settings.ImageUrl,
            AskExclude, Log, DownloadProgress());
        // 自動のときは Wiki で見つけたリンクを覚えておき、次に Wiki が読めなくても使えるようにする
        if (_settings.ImageAutoFromWiki && used != _settings.ImageUrl)
        {
            _imageUrlBox.Text = used;
            TrySaveSettings();
        }
    }

    // 取り込んだ画像が手で消されていたとき、その版を今後取り込まないか聞く
    private bool AskExclude(List<(string Rel, long Size, string Zip)> deleted)
    {
        if (InvokeRequired) return (bool)Invoke(() => AskExclude(deleted));
        string sample = string.Join("\n", deleted.Take(10).Select(d => "・" + d.Rel)) + (deleted.Count > 10 ? $"\n…ほか {deleted.Count - 10} 件" : "");
        var answer = MessageBox.Show(this,
            $"前回までに取り込んだ追加画像のうち {deleted.Count} 件が、手元から消されています。\n\n{sample}\n\n"
            + "この版は今後取り込まないようにしますか？\n\n"
            + "はい：取り込まない（同じ名前でも中身の違う版が来たら取り込みます）\nいいえ：取り直す\n\n"
            + "登録したものは「詳細設定」でいつでも解除できます。",
            "消された追加画像", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            // 大量に消えているのは事故の可能性が高いので、既定は「取り直す」
            deleted.Count > 50 ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1);
        return answer == DialogResult.Yes;
    }

    private IProgress<(long done, long total)> DownloadProgress()
    {
        _progressBar.Maximum = 1000;
        _progressBar.Value = 0;
        return new Progress<(long done, long total)>(p =>
        {
            _progressBar.Value = p.total > 0 ? (int)Math.Min(1000, p.done * 1000 / p.total) : (int)(p.done / 1_000_000 % 1000);
        });
    }

    private bool CheckTarget(out string targetRoot)
    {
        targetRoot = _targetBox.Text.Trim();
        string? error = targetRoot.Length == 0 ? "指定先が空です。" : ValidateTarget(targetRoot = Path.GetFullPath(targetRoot));
        if (error == null) return true;
        MessageBox.Show(this, error, "指定先を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // 起動中に同期すると、使用中のファイルが置き換えられなかったりセーブに影響したりするので止める
    private async Task<bool> WaitGameClosed(string targetRoot)
    {
        while (true)
        {
            var running = await Task.Run(() => Game.FindRunning(targetRoot));
            if (running.Count == 0) return true;
            var answer = MessageBox.Show(this,
                $"指定先のゲーム（{string.Join(", ", running)}）が起動中です。\n\n起動中に同期すると、使用中のファイルが置き換えられずに途中で止まったり、"
                + "セーブに影響したりすることがあります。\n\nゲームを終了してから「再試行」を押してください。",
                "ゲームが起動中です", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.Retry)
            {
                Log("ゲームが起動中のため、同期を中止しました");
                return false;
            }
        }
    }

    // 同期を最後まで問題なく行った（または差分なし）なら true
    private async Task<bool> Sync()
    {
        if (!CheckTarget(out string targetRoot)) return false;
        TrySaveSettings();
        if (!Directory.Exists(SourceRoot))
        {
            MessageBox.Show(this, $"本体の保存先に本体がありません。先に「本体を更新」を実行してください。\n{SourceRoot}", "同期できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        Directory.CreateDirectory(ResourcesSource);

        List<string> protectedPaths = FolderSync.LoadNoActionPaths(ConfigPath);
        Log("差分を確認中...");
        var plan = await Task.Run(() => FolderSync.CreatePlan(SourceRoot, ResourcesSource, targetRoot, protectedPaths));

        // 画像を大量に取り込んだ直後などはコピーが数万件になるので、ログには先頭だけ出す
        const int MaxCopyLines = 300;
        var report = new StringBuilder();
        foreach (var (_, relPath) in plan.Copies.Take(MaxCopyLines)) report.AppendLine("コピー: " + relPath);
        if (plan.Copies.Count > MaxCopyLines) report.AppendLine($"…ほか {plan.Copies.Count - MaxCopyLines} 件のコピー");
        foreach (string relPath in plan.DeleteFiles.Take(MaxCopyLines)) report.AppendLine("削除: " + relPath);
        if (plan.DeleteFiles.Count > MaxCopyLines) report.AppendLine($"…ほか {plan.DeleteFiles.Count - MaxCopyLines} 件の削除");
        foreach (string relDir in plan.DeleteDirs) report.AppendLine("フォルダ削除: " + relDir);
        report.Append($"コピー {plan.Copies.Count} 件 / 削除 {plan.DeleteFiles.Count} 件 / フォルダ削除 {plan.DeleteDirs.Count} 件");
        if (plan.Touched > 0) report.Append($"（中身が同じで日時だけ違った {plan.Touched} 件は日時を揃えました）");
        Log(report.ToString());

        int total = plan.Copies.Count + plan.DeleteFiles.Count + plan.DeleteDirs.Count;
        if (total == 0)
        {
            Log("指定先は最新です");
            return true;
        }
        if (!await WaitGameClosed(targetRoot)) return false;

        // ボタン一つで済ませたいので、確認は消すものがあるときだけ出す
        bool hasDeletion = plan.DeleteFiles.Count + plan.DeleteDirs.Count > 0;
        string question = $"{targetRoot}\n\nコピー {plan.Copies.Count} 件 / 削除 {plan.DeleteFiles.Count} 件 / フォルダ削除 {plan.DeleteDirs.Count} 件\n"
            + $"触らない場所: {string.Join(", ", protectedPaths)}\n\n消すファイルは次の場所に退避します（直近 {FolderSync.TrashGenerations} 回分）:\n{TrashRoot}\n\n"
            + "実行しますか？（内容はログを確認してください）";
        if (hasDeletion && MessageBox.Show(this, question, "同期の確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            Log("キャンセルしました");
            return false;
        }

        _progressBar.Maximum = total;
        _progressBar.Value = 0;
        var progress = new Progress<int>(done => _progressBar.Value = Math.Min(done, _progressBar.Maximum));
        bool hardLink = _settings.HardLinkResources;
        string trashRoot = TrashRoot;
        var result = await Task.Run(() => FolderSync.Execute(plan, targetRoot, hardLink, trashRoot, progress, Log));
        // 同期で exe が入った・名前が変わったときに、起動するゲームの一覧を今の指定先に合わせる
        RefreshGameList();

        var summary = $"同期しました（コピー {result.Copied} 件"
            + (result.Linked > 0 ? $" / ハードリンク {result.Linked} 件" : "")
            + $" / 退避 {result.Trashed} 件 / フォルダ削除 {result.DirsDeleted} 件）";
        if (result.TrashDir != null && result.Trashed > 0) summary += "\r\n  退避先: " + result.TrashDir;
        Log(summary);
        if (result.Errors.Count == 0) return true;

        Log($"終わらなかったもの {result.Errors.Count} 件:\r\n" + string.Join("\r\n", result.Errors.Take(100).Select(e => "  " + e))
            + (result.Errors.Count > 100 ? $"\r\n  …ほか {result.Errors.Count - 100} 件" : ""));
        MessageBox.Show(this, $"{result.Errors.Count} 件のファイルを同期できませんでした（ほかは同期済み）。\n\n"
            + string.Join("\n", result.Errors.Take(8)) + (result.Errors.Count > 8 ? "\n…" : "")
            + "\n\nファイルが使用中のことが多いです。開いているものを閉じて、もう一度「同期」を押してください。",
            "同期の一部が終わりませんでした", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // ハードリンクをオンにしたとき、今ある同じ画像をつなぎ直して容量を空ける
    private async Task Relink()
    {
        if (!CheckTarget(out string targetRoot) || !Directory.Exists(SourceRoot)) return;
        if (!PathUtil.SameVolume(ResourcesSource, targetRoot))
        {
            Log("追加画像の保存先と指定先が別のドライブなので、ハードリンクは使えません（普通のコピーのままです）");
            return;
        }
        if (!await WaitGameClosed(targetRoot)) return;
        Log("同じ画像をハードリンクにつなぎ直しています...");
        var protectedPaths = FolderSync.LoadNoActionPaths(ConfigPath);
        _progressBar.Maximum = 1000;
        _progressBar.Value = 0;
        var progress = new Progress<(int done, int total)>(p => _progressBar.Value = p.total > 0 ? (int)(p.done * 1000L / p.total) : 0);
        var (linked, bytes, errors) = await Task.Run(() => FolderSync.Relink(SourceRoot, ResourcesSource, targetRoot, protectedPaths, progress));
        Log($"ハードリンクにつなぎ直しました（{linked:#,0} 件 / {bytes / 1e6:#,0} MB 空きました）");
        if (errors.Count > 0) Log($"つなげなかったもの {errors.Count} 件:\r\n" + string.Join("\r\n", errors.Take(30).Select(e => "  " + e)));
    }

    private void LaunchGame()
    {
        if (!CheckTarget(out string targetRoot)) return;
        if (_gameExeBox.SelectedItem is not string exe)
        {
            MessageBox.Show(this, "指定先に exe が見つかりません。", "ゲームを起動", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (Game.FindRunning(targetRoot).Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            Log($"{exe} は起動中です");
            return;
        }
        Game.Launch(targetRoot, exe);
        Log($"{exe} を起動しました");
    }

    // 誤って別のフォルダを丸ごと書き換えないための確認
    private string? ValidateTarget(string targetRoot)
    {
        string? overlap = _settings.ValidateDirs(AppDirectory, targetRoot);
        if (overlap != null) return overlap;
        bool looksLikeGame = Directory.Exists(Path.Combine(targetRoot, "ERB")) && Directory.Exists(Path.Combine(targetRoot, "CSV"));
        bool isEmpty = !Directory.Exists(targetRoot) || !Directory.EnumerateFileSystemEntries(targetRoot).Any();
        if (!looksLikeGame && !isEmpty)
        {
            return "指定先が erablue_resort のフォルダではないようです（ERB と CSV のフォルダがありません）。";
        }
        return null;
    }

    private void RotateLog()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes) File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), true);
        }
        catch
        {
            // ログが回せなくても動作には関係ない
        }
    }

    // バックグラウンド処理からも呼ばれるので UI スレッドに回す。ログファイルにも残す
    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }
        _logBox.AppendText(message + Environment.NewLine);
        try
        {
            Directory.CreateDirectory(WorkDir);
            var sb = new StringBuilder();
            if (!_logStarted)
            {
                sb.Append($"\r\n===== {DateTime.Now:yyyy/MM/dd HH:mm:ss} 起動 =====\r\n");
                _logStarted = true;
            }
            sb.Append($"{DateTime.Now:HH:mm:ss} ").Append(message.Replace("\n", "\n         ")).Append("\r\n");
            File.AppendAllText(LogPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // ログファイルに書けなくても画面には出ている
        }
    }
}
