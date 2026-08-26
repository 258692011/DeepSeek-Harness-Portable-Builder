using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private static string s_root;
    private static string s_diagLog;
    private static string s_markerPath;
    private static bool s_checkOnly;

    [STAThread]
    private static int Main(string[] args)
    {
        s_checkOnly = args != null && Array.IndexOf(args, "--check") >= 0;
        Encoding originalConsole = null;
        try { originalConsole = Console.OutputEncoding; Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            return MainBody();
        }
        finally
        {
            try { if (originalConsole != null) Console.OutputEncoding = originalConsole; } catch { }
        }
    }

    private static int MainBody()
    {
        s_root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        s_diagLog = Path.Combine(s_root, "data", "dsh", "logs", "Update-exe-diagnostic.log");
        s_markerPath = Path.Combine(s_root, "data", "dsh", ".dsh-update-in-progress");
        try { Directory.CreateDirectory(Path.GetDirectoryName(s_diagLog)); } catch { }
        try { File.WriteAllText(s_diagLog, "=== DeepSeek Harness Portable Update diagnostic " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\r\n"); } catch { }

        // Re-entrancy marker: a second Update.exe must not run concurrently.
        // A stale marker from a killed process is ignored (PID not alive).
        // --check is read-only (registry query only) and never claims the
        // marker, so a tray "检查更新" always works even while the window is
        // open (2026-08-22). The marker is deleted in the finally block ONLY
        // if this process actually claimed it — an unrelated --check window
        // closing must never delete an in-progress update's marker (2026-08-24).
        bool markerOk = true;
        int markerPid = 0;
        bool markerClaimed = false;
        try
        {
            if (!s_checkOnly && File.Exists(s_markerPath))
            {
                int oldPid = 0;
                bool oldAlive = false;
                try { oldPid = int.Parse(File.ReadAllText(s_markerPath).Trim()); } catch { }
                if (oldPid > 0)
                {
                    try { Process.GetProcessById(oldPid); oldAlive = true; } catch (ArgumentException) { }
                }
                if (oldAlive) { markerOk = false; markerPid = oldPid; }
            }
            if (markerOk && !s_checkOnly)
            {
                File.WriteAllText(s_markerPath, Process.GetCurrentProcess().Id.ToString());
                markerClaimed = true;
            }
        }
        catch (Exception ex) { Log("marker claim failed: " + ex.Message); }

        try
        {
            // The window is the whole UX: no auto-check on launch, a
            // "检查更新" button runs the registry query, results and update
            // progress are shown inside the window. --check (tray) opens the
            // same window and triggers one check on load.
            using (var win = new UpdateForm(s_root, s_diagLog, s_checkOnly, markerOk, markerPid))
            {
                win.ShowDialog();
            }
            return 0;
        }
        catch (Exception ex)
        {
            return Fail("发生未预期的错误", 1, ex.ToString(), "", s_diagLog);
        }
        finally
        {
            // Only this process's own marker is removed. An unrelated window
            // (--check during an update, or a second Update.exe that saw the
            // marker) must never delete the marker of the running update.
            if (markerClaimed)
            {
                try { if (File.Exists(s_markerPath)) File.Delete(s_markerPath); } catch { }
            }
        }
    }

    private static string[] FindPortableNodeProcesses(string root)
    {
        var list = new List<string>();
        try
        {
            foreach (var p in Process.GetProcessesByName("node"))
            {
                try
                {
                    string cmd = p.MainModule.FileName;
                    // Match the portable root as a full path segment (root + "\"),
                    // not a bare prefix: D:\portable must never match D:\portable2.
                    if (cmd != null && cmd.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                        list.Add(p.Id.ToString());
                }
                catch { }
            }
        }
        catch { }
        return list.ToArray();
    }

    private static string RunCapture(string exe, string args, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Log("exit " + p.ExitCode + " stderr: " + se);
                    return so + (se.Length > 0 ? "\n[stderr] " + se : "");
                }
                return so;
            }
        }
        catch (Exception ex)
        {
            Log("RunCapture failed: " + ex.ToString());
            return null;
        }
    }

    // Streaming process runner: both stdout and stderr are read on async
    // callbacks (never the sequential ReadToEnd pattern, which can deadlock
    // when the child fills one pipe while the parent blocks on the other).
    // Every line is forwarded to onLine (live UI + log). isDoneLine spots the
    // installer's completion marker ("Done in ... using pnpm", npm's
    // "N packages in ..."). Measured 2026-08-22: pnpm normally exits ~0.1s
    // after "Done" with no children, but can linger indefinitely (post-run
    // network chatter / a stray child — the first hang waited hours). So the
    // moment the marker is seen, if the process has not exited yet, kill the
    // whole tree immediately (no grace); the post-install `bin.js --version`
    // check is the real gate. ExitCode -1 = could not start.
    private static InstallResult RunStreaming(string exe, string args, string workDir, Action<string> onLine, Func<string, bool> isDoneLine)
    {
        var sb = new StringBuilder();
        int doneTick = -1;
        const int GraceMs = 0; // kill the tree immediately once "Done" is seen and it has not exited
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                var stdoutDone = new ManualResetEvent(false);
                var stderrDone = new ManualResetEvent(false);
                Action<string> onData = data =>
                {
                    if (data == null) return;
                    lock (sb) { sb.AppendLine(data); }
                    if (onLine != null) onLine(data);
                    if (isDoneLine != null && isDoneLine(data)) { lock (sb) { if (doneTick < 0) doneTick = Environment.TickCount; } }
                };
                p.OutputDataReceived += (s, e) => { if (e.Data == null) { stdoutDone.Set(); return; } onData(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data == null) { stderrDone.Set(); return; } onData(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                int started = Environment.TickCount;
                bool exited = false;
                bool doneSeen = false;
                while (true)
                {
                    if (p.WaitForExit(500)) { exited = true; break; }
                    int elapsed = Environment.TickCount - started;
                    lock (sb) { doneSeen = doneTick >= 0; }
                    if (doneSeen && elapsed > (doneTick - started) + GraceMs) break;
                }
                if (!exited)
                {
                    // Installer printed its completion marker but the process
                    // lingers: stop waiting, kill the whole tree.
                    KillTree(p.Id);
                    p.WaitForExit(5000);
                }
                stdoutDone.WaitOne(2000);
                stderrDone.WaitOne(2000);
                int code = exited ? p.ExitCode : 0; // lingers after "Done" -> install finished
                return new InstallResult { ExitCode = code, Output = sb.ToString() };
            }
        }
        catch (Exception ex)
        {
            Log("RunStreaming failed: " + ex.ToString());
            return new InstallResult { ExitCode = -1, Output = sb.ToString() };
        }
    }

    private static void KillTree(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/PID " + pid + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var k = Process.Start(psi)) { k.WaitForExit(8000); }
        }
        catch { }
        try { Process.GetProcessById(pid).Kill(); } catch { }
    }

    private static bool IsInstallDoneLine(string line)
    {
        if (line == null) return false;
        string t = line.Trim();
        // pnpm: "Done in 31.4s using pnpm v11.21.0"
        if (t.StartsWith("Done in ", StringComparison.OrdinalIgnoreCase) && t.IndexOf(" using pnpm", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // npm: "added 447 packages in 31s" / "up to date in 2s" / "changed 62 packages in 3s"
        if (t.IndexOf(" packages in ", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // Quick connectivity probe to the official registry, run through the SAME
    // bundled node the installer uses (same runtime/proxy/CA — a .NET
    // HttpWebRequest probe misjudges this machine's TLS path, so it is NOT
    // used; 2026-08-22). Returns null when reachable, else a user-facing
    // reason. Runs on a worker thread so a dead link fails in seconds instead
    // of minutes of pnpm retries.
    private static string ProbeRegistry(string nodeExe, string workDir)
    {
        string script = "const https=require('https');const t=setTimeout(()=>{console.log('PROBE_TIMEOUT');process.exit(0)},6000);https.get('https://registry.npmjs.org/',r=>{clearTimeout(t);console.log('PROBE_OK '+r.statusCode);process.exit(0)}).on('error',e=>{clearTimeout(t);console.log('PROBE_ERR '+(e.code||e.message));process.exit(0)})";
        string out1 = RunCapture(nodeExe, "-e \"" + script + "\"", workDir);
        string line = null;
        if (out1 != null)
        {
            foreach (var ln in out1.Split('\n'))
            {
                string t = ln.Trim();
                if (t.Length > 0) { line = t; break; }
            }
        }
        if (line != null && line.StartsWith("PROBE_OK")) return null;
        string code = line != null && line.StartsWith("PROBE_ERR ") ? line.Substring("PROBE_ERR ".Length) : line;
        if (line == "PROBE_TIMEOUT") return "无法连接 registry.npmjs.org：连接超时（网络或代理不稳定）。";
        if (code != null && (code.IndexOf("ENOTFOUND") >= 0 || code.IndexOf("getaddrinfo") >= 0)) return "无法连接 registry.npmjs.org：DNS 解析失败。";
        if (code != null && (code.IndexOf("ECONNREFUSED") >= 0)) return "无法连接 registry.npmjs.org：连接被拒绝（网络或代理配置问题）。";
        if (code != null) return "无法连接 registry.npmjs.org：" + code;
        return "无法连接 registry.npmjs.org（网络探测失败）。";
    }

    // Turn the installer's raw failure output into a short user-facing cause
    // (mirrors the Hermes updater's ClassifyUpdateError, adopted 2026-08-22).
    // Returns null when no known pattern matches (generic text is used then).
    private static string ClassifyInstallError(string output)
    {
        string low = output == null ? "" : output.ToLowerInvariant();
        if (low.IndexOf("und_err_destroyed") >= 0 || low.IndexOf("etimedout") >= 0 ||
            low.IndexOf("econnrefused") >= 0 || low.IndexOf("econnreset") >= 0 ||
            low.IndexOf("eai_again") >= 0 || low.IndexOf("fetch failed") >= 0 ||
            low.IndexOf("network error") >= 0 || low.IndexOf("network request failed") >= 0 ||
            low.IndexOf("socket hang up") >= 0 || low.IndexOf("timed out") >= 0 ||
            low.IndexOf("timeout") >= 0 || low.IndexOf("connection reset") >= 0)
            return "原因：网络错误（无法访问 registry.npmjs.org，可能是网络或代理不稳定）。建议检查网络/代理后重试；更新流程是安全的，重试不会损坏现有安装。";
        if (low.IndexOf("enotfound") >= 0 || low.IndexOf("getaddrinfo") >= 0 ||
            low.IndexOf("could not resolve") >= 0)
            return "原因：DNS 解析失败，无法找到 registry.npmjs.org。建议检查 DNS 与网络配置后重试。";
        if (low.IndexOf(" 403") >= 0 || low.IndexOf(" 401") >= 0 || low.IndexOf("eacces") >= 0 ||
            low.IndexOf("permission denied") >= 0 || low.IndexOf("authentication") >= 0)
            return "原因：访问被拒绝（网络限制、代理或服务端拦截）。建议检查网络环境后重试。";
        return null;
    }

    // pnpm writes caret ranges (^0.1.1-rc.2) into package.json; strip range
    // specifiers so version comparisons are meaningful.
    private static string NormalizeVersion(string v)
    {
        if (v == null) return null;
        v = v.Trim();
        int i = 0;
        while (i < v.Length && (v[i] == '^' || v[i] == '~' || v[i] == '>' || v[i] == '<' || v[i] == '=' || v[i] == ' ' || v[i] == 'v' || v[i] == 'V'))
            i++;
        return v.Substring(i);
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var parts = text.Split('\n');
        int start = Math.Max(0, parts.Length - lines);
        return string.Join("\n", parts, start, parts.Length - start);
    }

    private sealed class InstallResult
    {
        public int ExitCode;
        public string Output;
    }

    private static int Fail(string step, int code, string message, string output, string diagLog)
    {
        try
        {
            string tail = "";
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                int start = Math.Max(0, lines.Length - 12);
                tail = string.Join("\n", lines, start, lines.Length - start);
            }
            string body = "更新失败：" + step + "（退出码 " + code + "）。\n\n" + message +
                (tail.Length > 0 ? "\n\n--- 输出 ---\n" + tail : "") +
                "\n\n详细日志：" + diagLog;
            MessageBox.Show(body, "DeepSeek Harness Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
        return code;
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(s_diagLog, msg + "\r\n"); } catch { }
    }

    // ------------------------------------------------------------------ UI
    // The updater window: no auto-check on launch, a 检查更新 button queries
    // the registry, results and update progress render inside the window.
    internal sealed class UpdateForm : Form
    {
        private readonly string _root;
        private readonly string _diagLog;
        private readonly bool _checkOnly;
        private readonly bool _markerOk;
        private readonly int _markerPid;

        private readonly string _nodeExe;
        private readonly string _npmCli;
        private readonly string _pnpmCjs;
        private readonly string _appDir;
        private readonly string _pkgJson;
        private readonly string _dshEntry;
        private readonly List<string> _missing = new List<string>();

        private string _current;
        private string _latest;
        private bool _busy;

        private readonly Label _lblCurrent;
        private readonly Label _lblLatest;
        private readonly Label _lblStatus;
        private readonly Panel _infoPanel;
        private readonly Button _btnCheck;
        private readonly Button _btnUpdate;
        private readonly TextBox _txtLog;

        public UpdateForm(string root, string diagLog, bool checkOnly, bool markerOk, int markerPid)
        {
            _root = root;
            _diagLog = diagLog;
            _checkOnly = checkOnly;
            _markerOk = markerOk;
            _markerPid = markerPid;

            _nodeExe = Path.Combine(root, "node", "node.exe");
            _npmCli = Path.Combine(root, "node", "node_modules", "npm", "bin", "npm-cli.js");
            _pnpmCjs = Path.Combine(root, "node", "node_modules", "pnpm", "bin", "pnpm.cjs");
            _appDir = Path.Combine(root, "app");
            _pkgJson = Path.Combine(_appDir, "package.json");
            _dshEntry = Path.Combine(_appDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

            foreach (var need in new[] { _nodeExe, _npmCli, _pkgJson })
                if (!File.Exists(need)) _missing.Add(need);

            _current = ReadCurrentVersion();

            // ----- layout -----
            Text = "DeepSeek Harness Update";
            ClientSize = new Size(620, 480);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font(FontFamily.GenericSansSerif, 9f);

            _infoPanel = new Panel { Dock = DockStyle.Top };
            var info = _infoPanel;
            _lblCurrent = new Label { Text = "当前版本：" + (_current ?? "未知"), AutoSize = false, Location = new Point(8, 8), Size = new Size(604, 18), TextAlign = ContentAlignment.MiddleLeft };
            _lblLatest = new Label { Text = "最新版本：—", AutoSize = false, Location = new Point(8, 28), Size = new Size(604, 18), TextAlign = ContentAlignment.MiddleLeft };
            _lblStatus = new Label { Text = "准备就绪：点击“检查更新”", AutoSize = false, Location = new Point(8, 48), Size = new Size(604, 18), TextAlign = ContentAlignment.TopLeft };
            info.Controls.Add(_lblCurrent);
            info.Controls.Add(_lblLatest);
            info.Controls.Add(_lblStatus);

            var logCaption = new Label
            {
                Text = "日志",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 20,
                Padding = new Padding(8, 2, 0, 0),
            };
            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Margin = new Padding(8, 2, 8, 2),
                BackColor = Color.White,
            };

            // Action buttons sit at the bottom-right (primary 立即更新 on the
            // far right), standard Windows dialog convention.
            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 8, 12, 8) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.Yes, WrapContents = false };
            _btnCheck = new Button { Text = "检查更新", Width = 110, Height = 34 };
            _btnUpdate = new Button { Text = "立即更新", Width = 110, Height = 34, Enabled = false };
            _btnCheck.Click += (s, e) => CheckForUpdates();
            _btnUpdate.Click += (s, e) => RunUpdate();
            flow.Controls.Add(_btnUpdate); // first control -> rightmost
            flow.Controls.Add(_btnCheck);
            btnPanel.Controls.Add(flow);
            AcceptButton = _btnCheck;

            Controls.Add(_txtLog);
            Controls.Add(logCaption);
            Controls.Add(info);
            Controls.Add(btnPanel);

            if (!_markerOk)
            {
                SetStatus("另一个 Update.exe 正在运行（PID " + _markerPid + "），请等待其完成后重试。");
                _btnCheck.Enabled = false;
            }
            else if (_missing.Count > 0)
            {
                SetStatus("便携包组件缺失：\n" + string.Join("\n", _missing.ToArray()));
                _btnCheck.Enabled = false;
            }
            else
            {
                UpdateStatusHeight();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // --check (tray "检查更新"): open the window and run one check.
            if (_checkOnly) BeginInvoke(new Action(CheckForUpdates));
        }

        private string ReadCurrentVersion()
        {
            try
            {
                string raw = File.ReadAllText(_pkgJson);
                int idx = raw.IndexOf("\"@deepseek-ai/dsh\"", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int colon = raw.IndexOf(':', idx);
                    if (colon >= 0)
                    {
                        int q1 = raw.IndexOf('"', colon);
                        int q2 = q1 >= 0 ? raw.IndexOf('"', q1 + 1) : -1;
                        if (q1 >= 0 && q2 > q1) return raw.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
            }
            catch (Exception ex) { Log("read version failed: " + ex.Message); }
            return null;
        }

        private void SetStatus(string text)
        {
            if (InvokeRequired)
            {
                // The window may have been closed while a worker ran; swallow
                // the cross-thread call instead of crashing (2026-08-22).
                try { BeginInvoke(new Action<string>(SetStatus), text); } catch { }
                return;
            }
            _lblStatus.Text = text;
            UpdateStatusHeight();
        }

        // Grow/shrink the status label and the info panel to fit the current
        // text (1 line while idle/"正在更新", up to ~5 lines for guard and
        // failure messages) so there is never a big dead gap above 日志.
        private void UpdateStatusHeight()
        {
            int lines = 0;
            foreach (var seg in _lblStatus.Text.Split('\n'))
            {
                lines += 1 + Math.Max(0, (int)Math.Ceiling(seg.Length / 60.0) - 1);
            }
            if (lines < 1) lines = 1;
            if (lines > 5) lines = 5;
            _lblStatus.Height = lines * 14 + 4;
            _infoPanel.Height = 48 + _lblStatus.Height + 4;
        }

        private void AppendLog(string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendLog), text); } catch { }
                return;
            }
            if (_txtLog.TextLength > 60000) _txtLog.Clear();
            _txtLog.AppendText(text + "\r\n");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing mid-update would orphan the pnpm worker; ask first.
            // Closing mid-check is harmless (read-only), so only guard busy.
            if (_busy)
            {
                DialogResult r = MessageBox.Show(
                    "更新正在进行中，关闭窗口不会撤销已完成的操作。\n\n确定要关闭吗？",
                    "DeepSeek Harness Update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnCheck.Enabled = !busy && _markerOk && _missing.Count == 0;
            _btnUpdate.Enabled = !busy && _markerOk && _missing.Count == 0 && _latest != null && !string.Equals(NormalizeVersion(_current), NormalizeVersion(_latest), StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------- check
        private void CheckForUpdates()
        {
            if (_busy) return;
            SetBusy(true);
            SetStatus("正在检查更新...");
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                // --fetch-timeout bounds the query on slow/flaky proxy links.
                string viewOut = RunCapture(_nodeExe,
                    "\"" + _npmCli + "\" view \"@deepseek-ai/dsh\" version --registry https://registry.npmjs.org/ --fetch-timeout=15000 --fetch-retries=2",
                    _appDir);
                string probe = null;
                if (viewOut == null || viewOut.IndexOf("[stderr]") >= 0) probe = ProbeRegistry(_nodeExe, _appDir);
                e.Result = new string[] { viewOut, probe };
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                try
                {
                    string[] pair = e.Error != null ? null : (string[])e.Result;
                    string viewOut = pair == null ? null : pair[0];
                    string probe = pair == null ? null : pair[1];
                    string latest = null;
                    if (viewOut != null)
                    {
                        int sep = viewOut.IndexOf("[stderr]");
                        string stdout = sep < 0 ? viewOut : viewOut.Substring(0, sep);
                        foreach (var line in stdout.Split('\n'))
                        {
                            string t = line.Trim();
                            if (t.Length > 0) latest = t;
                        }
                    }
                    _latest = latest;
                    if (_latest == null)
                    {
                        SetStatus("无法查询 npm registry。" + (probe != null ? "\n" + probe : "") + "请稍后重试。");
                        AppendLog("check failed: " + (viewOut ?? "(null)"));
                    }
                    else if (string.Equals(NormalizeVersion(_current), NormalizeVersion(_latest), StringComparison.OrdinalIgnoreCase))
                    {
                        _lblLatest.Text = "最新版本：" + _latest;
                        SetStatus("已是最新版本");
                        AppendLog("already latest: " + _latest);
                    }
                    else
                    {
                        _lblLatest.Text = "最新版本：" + _latest;
                        SetStatus("发现新版本：" + _latest + "，点击“立即更新”。");
                        AppendLog("update available: " + _current + " -> " + _latest);
                    }
                    SetBusy(false);
                }
                catch (Exception ex) { Log("check completion handler: " + ex); }
            };
            worker.RunWorkerAsync();
        }

        // ---------------------------------------------------------- update
        // Kill every DeepSeek Harness.exe launcher and portable node web
        // process started FROM this portable's own root (taskkill /T /F, so
        // the launcher's tray icon disappears with it). Returns true if any
        // were stopped. Instances from other directories are left alone.
        private bool StopRunningInstances()
        {
            bool any = false;
            foreach (var p in Process.GetProcessesByName("DeepSeek Harness"))
            {
                try
                {
                    string f = p.MainModule.FileName;
                    // Full path-segment match (root + "\") so a sibling portable
                    // whose path is a prefix of ours (D:\portable2 vs D:\portable)
                    // is never touched (2026-08-24).
                    if (f != null && f.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("stopping launcher PID " + p.Id + " (" + f + ")");
                        KillTree(p.Id);
                        any = true;
                    }
                }
                catch { }
            }
            foreach (var pidStr in FindPortableNodeProcesses(_root))
            {
                int pid;
                if (int.TryParse(pidStr, out pid) && pid > 0)
                {
                    Log("stopping node PID " + pid);
                    KillTree(pid);
                    any = true;
                }
            }
            return any;
        }

        private void RunUpdate()
        {
            if (_busy || _latest == null) return;

            // Automatically stop THIS portable's own running instances first:
            // killing the launcher also removes its tray icon. Instances from
            // ANOTHER directory are never touched — they do not lock this tree
            // (2026-08-22: a name-only match wrongly blocked the stage updater
            // while a different C:\ install ran).
            if (StopRunningInstances())
            {
                SetStatus("已停止本目录运行的 DeepSeek Harness（含托盘图标），开始更新...");
                AppendLog("已自动停止同目录实例（DeepSeek Harness.exe / dsh web，含托盘图标）");
                System.Threading.Thread.Sleep(1500); // let file handles release
            }

            bool usePnpm = File.Exists(_pnpmCjs);
            string installer = usePnpm ? _pnpmCjs : _npmCli;
            // pnpm needs `add` (not `install`) to bump the dependency in
            // package.json — `pnpm install <spec>` silently reinstalls the
            // existing spec (hit 2026-08-22). Retry/concurrency flags make
            // flaky proxy networks (UND_ERR_DESTROYED) survive.
            // --config.minimum-release-age=0: pnpm 11 blocks packages younger
            // than 1 day (minimumReleaseAge default 1440 min). A just-published
            // dsh release makes "@latest" silently resolve to the previous
            // version and "Done" without changing anything (hit 2026-08-22:
            // rc.2 stayed rc.1). 0 disables the age gate.
            string installArgs = usePnpm
                ? "add \"@deepseek-ai/dsh@latest\" --registry=https://registry.npmjs.org/ --config.node-linker=hoisted --config.dangerously-allow-all-builds --fetch-retries=5 --network-concurrency=8 --config.minimum-release-age=0"
                : "install \"@deepseek-ai/dsh@latest\" --registry https://registry.npmjs.org/ --no-audit --no-fund --fetch-retries=5 --fetch-retry-mintimeout=1000";

            // A hoisted pnpm tree ships .modules.yaml whose storeDir /
            // virtualStoreDir record the BUILDER machine's paths; pnpm
            // refuses to work on it. A hoisted tree does not need the file.
            string modulesYaml = Path.Combine(_appDir, "node_modules", ".modules.yaml");
            if (File.Exists(modulesYaml))
            {
                try { File.Delete(modulesYaml); Log("removed stale node_modules\\.modules.yaml (builder-path metadata)"); }
                catch (Exception ex) { Log("could not remove .modules.yaml: " + ex.Message); }
            }

            SetBusy(true);
            AppendLog("开始更新：engine=" + (usePnpm ? "pnpm" : "npm"));
            Log("installing @deepseek-ai/dsh@latest in " + _appDir);
            SetStatus("正在更新 dsh：" + _current + " → " + _latest + "（" + (usePnpm ? "pnpm" : "npm") + "）...");

            var sw = Stopwatch.StartNew();
            InstallResult result = null;
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                // Pre-flight connectivity probe: a dead/flaky link fails in
                // seconds with a clear message instead of minutes of pnpm
                // retries (2026-08-22).
                string netReason = ProbeRegistry(_nodeExe, _appDir);
                if (netReason != null)
                {
                    Log("preflight network probe failed: " + netReason);
                    result = new InstallResult { ExitCode = -3, Output = netReason };
                    return;
                }
                result = RunStreaming(_nodeExe, "\"" + installer + "\" " + installArgs, _appDir, line =>
                {
                    string t = line == null ? "" : line.Trim();
                    if (t.Length == 0) return;
                    Log("  " + t);
                    AppendLog(t);
                }, IsInstallDoneLine);
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                try
                {
                    sw.Stop();
                    Log("install exit=" + (result == null ? -1 : result.ExitCode) + " elapsed=" + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");

                    if (e.Error != null || result == null || result.ExitCode != 0)
                    {
                        Log("install failed");
                        string why;
                        if (result != null && result.ExitCode == -1) why = "更新失败：无法启动安装进程。";
                        else if (result != null && result.ExitCode == -3) why = "更新失败（网络）：\n" + result.Output;
                        else
                        {
                            string cls = result == null ? null : ClassifyInstallError(result.Output);
                            why = "更新失败：安装过程出错。" + (cls != null ? "\n" + cls : "") + "（详见下方日志与 " + _diagLog + "）";
                        }
                        SetStatus(why);
                        if (result != null) AppendLog("--- 输出尾部 ---\n" + Tail(result.Output, 12));
                        SetBusy(false);
                        return;
                    }
                    if (!File.Exists(_dshEntry))
                    {
                        SetStatus("更新失败：安装后未找到 dsh 入口（详见日志）。");
                        SetBusy(false);
                        return;
                    }

                    string newVer = RunCapture(_nodeExe, "\"" + _dshEntry + "\" --version", _appDir);
                    newVer = newVer == null ? "?" : newVer.Trim();
                    Log("verified dsh --version: " + newVer);
                    if (_latest != null && !string.Equals(NormalizeVersion(newVer), NormalizeVersion(_latest), StringComparison.OrdinalIgnoreCase))
                    {
                        SetStatus("更新失败：安装后 dsh 版本未变为最新（期望 " + _latest + "，实际 " + newVer + "，详见日志）。");
                        SetBusy(false);
                        return;
                    }

                    SetStatus("更新完成：" + newVer);
                    AppendLog("更新完成：" + newVer);
                    _btnCheck.Enabled = false;
                    _btnUpdate.Enabled = false;
                    // Ask before restarting: the user may be busy. On "是" the
                    // launcher boots the web UI and shows the WebView2 window
                    // (waits for HTTP 200 first — no open-page race here).
                    DialogResult rr = MessageBox.Show(
                        "更新完成：" + newVer + "\n\n是否立即重启 DeepSeek Harness？",
                        "DeepSeek Harness Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (rr == DialogResult.Yes)
                    {
                        try
                        {
                            Process.Start(Path.Combine(_root, "DeepSeek Harness.exe"));
                        }
                        catch (Exception launchEx)
                        {
                            MessageBox.Show("更新已完成，但无法自动启动 DeepSeek Harness.exe：\n" + launchEx.Message + "\n\n请手动启动。",
                                "DeepSeek Harness Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    Close();
                }
                catch (Exception ex) { Log("update completion handler: " + ex); }
            };
            worker.RunWorkerAsync();
        }
    }
}
