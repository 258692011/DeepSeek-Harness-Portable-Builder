using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private static Process _child;
    private static NotifyIcon _tray;
    private static string _root;

    [STAThread]
    private static int Main()
    {
        _root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            string nodeExe = Path.Combine(_root, "node", "node.exe");
            string dshEntry = Path.Combine(_root, "app", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (!File.Exists(nodeExe) || !File.Exists(dshEntry))
            {
                MessageBox.Show("DeepSeek-Harness-Portable 组件缺失:\r\n" + nodeExe + "\r\n" + dshEntry,
                    "DeepSeek Harness Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // The canonical dsh port: the UI always lives here. A second
            // double-click while the app is already running must not spin up
            // a new instance on a random port — hand the browser to the
            // running instance instead.
            const int port = 3080;
            string url = "http://127.0.0.1:" + port + "/";
            if (PortInUse(port))
            {
                // The first instance may still be booting; give it a moment,
                // then open its page and exit (no second server, no tray).
                WaitForHttp(url, TimeSpan.FromSeconds(5));
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return 0;
            }

            // Self-heal the profile node_modules link farm: after the portable
            // is moved, the profile links point at the old absolute location.
            // dsh rebuilds them on boot; deleting the stale farm forces that.
            string profilesNm = Path.Combine(_root, "data", "dsh", "profiles", "node_modules");
            if (Directory.Exists(profilesNm))
            {
                try { Directory.Delete(profilesNm, true); }
                catch { /* best-effort; dsh may still recover */ }
            }

            // Route all user data into the portable data dir.
            Environment.SetEnvironmentVariable("DSH_HOME", Path.Combine(_root, "data", "dsh"));
            Environment.SetEnvironmentVariable("PATH",
                Path.Combine(_root, "node") + ";" + Environment.GetEnvironmentVariable("PATH"));

            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                // --no-open: upstream dsh web opens the default browser itself
                // (openBrowser defaults true); the launcher below is the single
                // owner of the browser handoff. Without this flag the URL
                // opens twice.
                Arguments = "\"" + dshEntry + "\" web --no-open --port " + port,
                WorkingDirectory = Path.Combine(_root, "app"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _child = Process.Start(psi);

            // Wait for the web UI to come up, then open the browser.
            var ready = WaitForHttp(url, TimeSpan.FromSeconds(60));
            if (ready) { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }

            SetupTray(url);
            Application.Run();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "DeepSeek Harness Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            KillChildTree();
        }
    }

    // Kill the dsh web process AND its whole subtree: a plain _child.Kill()
    // leaves orphaned grandchildren (code-runtime workers, ripgrep, …) alive
    // holding files — taskkill /T /F is the reliable tree kill on Windows
    // (2026-08-22).
    private static void KillChildTree()
    {
        try
        {
            if (_child != null && !_child.HasExited)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/PID " + _child.Id + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var k = Process.Start(psi)) { k.WaitForExit(5000); }
            }
        }
        catch { }
        try { if (_child != null && !_child.HasExited) _child.Kill(); } catch { }
    }

    private static bool PortInUse(int port)
    {
        // Bind-test with SO_REUSEADDR: on Windows this succeeds over a
        // TIME_WAIT remnant (a just-closed connection) but still fails
        // against an actively LISTENING socket. A plain TcpListener test
        // would misjudge TIME_WAIT as "in use" and needlessly fall back to
        // an ephemeral port right after the app was closed and reopened.
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return false;
        }
        catch { return true; }
        finally { try { s.Close(); } catch { } }
    }

    private static bool WaitForHttp(string url, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_child != null && _child.HasExited) return false;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 3000;
                using (var resp = (HttpWebResponse)req.GetResponse()) { return true; }
            }
            catch { Thread.Sleep(500); }
        }
        return false;
    }

    private static void SetupTray(string url)
    {
        // Use the DeepSeek icon embedded in this exe (/win32icon:) instead of
        // the generic application icon — SystemIcons.Application shows blank.
        System.Drawing.Icon trayIcon = null;
        try { trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        _tray = new NotifyIcon
        {
            Icon = trayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "DeepSeek Harness",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开界面", null, (s, e) =>
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }));
        menu.Items.Add("检查更新", null, (s, e) =>
        {
            // Fire-and-forget check: Update.exe --check only queries the
            // registry and shows a dialog; it is safe while we are running.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(_root, "Update.exe"),
                    Arguments = "--check",
                    WorkingDirectory = _root,
                    UseShellExecute = true,
                };
                Process.Start(psi);
            }
            catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (s, e) =>
        {
            KillChildTree();
            _tray.Visible = false;
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) =>
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
