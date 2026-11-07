using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace erablue_resort同期アプリ;

/// <summary>GitHub の最新リリース（新しい版のアプリ）</summary>
record AppRelease(Version Version, string Tag, string Notes, string PageUrl, string ZipUrl, long ZipSize, string? Sha256);

/// <summary>
/// アプリ自身の更新。GitHub の最新リリースと今の版を比べ、zip を落として SHA-256 を確かめ、exe を入れ替える。
/// 実行中の exe は上書きできないが名前は変えられるので、今の exe を .old にしてから新しい exe を置き、起動し直す
/// </summary>
static class AppUpdater
{
    public const string Repo = "BluishCat/erablue-resort-sync";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";
    private const string ExeName = "erablue_resort同期アプリ.exe";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
    private static string OldPath => ExePath + ".old";

    /// <summary>前回の更新で残った古い exe を消す（起動直後は前の版がまだ終わりきっていないことがあるので、少し待ってやり直す）</summary>
    public static async Task CleanupOld(Action<string> log)
    {
        if (!File.Exists(OldPath)) return;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                File.Delete(OldPath);
                log($"アプリを v{Current.ToString(3)} に更新しました");
                return;
            }
            catch (Exception) when (i < 9)
            {
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                log("古い版の exe を消せませんでした（次の起動時にもう一度消します）: " + ex.Message);
            }
        }
    }

    /// <summary>最新リリースを調べる。今より新しくなければ null</summary>
    public static async Task<AppRelease?> FindNewer()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        if (version <= Current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!Regex.IsMatch(name, @"^erablue_resort_sync_v[\d.]+\.zip$", RegexOptions.IgnoreCase)) continue;
            string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : null;
            return new AppRelease(version, tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.GetProperty("html_url").GetString() ?? ReleasesUrl,
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                sha);
        }
        return null;
    }

    /// <summary>exe のあるフォルダに書けるか（Program Files などに置かれていると書けない）</summary>
    public static bool CanWriteAppDir()
    {
        try
        {
            string probe = Path.Combine(Path.GetDirectoryName(ExePath)!, $".update-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 新しい版を落として確かめ、exe を入れ替える。ここで失敗したら今の exe はそのまま。
    /// 成功したら、呼び出し側で新しい exe を起動して今のアプリを閉じる
    /// </summary>
    public static async Task Install(AppRelease release, string workDir, Action<string> log, IProgress<(long done, long total)> progress)
    {
        if (release.Sha256 == null) throw new InvalidDataException("リリースに SHA-256 が載っていないため、自動では更新しません。リリースページから手で更新してください。");
        string dir = Path.Combine(workDir, "アプリ更新");
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, $"erablue_resort_sync_{release.Tag}.zip");
        string newExe = ExePath + ".new";
        try
        {
            log($"新しい版（{release.Tag}）をダウンロード中: {release.ZipUrl}");
            await Http.DownloadFile(release.ZipUrl, zipPath, progress, release.ZipSize);

            string actual;
            await using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ダウンロードした zip の SHA-256 がリリースと違うため、更新をやめました（{actual[..12]}… / {release.Sha256[..12]}…）");
            log("SHA-256 を確かめました");

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("zip の中に exe が見つかりません");
                entry.ExtractToFile(newExe, true);
            }

            // 今の exe を .old に退かしてから新しい exe を置く。置けなければ元に戻す
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);
            try
            {
                File.Move(newExe, ExePath);
            }
            catch
            {
                File.Move(OldPath, ExePath);
                throw;
            }
            log($"exe を {release.Tag} に入れ替えました。起動し直します");
        }
        finally
        {
            try { if (File.Exists(newExe)) File.Delete(newExe); } catch { /* 次回の更新で上書きする */ }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* 作業フォルダに残るだけ */ }
        }
    }

    public static void Restart() =>
        Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = Path.GetDirectoryName(ExePath)!, UseShellExecute = true });
}

/// <summary>新しい版のお知らせ。更新内容を見せて、更新するか・今はしないか・この版を飛ばすかを選んでもらう</summary>
class AppUpdateDialog : Form
{
    public enum Choice { Later, Update, Skip }
    public Choice Result { get; private set; } = Choice.Later;

    public AppUpdateDialog(AppRelease release, bool canWrite)
    {
        Text = "アプリの更新";
        Size = new Size(620, 460);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.Controls.Add(new Label
        {
            Text = $"新しい版 {release.Tag} があります（今の版: v{AppUpdater.Current.ToString(3)}）。",
            AutoSize = true,
            Font = new Font(Control.DefaultFont, FontStyle.Bold),
        });
        layout.Controls.Add(new Label { Text = "更新内容", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        var notes = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = PlainNotes(release.Notes),
            TabStop = false,
        };
        layout.Controls.Add(notes);
        var page = new LinkLabel { Text = "リリースページを開く", AutoSize = true };
        page.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
        layout.Controls.Add(page);
        layout.Controls.Add(new Label
        {
            Text = canWrite
                ? "「更新して再起動」を押すと、新しい版をダウンロードして exe を入れ替え、起動し直します。設定・本体・追加画像はそのままです。"
                : "アプリのフォルダに書き込めないため、自動では更新できません。リリースページから zip をダウンロードし、exe を上書きしてください。",
            AutoSize = true, MaximumSize = new Size(580, 0), Margin = new Padding(3, 6, 3, 6),
        });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var update = new Button { Text = "更新して再起動", AutoSize = true, Enabled = canWrite };
        var later = new Button { Text = "今はしない", AutoSize = true };
        var skip = new Button { Text = "この版は飛ばす", AutoSize = true };
        update.Click += (_, _) => { Result = Choice.Update; DialogResult = DialogResult.OK; };
        later.Click += (_, _) => { Result = Choice.Later; DialogResult = DialogResult.Cancel; };
        skip.Click += (_, _) => { Result = Choice.Skip; DialogResult = DialogResult.OK; };
        buttons.Controls.AddRange([update, later, skip]);
        layout.Controls.Add(buttons);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AcceptButton = canWrite ? update : later;
        CancelButton = later;
        // 開いたときに更新内容が全選択にならないよう、ボタンにフォーカスを置く
        Shown += (_, _) => ActiveControl = canWrite ? update : later;
    }

    // リリースノート（Markdown）を、テキストボックスで読みやすい形にする
    private static string PlainNotes(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = Regex.Replace(raw, @"\[([^\]]+)\]\([^)]+\)", "$1");   // [文字](URL) → 文字
            line = line.Replace("`", "").Replace("**", "");
            var heading = Regex.Match(line, @"^#{1,6}\s+(.*)$");
            if (heading.Success) line = "■ " + heading.Groups[1].Value;
            var bullet = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
            if (bullet.Success) line = new string('　', bullet.Groups[1].Value.Length / 2) + "・" + bullet.Groups[2].Value;
            lines.Add(line);
        }
        return string.Join("\r\n", lines).Trim();
    }
}
