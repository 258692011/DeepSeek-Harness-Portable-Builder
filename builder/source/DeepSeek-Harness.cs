using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    private static Process _child;
    private static NotifyIcon _tray;
    private static string _root;
    private static string _url;
    private static int _port;
    private static string _revealEvent;
    private static Mutex _instanceMutex; // held for the process lifetime: one instance per portable path
    private static Form _shell;
    private static WebView2 _web;
    private static bool _exiting;

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

            // Single instance per portable PATH (not per machine): a named mutex
            // derived from the root directory stops a second launcher of the SAME
            // copy from starting. Same path = same data\dsh + data\webview2, and
            // two processes must never share those (WebView2 refuses a user data
            // folder already in use by another process). A second double-click of
            // the same copy reveals the running window instead of booting again.
            string pathKey = StableHash(_root);
            string mutexName = "DeepSeekHarnessPortable_Mutex_" + pathKey;
            _revealEvent = "DeepSeekHarnessPortable_Show_" + pathKey;
            _instanceMutex = new Mutex(false, mutexName);
            bool ownMutex;
            try { ownMutex = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownMutex = true; } // previous instance crashed: we took over
            if (!ownMutex)
            {
                // Another launcher of THIS path is already running — ask it to
                // show its window and exit. If it is still booting, the reveal
                // signal is lost and it shows its own window when ready.
                RevealShell();
                return 0;
            }

            // Port selection: 3080 is the canonical port the first instance of
            // any copy uses (README documents it). Copies launched from OTHER
            // paths (or a foreign program holding 3080) must not collide with
            // the running instance — instead of failing or hijacking it, boot on
            // an OS-assigned random port (node's "--port 0" semantics, resolved
            // HERE so the launcher knows the concrete URL for the app window).
            const int canonicalPort = 3080;
            _port = PortInUse(canonicalPort) ? GetFreePort() : canonicalPort;
            _url = "http://127.0.0.1:" + _port + "/";

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

            _child = StartDshWeb(nodeExe, dshEntry);

            // Wait for the web UI to come up, then open the app window.
            var ready = WaitForHttp(_url, TimeSpan.FromSeconds(60));
            if (!ready && _child != null && _child.HasExited)
            {
                // The chosen port lost the bind race: another copy grabbed it
                // in the same instant (two different-path launches within
                // milliseconds, both saw 3080 free). Retry once on a fresh
                // OS-assigned port so the copy still comes up.
                _port = GetFreePort();
                _url = "http://127.0.0.1:" + _port + "/";
                _child = StartDshWeb(nodeExe, dshEntry);
                ready = WaitForHttp(_url, TimeSpan.FromSeconds(60));
            }
            if (ready)
            {
                BuildShell();
                ShowShell();
            }
            else
            {
                MessageBox.Show("dsh web 未能启动，请查看日志。\r\n" + _url,
                    "DeepSeek Harness Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            SetupTray();
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
            _exiting = true;
            KillChildTree();
        }
    }

    // Boot dsh web on the CURRENT _port. --no-open: upstream dsh web opens the
    // default browser itself (openBrowser defaults true); the app window is the
    // single owner of the UI, without the flag the URL would open twice.
    private static Process StartDshWeb(string nodeExe, string dshEntry)
    {
        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = "\"" + dshEntry + "\" web --no-open --port " + _port,
            WorkingDirectory = Path.Combine(_root, "app"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi);
    }

    // ------------------------------------------------------------------
    // WebView2 app window (the desktop UI)
    // ------------------------------------------------------------------

    private static void BuildShell()
    {
        // Use the DeepSeek icon embedded in this exe (/win32icon:) for the
        // title bar and taskbar — the default form icon is the generic .NET
        // one, which reads as "wrong icon".
        System.Drawing.Icon winIcon = null;
        try { winIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        _shell = new Form
        {
            Text = "DeepSeek Harness",
            Icon = winIcon ?? System.Drawing.SystemIcons.Application,
            // First-run default window size; the user's size is remembered in
            // data\webview2\window-state.ini and wins on later launches.
            Width = 1200,
            Height = 860,
            StartPosition = FormStartPosition.CenterScreen,
            // A real desktop app: minimize-to-tray instead of exiting when the
            // window is closed; the tray "退出" is the only exit path (same as
            // the old browser behaviour where closing the tab kept the tray).
            ShowInTaskbar = true,
            // dsh web is fluid, but a narrow window cramps the three-column
            // layout (sidebar + session list + chat); keep a sane floor.
            MinimumSize = new System.Drawing.Size(800, 600),
        };
        _web = new WebView2 { Dock = DockStyle.Fill };
        _shell.Controls.Add(_web);
        RestoreWindowState();

        _shell.FormClosing += (s, e) =>
        {
            // Closing the window hides it to the tray, never exits the app.
            // The only exit is the tray menu (or the explicit Shutdown).
            if (!_exiting)
            {
                SaveWindowState();
                e.Cancel = true;
                _shell.Hide();
            }
        };

        _shell.Load += async (s, e) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(_root, "data", "webview2"),
                    options: null);
                await _web.EnsureCoreWebView2Async(env);

                var core = _web.CoreWebView2;

                // Match normal-browser behaviour: anything that navigates away
                // from the app origin (external links, file:// from a dropped
                // file, target=_blank popups) opens in the system default
                // browser instead of hijacking the app window.
                core.NewWindowRequested += (s2, e2) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = e2.Uri, UseShellExecute = true });
                    }
                    catch { }
                    e2.Handled = true;
                };
                core.NavigationStarting += (s2, e2) =>
                {
                    if (!IsAppUrl(e2.Uri))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = e2.Uri, UseShellExecute = true });
                        }
                        catch { }
                        e2.Cancel = true;
                    }
                };

                // Window title stays fixed at "DeepSeek Harness" (the tray and
                // taskbar identity) — do NOT follow the page <title>.
                core.Navigate(_url);
            }
            catch (Exception ex)
            {
                // WebView2 Runtime missing (or failed to init): guide the user.
                var r = MessageBox.Show(
                    "启动内置窗口需要 WebView2 运行时（Windows 10/11 大多自带）。\r\n\r\n" +
                    "错误: " + ex.Message + "\r\n\r\n是否打开官方下载页？",
                    "DeepSeek Harness Portable", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://developer.microsoft.com/microsoft-edge/webview2/",
                            UseShellExecute = true,
                        });
                    }
                    catch { }
                }
                // Fall back to the default browser so the app stays usable.
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = _url, UseShellExecute = true });
                }
                catch { }
            }
        };
    }

    private static void ShowShell()
    {
        if (_shell == null) return;
        if (_shell.InvokeRequired)
        {
            _shell.Invoke(new Action(ShowShell));
            return;
        }
        _shell.Show();
        _shell.WindowState = FormWindowState.Normal;
        _shell.Activate();
        // Activate() alone is silently refused when another process owns the
        // Windows foreground lock (e.g. a second double-click spawns a fresh
        // process that briefly grabs foreground, then exits) — the window then
        // reappears BEHIND other apps. ForceForeground steals the focus.
        ForceForeground(_shell.Handle);
    }

    // Windows restricts which process may set the foreground window. The
    // reliable workaround: attach our input queue to the current foreground
    // thread, call SetForegroundWindow, then detach. ShowWindowAsync(SW_RESTORE)
    // also un-minimizes the window, covering the tray-restore path.
    private static void ForceForeground(IntPtr hWnd)
    {
        ShowWindowAsync(hWnd, SW_RESTORE);
        IntPtr fg = GetForegroundWindow();
        uint fgThread = 0;
        if (fg != IntPtr.Zero)
        {
            uint fgPid;
            fgThread = GetWindowThreadProcessId(fg, out fgPid);
        }
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != thisThread
            && AttachThreadInput(fgThread, thisThread, true);
        try { SetForegroundWindow(hWnd); }
        finally { if (attached) AttachThreadInput(fgThread, thisThread, false); }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    private const int SW_RESTORE = 9;

    // Bring the running instance's window forward from a second double-click
    // (cross-process: same machine, same copy — a named event scoped to this
    // portable's path, so copies at different paths never trigger each other).
    private static void RevealShell()
    {
        try
        {
            using (var evt = new EventWaitHandle(false, EventResetMode.AutoReset, _revealEvent))
            {
                evt.Set();
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Window state persistence (remember the user's window size)
    // ------------------------------------------------------------------

    private static string WindowStateFile
    {
        get { return Path.Combine(_root, "data", "webview2", "window-state.ini"); }
    }

    // Apply the previously saved window size (if any) before the window shows.
    private static void RestoreWindowState()
    {
        try
        {
            var lines = File.ReadAllLines(WindowStateFile);
            int w = 0, h = 0;
            bool inWindow = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inWindow = string.Equals(trimmed, "[Window]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inWindow) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                var key = trimmed.Substring(0, eq).Trim();
                var value = trimmed.Substring(eq + 1).Trim();
                int v;
                if (!int.TryParse(value, out v)) continue;
                if (key == "width") w = v;
                else if (key == "height") h = v;
            }
            if (w >= _shell.MinimumSize.Width && h >= _shell.MinimumSize.Height)
            {
                _shell.Width = w;
                _shell.Height = h;
            }
        }
        catch { /* first run / unreadable state — keep the default size */ }
    }

    // Persist the current window size on hide-to-tray and on shutdown.
    private static void SaveWindowState()
    {
        try
        {
            if (_shell == null) return;
            string dir = Path.GetDirectoryName(WindowStateFile);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(WindowStateFile,
                "[Window]" + Environment.NewLine +
                "width=" + _shell.Width + Environment.NewLine +
                "height=" + _shell.Height + Environment.NewLine);
        }
        catch { /* best-effort; a read-only portable still works with the default size */ }
    }

    // ------------------------------------------------------------------
    // Tray
    // ------------------------------------------------------------------

    private static void SetupTray()
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
        menu.Items.Add("打开界面", null, (s, e) => ShowShell());
        menu.Items.Add("打开网页", null, (s, e) =>
        {
            // Same UI in the system default browser — handy when the WebView2
            // Runtime is missing or the user prefers a real browser tab.
            try
            {
                Process.Start(new ProcessStartInfo { FileName = _url, UseShellExecute = true });
            }
            catch { }
        });
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
        menu.Items.Add("退出", null, (s, e) => Shutdown());
        _tray.ContextMenuStrip = menu;
        // Left single-click shows the window (double-click on a tray icon is
        // the historical default, but single-click is the expected gesture).
        _tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowShell();
        };
        // Cross-process reveal listener: a second double-click of this same
        // portable sets the per-path event; show the window and swallow it
        // (auto-reset). Copies at other paths have their own event name.
        var revealThread = new Thread(() =>
        {
            try
            {
                using (var evt = new EventWaitHandle(false, EventResetMode.AutoReset, _revealEvent))
                {
                    while (true)
                    {
                        evt.WaitOne();
                        ShowShell();
                    }
                }
            }
            catch { /* the named event is per-session; a miss is fine */ }
        });
        revealThread.IsBackground = true;
        revealThread.Start();
    }

    private static void Shutdown()
    {
        _exiting = true;
        SaveWindowState(); // the window may never have been closed (hide-to-tray)
        try { if (_tray != null) _tray.Visible = false; } catch { }
        Application.Exit();
    }

    // ------------------------------------------------------------------
    // Process tree management
    // ------------------------------------------------------------------

    // Kill the dsh web process AND its whole subtree: a plain _child.Kill()
    // leaves orphaned grandchildren (code-runtime workers, ripgrep, …) alive
    // holding files — taskkill /T /F is the reliable tree kill on Windows.
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

    // FNV-1a hash of the portable root (case-insensitive): stable across runs
    // and processes, used to scope the per-path mutex and reveal event so that
    // copies at different paths never interfere with each other.
    private static string StableHash(string s)
    {
        uint hash = 2166136261u;
        foreach (char c in s.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash.ToString("x8");
    }

    // OS-assigned free port on loopback — the launcher-side equivalent of
    // node's "--port 0": bind port 0, read the assigned port, release it.
    // dsh web must be told the concrete port (the launcher needs the URL for
    // the app window), so node cannot pick it itself.
    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            l.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally { l.Stop(); }
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

    // Whether a URL belongs to the app itself (the local dsh web server) — used
    // to keep external links out of the app window and in the system browser,
    // mirroring how a normal browser tab would handle them. Compares against the
    // ACTUAL port this instance runs on (3080 or a random fallback port), not a
    // hard-coded one.
    private static bool IsAppUrl(string uri)
    {
        try
        {
            var u = new Uri(uri);
            return u.IsLoopback
                && (u.Port == _port || u.Port == -1)
                && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }
        catch { return false; }
    }
}
