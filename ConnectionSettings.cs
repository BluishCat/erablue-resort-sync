using System.Text;
using System.Text.RegularExpressions;

namespace erablue_resort同期アプリ;

/// <summary>
/// 同期アプリの設定（取得元・保存先・同期先）。すべて config.txt に保存する
/// </summary>
class ConnectionSettings
{
    public const string DefaultBodyUrl = "https://github.com/erablue-resort/erablue_resort";
    // Wiki が読めなかったとき用（2026/10 時点のリンク）
    public const string DefaultImageUrl = "https://www.dropbox.com/scl/fo/v1td0v9ud4eucalnnnkal/h?rlkey=9fph7swess03tl9qjf6gfpiic&dl=0";

    public string BodyUrl { get; set; } = DefaultBodyUrl;
    public string ImageUrl { get; set; } = DefaultImageUrl;
    // true なら毎回 Wiki から最新のリンクを探し、ImageUrl はその結果で書き換わる
    public bool ImageAutoFromWiki { get; set; } = true;

    // 保存先。空なら既定。相対パスはアプリのフォルダ基準
    public string BodyDir { get; set; } = "";
    public string ResourcesDir { get; set; } = "";
    public string WorkDir { get; set; } = "";
    // 同期先（ゲームのフォルダ）
    public string TargetDir { get; set; } = "";
    // 画像（resources）をコピーせずハードリンクで同期する
    public bool HardLinkResources { get; set; } = false;
    // まとめて更新の後に起動するゲーム（指定先からの相対パス）
    public string GameExe { get; set; } = "";
    public bool LaunchAfterUpdate { get; set; } = false;
    // 前回取り込んだ本体のコミット。同じなら本体のダウンロードを省き、違えば変わったファイルだけ取る
    public string BodyCommit { get; set; } = "";
    // 最後に同期を終えた指定先。今の指定先と違えば「初めての同期」として、ゲーム側にだけある resources を先に取り込む
    public string SyncedTargetDir { get; set; } = "";
    // 起動時にアプリの新しい版を確かめる
    public bool AppUpdateCheck { get; set; } = true;
    // 「この版は飛ばす」を選んだ版（起動時の確認では知らせない）
    public string SkipAppVersion { get; set; } = "";

    public static string DefaultBodyPath(string appDir) => Path.Combine(appDir, "erablue_resort");
    public static string DefaultResourcesPath(string appDir) => Path.Combine(appDir, "resources");
    // exe の隣に置く（場所が分かりやすく、本体・resources と同じドライブになる）
    public static string DefaultWorkPath(string appDir) => Path.Combine(appDir, "work");

    public string BodyPath(string appDir) => Resolve(BodyDir, appDir, DefaultBodyPath(appDir));
    public string ResourcesPath(string appDir) => Resolve(ResourcesDir, appDir, DefaultResourcesPath(appDir));
    public string WorkPath(string appDir) => Resolve(WorkDir, appDir, DefaultWorkPath(appDir));

    private static string Resolve(string value, string appDir, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? fallback : Path.Combine(appDir, Environment.ExpandEnvironmentVariables(value.Trim())));

    public ConnectionSettings Clone() => (ConnectionSettings)MemberwiseClone();

    public static ConnectionSettings Load(string configPath)
    {
        var c = UpdaterConfig.Load(configPath);
        var s = new ConnectionSettings();
        if (!string.IsNullOrEmpty(c.SourceUrl)) s.BodyUrl = c.SourceUrl;
        foreach (var (key, value) in c.Extras) s.Apply(key, value);
        return s;
    }

    private void Apply(string key, string value)
    {
        switch (key)
        {
            case "ImageUrl" when value.Length > 0: ImageUrl = value; break;
            case "ImageAutoFromWiki": ImageAutoFromWiki = !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
            case "BodyDir": BodyDir = value; break;
            case "ResourcesDir": ResourcesDir = value; break;
            case "WorkDir": WorkDir = value; break;
            case "TargetDir": TargetDir = value; break;
            case "HardLinkResources": HardLinkResources = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
            case "GameExe": GameExe = value; break;
            case "LaunchAfterUpdate": LaunchAfterUpdate = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
            case "BodyCommit": BodyCommit = value; break;
            case "SyncedTargetDir": SyncedTargetDir = value; break;
            case "AppUpdateCheck": AppUpdateCheck = !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
            case "SkipAppVersion": SkipAppVersion = value; break;
        }
    }

    // 上書き対象・保護対象など他の項目はそのまま残す
    public void Save(string configPath)
    {
        var c = UpdaterConfig.Load(configPath);
        c.SourceUrl = BodyUrl;
        var mine = new HashSet<string> { "TargetDir", "ImageUrl", "ImageAutoFromWiki", "BodyDir", "ResourcesDir", "WorkDir",
            "HardLinkResources", "GameExe", "LaunchAfterUpdate", "BodyCommit", "SyncedTargetDir", "AppUpdateCheck", "SkipAppVersion" };
        c.Extras = c.Extras.Where(kv => !mine.Contains(kv.Key)).ToList();
        c.Extras.InsertRange(0,
        [
            new("TargetDir", TargetDir),
            new("ImageUrl", ImageUrl),
            new("ImageAutoFromWiki", ImageAutoFromWiki ? "true" : "false"),
            new("BodyDir", BodyDir),
            new("ResourcesDir", ResourcesDir),
            new("WorkDir", WorkDir),
            new("HardLinkResources", HardLinkResources ? "true" : "false"),
            new("GameExe", GameExe),
            new("LaunchAfterUpdate", LaunchAfterUpdate ? "true" : "false"),
            new("BodyCommit", BodyCommit),
            new("SyncedTargetDir", SyncedTargetDir),
            new("AppUpdateCheck", AppUpdateCheck ? "true" : "false"),
            new("SkipAppVersion", SkipAppVersion),
        ]);
        c.Save(configPath);
    }

    /// <summary>
    /// 以前の版が分けて保存していた 同期アプリ_設定.txt / 同期アプリ_指定先.txt を取り込んで消す。
    /// 取り込んだら true
    /// </summary>
    public static bool MigrateLegacy(string configPath, string appDir)
    {
        string settingsPath = Path.Combine(appDir, "同期アプリ_設定.txt");
        string targetPath = Path.Combine(appDir, "同期アプリ_指定先.txt");
        if (!File.Exists(settingsPath) && !File.Exists(targetPath)) return false;

        var s = Load(configPath);
        if (File.Exists(settingsPath))
        {
            foreach (var raw in File.ReadAllLines(settingsPath, Encoding.UTF8))
            {
                int eq = raw.IndexOf('=');
                if (eq > 0 && !raw.TrimStart().StartsWith('#')) s.Apply(raw[..eq].Trim(), raw[(eq + 1)..].Trim());
            }
        }
        if (File.Exists(targetPath) && string.IsNullOrWhiteSpace(s.TargetDir)) s.TargetDir = File.ReadAllText(targetPath, Encoding.UTF8).Trim();
        s.Save(configPath);
        File.Delete(settingsPath);
        File.Delete(targetPath);
        return true;
    }

    /// <summary>
    /// 保存先どうし・指定先と重なっていないか。重なっていると更新や同期で消し合うので禁止する
    /// </summary>
    public string? ValidateDirs(string appDir, string? targetRoot)
    {
        string body, res, work;
        try
        {
            body = BodyPath(appDir);
            res = ResourcesPath(appDir);
            work = WorkPath(appDir);
        }
        catch (Exception ex)
        {
            return "保存先のパスが正しくありません: " + ex.Message;
        }
        // resources を本体の中に置くのは可（本体更新では上書き対象なので消えない）。逆と同じ場所は不可
        if (PathUtil.IsSameOrUnder(body, res)) return "本体の保存先が追加画像の保存先と同じか、その中にあります。";
        // 本体の中に置けるのは本体の resources だけ（それ以外は本体の更新で「配布元に無いもの」として消される）
        if (PathUtil.IsSameOrUnder(res, body) && !PathUtil.IsSameOrUnder(res, Path.Combine(body, "resources")))
            return "追加画像を本体の中に置く場合は、本体の resources フォルダにしてください（他の場所は本体の更新で消されます）。";
        if (PathUtil.IsSameOrUnder(work, body) || PathUtil.IsSameOrUnder(body, work)) return "作業フォルダが本体の保存先と重なっています（本体の更新で消されます）。";
        if (PathUtil.IsSameOrUnder(work, res) || PathUtil.IsSameOrUnder(res, work)) return "作業フォルダが追加画像の保存先と重なっています（同期でゲームに入ってしまいます）。";
        if (!string.IsNullOrWhiteSpace(targetRoot))
        {
            string target = Path.GetFullPath(targetRoot);
            foreach (var (name, path) in new[] { ("本体の保存先", body), ("追加画像の保存先", res), ("作業フォルダ", work) })
            {
                if (PathUtil.IsSameOrUnder(path, target) || PathUtil.IsSameOrUnder(target, path)) return $"{name}が指定先（ゲームのフォルダ）と重なっています。";
            }
        }
        return null;
    }

    /// <summary>入力が使える形か確かめる。問題があればその説明を返す</summary>
    public string? Validate()
    {
        if (!Regex.IsMatch(BodyUrl, @"^https://github\.com/[^/\s]+/[^/\s]+") && !BodyUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return "本体の取得元は「https://github.com/作者名/リポジトリ名」か、zip の直リンクを指定してください。";
        if (!ImageAutoFromWiki && !ImageUrl.StartsWith("https://www.dropbox.com/", StringComparison.OrdinalIgnoreCase))
            return "追加画像の取得元は Dropbox の共有リンク（https://www.dropbox.com/...）を指定してください。";
        return null;
    }

    /// <summary>リポジトリ URL なら main ブランチの zip に、zip の直リンクならそのまま</summary>
    public static string ToBodyZipUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return url;
        url = Regex.Replace(url, @"(\.git)?/?$", "");
        // https://github.com/作者/リポジトリ/tree/ブランチ の形ならそのブランチ
        var m = Regex.Match(url, @"^(https://github\.com/[^/]+/[^/]+)/tree/(.+)$");
        return m.Success ? $"{m.Groups[1].Value}/archive/refs/heads/{m.Groups[2].Value}.zip" : url + "/archive/refs/heads/main.zip";
    }

    /// <summary>GitHub のリポジトリ URL なら（作者, リポジトリ, ブランチ）。zip の直リンク等は null</summary>
    public static (string Owner, string Repo, string Branch)? ParseGitHub(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(url, @"^https://github\.com/([^/\s]+)/([^/\s]+?)(?:\.git)?(?:/tree/(\S+?))?/?$");
        if (!m.Success) return null;
        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? m.Groups[3].Value : "main");
    }

    /// <summary>Dropbox 共有リンクを「フォルダ丸ごと zip」でダウンロードする形にする</summary>
    public static string ToDropboxDownloadUrl(string url)
    {
        url = Regex.Replace(url.Trim(), @"([?&])dl=[^&]*", "${1}dl=1");
        if (!url.Contains("dl=1")) url += (url.Contains('?') ? "&" : "?") + "dl=1";
        return url;
    }
}
