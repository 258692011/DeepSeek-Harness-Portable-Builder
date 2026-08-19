using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        try
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(s_diagLog)); } catch { }
            try { File.WriteAllText(s_diagLog, "=== DeepSeek Harness Portable Update diagnostic " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\r\n"); } catch { }

            // Re-entrancy guard: a second Update.exe must not run concurrently.
            try
            {
                if (File.Exists(s_markerPath))
                {
                    int oldPid = 0;
                    bool oldAlive = false;
                    try { oldPid = int.Parse(File.ReadAllText(s_markerPath).Trim()); } catch { }
                    if (oldPid > 0)
                    {
                        try { Process.GetProcessById(oldPid); oldAlive = true; } catch (ArgumentException) { }
                    }
                    if (oldAlive)
                    {
                        return Fail("防重入", 1,
                            "另一个 Update.exe 正在运行（PID " + oldPid + "）。请等待其完成后再试。",
                            "", s_diagLog);
                    }
                }
                File.WriteAllText(s_markerPath, Process.GetCurrentProcess().Id.ToString());
            }
            catch (Exception ex)
            {
                Log("marker claim failed: " + ex.Message);
            }

            string nodeExe = Path.Combine(s_root, "node", "node.exe");
            string npmCli = Path.Combine(s_root, "node", "node_modules", "npm", "bin", "npm-cli.js");
            string appDir = Path.Combine(s_root, "app");
            string pkgJson = Path.Combine(appDir, "package.json");
            string dshEntry = Path.Combine(appDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

            foreach (var need in new[] { nodeExe, npmCli, pkgJson })
            {
                if (!File.Exists(need))
                {
                    return Fail("组件缺失", 1, "便携包组件缺失：\n" + need + "\n\n请确认 Update.exe 位于 DeepSeek-Harness-Portable 根目录。", "", s_diagLog);
                }
            }

            // Refuse to update while the launcher or a dsh web process from
            // THIS portable is still running (files locked / half-updated tree).
            // --check mode is read-only, so it runs even with the app open.
            Process[] launcher = Process.GetProcessesByName("DeepSeek-Harness");
            string[] runningNodes = FindPortableNodeProcesses(s_root);
            if (!s_checkOnly && (launcher.Length > 0 || runningNodes.Length > 0))
            {
                string detail = "";
                if (launcher.Length > 0) detail += "DeepSeek-Harness.exe 正在运行。\n";
                if (runningNodes.Length > 0) detail += "dsh web 进程正在运行（PID " + string.Join(", ", runningNodes) + "）。\n";
                return Fail("程序运行中", 1,
                    "更新前请先退出 DeepSeek Harness：\n" + detail +
                    "\n请从托盘图标选择“退出”，或关闭后重试。", "", s_diagLog);
            }

            // Read current version from app\package.json.
            string current = null;
            try
            {
                string raw = File.ReadAllText(pkgJson);
                int idx = raw.IndexOf("\"@deepseek-ai/dsh\"", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int colon = raw.IndexOf(':', idx);
                    if (colon >= 0)
                    {
                        int q1 = raw.IndexOf('"', colon);
                        int q2 = q1 >= 0 ? raw.IndexOf('"', q1 + 1) : -1;
                        if (q1 >= 0 && q2 > q1) current = raw.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
            }
            catch (Exception ex) { Log("read version failed: " + ex.Message); }

            // Query the registry for the latest version.
            string latest = null;
            string viewOut = RunCapture(nodeExe, "\"" + npmCli + "\" view \"@deepseek-ai/dsh\" version --registry https://registry.npmjs.org/", appDir);
            if (viewOut != null)
            {
                // RunCapture appends "[stderr]" when npm exits non-zero (e.g.
                // offline). Such a run has no version to report — take only
                // the last non-empty stdout line so an error message is never
                // mistaken for a version number.
                int sep = viewOut.IndexOf("[stderr]");
                string stdout = sep < 0 ? viewOut : viewOut.Substring(0, sep);
                foreach (var line in stdout.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length > 0) latest = t;
                }
            }

            if (latest == null)
            {
                if (s_checkOnly)
                {
                    MessageBox.Show("无法查询 npm registry（可能离线），请稍后重试。",
                        "DeepSeek Harness Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 0;
                }
                DialogResult r = MessageBox.Show(
                    "无法查询 npm registry（可能离线）。\n\n仍要尝试更新到最新版吗？\n（npm 会自行判断是否有新版）",
                    "DeepSeek Harness Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return 0;
            }
            else if (latest == current)
            {
                MessageBox.Show("已是最新版本（" + latest + "），无需更新。",
                    "DeepSeek Harness Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            else if (s_checkOnly)
            {
                MessageBox.Show("发现新版本：" + latest + "（当前 " + current + "）。\n\n" +
                    "请先退出 DeepSeek-Harness.exe，然后双击 Update.exe 进行更新。",
                    "DeepSeek Harness Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            else
            {
                DialogResult r = MessageBox.Show(
                    "当前版本：" + current + "\n最新版本：" + latest + "\n\n确认更新？\n（仅更新 dsh，用户数据 data\\dsh 原样保留）",
                    "DeepSeek Harness Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return 0;
            }

            // Perform the update with the bundled npm (never the system npm).
            // Show a progress dialog while npm runs: the update is silent and
            // takes seconds, and a bare MessageBox-then-wait would look hung.
            Log("installing @deepseek-ai/dsh@latest in " + appDir);
            string install = null;
            bool installFailed = false;
            using (var progress = new Form
            {
                Text = "DeepSeek Harness Update",
                ClientSize = new System.Drawing.Size(360, 90),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.CenterScreen,
            })
            {
                var label = new Label
                {
                    Text = "正在更新 dsh：" + current + " → " + latest + "，请稍候...",
                    AutoSize = false,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Top,
                    Height = 36,
                };
                var bar = new ProgressBar
                {
                    Style = ProgressBarStyle.Marquee,
                    MarqueeAnimationSpeed = 30,
                    Dock = DockStyle.Bottom,
                    Height = 20,
                };
                progress.Controls.Add(bar);
                progress.Controls.Add(label);
                var worker = new System.ComponentModel.BackgroundWorker();
                worker.DoWork += (s, e) =>
                {
                    install = RunCapture(nodeExe,
                        "\"" + npmCli + "\" install \"@deepseek-ai/dsh@latest\" --registry https://registry.npmjs.org/ --no-audit --no-fund",
                        appDir);
                };
                worker.RunWorkerCompleted += (s, e) =>
                {
                    if (e.Error != null) { installFailed = true; Log("worker error: " + e.Error); }
                    progress.Close();
                };
                worker.RunWorkerAsync();
                progress.ShowDialog(); // modal; own message pump for the worker callback
            }
            Log("npm install output:\n" + (install ?? "(null)"));

            if (installFailed)
            {
                return Fail("更新失败", 1, "npm 更新过程中发生错误。\n\n详细日志：" + s_diagLog, install, s_diagLog);
            }

            if (!File.Exists(dshEntry))
            {
                return Fail("更新失败", 1, "npm 安装后未找到 dsh 入口：\n" + dshEntry + "\n\n详细日志：" + s_diagLog, install, s_diagLog);
            }

            string newVer = RunCapture(nodeExe, "\"" + dshEntry + "\" --version", appDir);
            newVer = newVer == null ? "?" : newVer.Trim();
            MessageBox.Show(
                "更新完成：" + newVer + "\n\n正在重新启动 DeepSeek-Harness.exe...",
                "DeepSeek Harness Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try
            {
                Process.Start(Path.Combine(s_root, "DeepSeek-Harness.exe"));
            }
            catch (Exception launchEx)
            {
                MessageBox.Show("更新已完成，但无法自动启动 DeepSeek-Harness.exe：\n" + launchEx.Message + "\n\n请手动启动。",
                    "DeepSeek Harness Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return 0;
        }
        catch (Exception ex)
        {
            return Fail("发生未预期的错误", 1, ex.ToString(), "", s_diagLog);
        }
        finally
        {
            try { if (File.Exists(s_markerPath)) File.Delete(s_markerPath); } catch { }
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
                    if (cmd != null && cmd.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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

            // A modal dialog with a "copy log" button: the raw failure text is
            // too long for a MessageBox, and users need an easy way to report it.
            using (var form = new Form
            {
                Text = "DeepSeek Harness Update",
                ClientSize = new System.Drawing.Size(560, 300),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
            })
            {
                var txt = new TextBox
                {
                    Text = body,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Dock = DockStyle.Fill,
                };
                var btnCopy = new Button
                {
                    Text = "复制日志",
                    Width = 90,
                    Height = 30,
                    Dock = DockStyle.Right,
                };
                btnCopy.Click += (s, e) =>
                {
                    try
                    {
                        string fullLog = body;
                        if (File.Exists(diagLog)) fullLog += "\n\n=== 诊断日志 ===\n" + File.ReadAllText(diagLog);
                        Clipboard.SetText(fullLog);
                        btnCopy.Text = "已复制 ✓";
                    }
                    catch { }
                };
                var btnClose = new Button
                {
                    Text = "关闭",
                    Width = 90,
                    Height = 30,
                    Dock = DockStyle.Right,
                };
                btnClose.Click += (s, e) => form.Close();
                var bar = new Panel { Dock = DockStyle.Bottom, Height = 36 };
                bar.Controls.Add(btnCopy);
                bar.Controls.Add(btnClose);
                form.Controls.Add(txt);
                form.Controls.Add(bar);
                form.ShowDialog();
            }
        }
        catch { }
        return code;
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(s_diagLog, msg + "\r\n"); } catch { }
    }
}
