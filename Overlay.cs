// Amuse Desktop Overlay
// A transparent, always-on-top, click-through WebView2 window that projects a
// web widget (browser source) onto the Windows desktop.
//
// No taskbar entry, no console window. Control it from the tray icon.
//
// Build with: powershell -ExecutionPolicy Bypass -File build.ps1
// Requires .NET Framework 4.8 + Microsoft Edge WebView2 Runtime.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AmuseOverlay
{
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;

        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public const uint LWA_COLORKEY = 0x00000001;
        public const uint LWA_ALPHA = 0x00000002;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOMOVE = 0x0002;
        public const int SWP_NOACTIVATE = 0x0010;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);
    }

    internal sealed class OverlayConfig
    {
        public const string DefaultUrl =
            "https://6klabs.com/widget/amuse/vjb4txyuby6rbwsevz8zbjijg3ltyt0d35bdjhze18nu9g41sypock4jqr9at4hr";

        public string Url = DefaultUrl;
        public int X = -1;
        public int Y = -1;
        public int Width = 600;
        public int Height = 200;
        public double Opacity = 1.0;
        public double Zoom = 1.0;
        public bool ClickThrough = true;
        public bool AlwaysOnTop = true;
        public bool Muted = true;
        public bool Visible = true;
    }

    internal sealed class OverlayForm : Form
    {
        // The color that becomes fully transparent. Keep it unusual so widget
        // artwork is unlikely to contain it. Note: with LWA_COLORKEY, R must be
        // 0, G any value, B non-zero for mouse hit-testing to keep working.
        private static readonly Color KeyColor = Color.FromArgb(0, 255, 1);
        private static readonly Color FrameColor = Color.FromArgb(238, 24, 26, 34);
        private static readonly uint KeyColorRef =
            (uint)(KeyColor.R | (KeyColor.G << 8) | (KeyColor.B << 16));

        private const int DragBarHeightDefault = 26;
        private const int GripSizeDefault = 18;
        private const int BorderSizeDefault = 7;
        private const int CornerSizeDefault = 20;

        private readonly string _configPath;
        private readonly string _logPath;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private OverlayConfig _cfg;

        private WebView2 _web;
        private Panel _dragBar;
        private Label _dragLabel;
        private Panel _grip;
        private Label _errorLabel;
        private readonly List<Panel> _handles = new List<Panel>();

        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _showItem;
        private ToolStripMenuItem _lockItem;
        private ToolStripMenuItem _topItem;
        private Icon _trayIcon;

        private System.Windows.Forms.Timer _saveTimer;
        private System.Windows.Forms.Timer _topmostTimer;
        private System.Windows.Forms.Timer _reloadTimer;
        private System.Windows.Forms.Timer _retryTimer;
        private System.Windows.Forms.Timer _trayMenuTimer;
        private FileSystemWatcher _watcher;

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _showEvent;

        private bool _webReady;
        private bool _initialized;
        private bool _needInitialSave;
        private double _uiScale = 1.0;
        private int _dragBarHeight = DragBarHeightDefault;
        private int _gripSize = GripSizeDefault;
        private int _borderSize = BorderSizeDefault;
        private int _cornerSize = CornerSizeDefault;
        private int _navRetryCount;
        private bool _resizing;
        private int _resizeEdges;
        private Point _resizeStart;
        private Rectangle _resizeStartBounds;
        private DateTime _suppressWatchUntil = DateTime.MinValue;
        private DateTime _lastNavigate = DateTime.MinValue;

        public OverlayForm(string configPath, Mutex mutex, EventWaitHandle showEvent)
        {
            _configPath = configPath;
            _logPath = Path.Combine(Path.GetDirectoryName(configPath), "overlay.log");
            _mutex = mutex;
            _showEvent = showEvent;

            bool configExisted = File.Exists(configPath);
            _cfg = LoadConfig(configPath);
            _needInitialSave = !configExisted;

            double sysScale = GetSystemScale();
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int logicalRight = (int)Math.Round(wa.Right / sysScale);
            if (_cfg.X < 0) _cfg.X = logicalRight - _cfg.Width - 24;
            if (_cfg.Y < 0) _cfg.Y = 24;

            // Window chrome: borderless, no taskbar entry, transparent key color.
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = _cfg.AlwaysOnTop;
            BackColor = KeyColor;
            Text = "Amuse Desktop Overlay";
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            MinimizeBox = false;
            MaximizeBox = false;
            SetBounds(ToPixels(_cfg.X, sysScale), ToPixels(_cfg.Y, sysScale),
                ToPixels(_cfg.Width, sysScale), ToPixels(_cfg.Height, sysScale));

            BuildWebView();
            BuildDragBar();
            BuildResizeHandles();
            BuildTimers();
            BuildTray();

            Log("Started. Config: " + _configPath);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;

                // Re-apply every time the native window is created so WinForms
                // cannot drop these flags during UpdateStyles / handle recreation.
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
                if (_cfg == null || _cfg.ClickThrough)
                    cp.ExStyle |= Native.WS_EX_TRANSPARENT;

                return cp;
            }
        }

        private void BuildWebView()
        {
            _web = new WebView2();
            _web.Dock = DockStyle.None;
            _web.BackColor = KeyColor;
            _web.DefaultBackgroundColor = Color.Transparent;
            _web.AllowExternalDrop = false;
            Controls.Add(_web);
        }

        private void BuildDragBar()
        {
            _dragBar = new Panel();
            _dragBar.Height = _dragBarHeight;
            _dragBar.Dock = DockStyle.None;
            _dragBar.BackColor = FrameColor;
            _dragBar.Cursor = Cursors.SizeAll;
            _dragBar.Visible = false;

            _dragLabel = new Label();
            _dragLabel.AutoSize = false;
            _dragLabel.Dock = DockStyle.Fill;
            _dragLabel.TextAlign = ContentAlignment.MiddleCenter;
            _dragLabel.ForeColor = Color.WhiteSmoke;
            _dragLabel.BackColor = Color.Transparent;
            _dragLabel.Font = new Font("Microsoft YaHei UI", 9f);
            _dragLabel.Text = "拖动移动  ·  拖动边缘或四角缩放  ·  Ctrl+Alt+L 锁定";
            _dragLabel.Cursor = Cursors.SizeAll;
            _dragLabel.MouseDown += DragHandleMouseDown;
            _dragBar.MouseDown += DragHandleMouseDown;

            _dragBar.Controls.Add(_dragLabel);
            Controls.Add(_dragBar);
        }

        private void BuildResizeHandles()
        {
            // Order matters: edges first, then corners so corners get the mouse.
            AddResizeHandle(1, Cursors.SizeWE, false);              // left
            AddResizeHandle(2, Cursors.SizeWE, false);              // right
            AddResizeHandle(4, Cursors.SizeNS, false);              // top
            AddResizeHandle(8, Cursors.SizeNS, false);              // bottom
            AddResizeHandle(1 | 4, Cursors.SizeNWSE, false);        // top-left
            AddResizeHandle(2 | 4, Cursors.SizeNESW, false);        // top-right
            AddResizeHandle(1 | 8, Cursors.SizeNESW, false);        // bottom-left
            _grip = AddResizeHandle(2 | 8, Cursors.SizeNWSE, true); // bottom-right
            _grip.Paint += GripPaint;
        }

        private Panel AddResizeHandle(int edges, Cursor cursor, bool withGrip)
        {
            Panel p = new Panel();
            p.Tag = edges;
            p.Cursor = cursor;
            p.BackColor = FrameColor;
            p.Visible = false;
            p.MouseDown += ResizeHandleMouseDown;
            p.MouseMove += ResizeHandleMouseMove;
            p.MouseUp += ResizeHandleMouseUp;

            Controls.Add(p);
            _handles.Add(p);
            return p;
        }

        private void BuildTimers()
        {
            _saveTimer = new System.Windows.Forms.Timer();
            _saveTimer.Interval = 650;
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                SaveConfig();
            };

            _topmostTimer = new System.Windows.Forms.Timer();
            _topmostTimer.Interval = 3000;
            _topmostTimer.Tick += delegate
            {
                if (_cfg.AlwaysOnTop && Visible)
                {
                    Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
            };
            _topmostTimer.Start();

            _reloadTimer = new System.Windows.Forms.Timer();
            _reloadTimer.Interval = 500;
            _reloadTimer.Tick += delegate
            {
                _reloadTimer.Stop();
                ReloadConfigFromDisk();
            };

            _retryTimer = new System.Windows.Forms.Timer();
            _retryTimer.Interval = 4000;
            _retryTimer.Tick += delegate
            {
                _retryTimer.Stop();
                if (_webReady && _web.CoreWebView2 != null)
                {
                    try
                    {
                        Log("Retrying navigation (" + _navRetryCount + "/5): " + _cfg.Url);
                        _web.CoreWebView2.Navigate(_cfg.Url);
                    }
                    catch (Exception ex)
                    {
                        Log("Retry navigate failed: " + ex.Message);
                    }
                }
            };
        }

        private void BuildTray()
        {
            _trayIcon = CreateTrayIcon();

            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9f);

            _showItem = new ToolStripMenuItem("显示悬浮窗");
            _showItem.CheckOnClick = false;
            _showItem.Click += delegate { ToggleVisible(); };

            _lockItem = new ToolStripMenuItem("锁定（鼠标穿透）");
            _lockItem.CheckOnClick = false;
            _lockItem.Click += delegate { ToggleClickThrough(); };

            _topItem = new ToolStripMenuItem("窗口置顶");
            _topItem.CheckOnClick = false;
            _topItem.Click += delegate { ToggleTopMost(); };

            ToolStripMenuItem reloadItem = new ToolStripMenuItem("重新加载网页");
            reloadItem.Click += delegate { ReloadWeb(); };

            ToolStripMenuItem resetItem = new ToolStripMenuItem("重置位置和大小");
            resetItem.Click += delegate { ResetPosition(); };

            ToolStripMenuItem configItem = new ToolStripMenuItem("打开配置文件");
            configItem.Click += delegate { OpenConfig(); };

            ToolStripMenuItem openUrlItem = new ToolStripMenuItem("在浏览器中打开源地址");
            openUrlItem.Click += delegate { OpenInBrowser(_cfg.Url); };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { Close(); };

            _menu.Items.Add(_showItem);
            _menu.Items.Add(_lockItem);
            _menu.Items.Add(_topItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(reloadItem);
            _menu.Items.Add(resetItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(openUrlItem);
            _menu.Items.Add(configItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _menu.Opening += delegate
            {
                _showItem.Checked = Visible;
                _lockItem.Checked = _cfg.ClickThrough;
                _topItem.Checked = _cfg.AlwaysOnTop;
            };

            _trayMenuTimer = new System.Windows.Forms.Timer();
            _trayMenuTimer.Interval = 300;
            _trayMenuTimer.Tick += delegate
            {
                _trayMenuTimer.Stop();
                ShowTrayMenu();
            };

            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = "Amuse 桌面悬浮窗";
            _tray.Visible = true;
            _tray.MouseUp += TrayMouseUp;
            _tray.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleClickThrough();
            };
            _tray.BalloonTipTitle = "Amuse 桌面悬浮窗已启动";
            _tray.BalloonTipText =
                "双击托盘图标：锁定 / 解锁（鼠标穿透）\n" +
                "右键双击：显示但不置顶\n" +
                "右键单击：打开菜单";
            _tray.ShowBalloonTip(5000);
        }

        private void TrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            if (_trayMenuTimer.Enabled)
            {
                // Second right click within the double-click window.
                _trayMenuTimer.Stop();
                ShowNonTopmost();
            }
            else
            {
                _trayMenuTimer.Stop();
                _trayMenuTimer.Start();
            }
        }

        private void ShowTrayMenu()
        {
            try
            {
                if (_menu != null && _menu.Items.Count > 0)
                    _menu.Show(Cursor.Position);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            double scale = GetSystemScale();
            try
            {
                uint dpi = Native.GetDpiForWindow(Handle);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }
            if (scale <= 0 || scale > 4) scale = 1.0;
            _uiScale = scale;

            _dragBarHeight = Math.Max(20, (int)Math.Round(DragBarHeightDefault * scale));
            _borderSize = Math.Max(4, (int)Math.Round(BorderSizeDefault * scale));
            _cornerSize = Math.Max(12, (int)Math.Round(CornerSizeDefault * scale));
            _gripSize = _cornerSize;
            _dragBar.Height = _dragBarHeight;
            _dragLabel.Font = new Font("Microsoft YaHei UI",
                9f * (float)Math.Min(scale, 2.0));

            ApplyBoundsFromConfig();

            EnsureWindowStyles();
            ApplyLayeredAttributes();
            LayoutOverlay();

            Native.RegisterHotKey(Handle, 1,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.L);
            Native.RegisterHotKey(Handle, 2,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.H);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            _initialized = true;
            if (_needInitialSave) SaveConfig();
            Visible = _cfg.Visible;
            EnsureWindowStyles();
            ApplyLayeredAttributes();
            LayoutOverlay();

            // WinForms may touch the extended styles once more while showing.
            BeginInvoke(new Action(delegate
            {
                EnsureWindowStyles();
                ApplyLayeredAttributes();
                LayoutOverlay();
            }));

            StartShowListener();
            StartConfigWatcher();
            InitializeWebView();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { Native.UnregisterHotKey(Handle, 1); } catch { }
            try { Native.UnregisterHotKey(Handle, 2); } catch { }

            StopConfigWatcher();

            if (_trayMenuTimer != null)
            {
                _trayMenuTimer.Stop();
                _trayMenuTimer.Dispose();
                _trayMenuTimer = null;
            }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }

            if (_trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            try { if (_showEvent != null) _showEvent.Dispose(); } catch { }
            try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }

            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) ToggleClickThrough();
                else if (id == 2) ToggleVisible();
            }

            base.WndProc(ref m);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            ScheduleSave();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutOverlay();
            ScheduleSave();
        }

        // ---------------------------------------------------------------- web

        private async void InitializeWebView()
        {
            try
            {
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AmuseOverlay", "WebView2");
                Directory.CreateDirectory(dataDir);

                CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
                options.AdditionalBrowserArguments =
                    "--autoplay-policy=no-user-gesture-required " +
                    "--disable-background-timer-throttling " +
                    "--disable-renderer-backgrounding " +
                    "--disable-backgrounding-occluded-windows";

                CoreWebView2Environment env =
                    await CoreWebView2Environment.CreateAsync(null, dataDir, options);

                await _web.EnsureCoreWebView2Async(env);

                // The Chromium dark-mode canvas is #121212; make it transparent
                // so the parent window's chroma-key color shows through.
                try { _web.DefaultBackgroundColor = Color.Transparent; } catch { }

                CoreWebView2Settings settings = _web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.IsSwipeNavigationEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false;

                _web.CoreWebView2.IsMuted = _cfg.Muted;
                _web.ZoomFactor = _cfg.Zoom;

                // Force the widget page itself to stay transparent so the
                // chroma-key color underneath it can be keyed out.
                string css =
                    "html,body{background:transparent!important;background-color:transparent!important;" +
                    "overflow:hidden!important}" +
                    "::-webkit-scrollbar{width:0!important;height:0!important;display:none!important}";
                string js =
                    "(function(){function add(){try{" +
                    "if(document.getElementById('amuse-overlay-style'))return;" +
                    "var s=document.createElement('style');s.id='amuse-overlay-style';" +
                    "s.textContent=" + JsonQuote(css) + ";" +
                    "(document.head||document.documentElement).appendChild(s);" +
                    "}catch(e){}}" +
                    "if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',add);}" +
                    "else{add();}setTimeout(add,300);setTimeout(add,1500);})();";
                await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(js);

                _web.CoreWebView2.NavigationCompleted += delegate(object s, CoreWebView2NavigationCompletedEventArgs a)
                {
                    if (a.IsSuccess)
                    {
                        _navRetryCount = 0;
                        Log("Navigation completed successfully.");
                        return;
                    }

                    Log("Navigation failed: " + a.WebErrorStatus + " " + _cfg.Url);

                    if (ShouldRetry(a.WebErrorStatus) && _navRetryCount < 5)
                    {
                        _navRetryCount++;
                        _retryTimer.Stop();
                        _retryTimer.Start();
                    }
                };

                _web.CoreWebView2.ProcessFailed += delegate(object s, CoreWebView2ProcessFailedEventArgs a)
                {
                    Log("WebView process failed: " + a.ProcessFailedKind);
                    try { _web.CoreWebView2.Reload(); } catch { }
                };

                _webReady = true;
                _lastNavigate = DateTime.Now;
                _web.CoreWebView2.Navigate(_cfg.Url);
                Log("WebView initialized, navigating to " + _cfg.Url);
            }
            catch (Exception ex)
            {
                Log("WebView init failed: " + ex);
                ShowError(
                    "无法初始化 WebView2。\n\n" +
                    "请确认已安装 Microsoft Edge WebView2 运行时。\n\n" +
                    ex.Message);
            }
        }

        private void ReloadWeb()
        {
            try
            {
                if (_webReady && _web.CoreWebView2 != null)
                {
                    _web.CoreWebView2.Reload();
                }
            }
            catch (Exception ex)
            {
                Log("Reload failed: " + ex.Message);
            }
        }

        private void NavigateTo(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!_webReady || _web.CoreWebView2 == null) return;
            if (DateTime.Now - _lastNavigate < TimeSpan.FromMilliseconds(800)) return;

            try
            {
                _lastNavigate = DateTime.Now;
                _navRetryCount = 0;
                _web.CoreWebView2.Navigate(url);
                Log("Navigated to " + url);
            }
            catch (Exception ex)
            {
                Log("Navigate failed: " + ex.Message);
            }
        }

        private void ShowError(string message)
        {
            if (_errorLabel == null)
            {
                _errorLabel = new Label();
                _errorLabel.Dock = DockStyle.Fill;
                _errorLabel.BackColor = Color.FromArgb(235, 30, 30, 40);
                _errorLabel.ForeColor = Color.White;
                _errorLabel.TextAlign = ContentAlignment.MiddleCenter;
                _errorLabel.Font = new Font("Microsoft YaHei UI", 10f);
                Controls.Add(_errorLabel);
                _errorLabel.BringToFront();
            }

            _errorLabel.Text = message;
            _errorLabel.Visible = true;
        }

        // ------------------------------------------------------------- layout

        private void LayoutOverlay()
        {
            if (_web == null || _dragBar == null || _handles.Count == 0) return;

            bool edit = !_cfg.ClickThrough && _initialized;

            _dragBar.Visible = edit;
            for (int i = 0; i < _handles.Count; i++)
                _handles[i].Visible = edit;

            int w = ClientSize.Width;
            int h = ClientSize.Height;

            if (edit)
            {
                // Leave a visible frame around the widget while editing so every
                // edge and corner can be grabbed with the mouse.
                int b = Math.Min(_borderSize, Math.Max(0, Math.Min(w, h) / 6));
                int bar = _dragBarHeight;
                int c = Math.Min(_cornerSize, Math.Max(1, Math.Min(w, h) / 3));

                _web.Bounds = new Rectangle(
                    b, b + bar,
                    Math.Max(0, w - 2 * b),
                    Math.Max(0, h - b - (b + bar)));

                _dragBar.SetBounds(b, b, Math.Max(0, w - 2 * b), bar);

                _handles[0].SetBounds(0, 0, b, h);             // left
                _handles[1].SetBounds(w - b, 0, b, h);         // right
                _handles[2].SetBounds(0, 0, w, b);             // top
                _handles[3].SetBounds(0, h - b, w, b);         // bottom
                _handles[4].SetBounds(0, 0, c, c);             // top-left
                _handles[5].SetBounds(w - c, 0, c, c);         // top-right
                _handles[6].SetBounds(0, h - c, c, c);         // bottom-left
                _handles[7].SetBounds(w - c, h - c, c, c);     // bottom-right

                _dragBar.BringToFront();
                for (int i = 0; i < _handles.Count; i++)
                    _handles[i].BringToFront();
            }
            else
            {
                _web.Bounds = new Rectangle(0, 0, w, h);
            }
        }

        private void DragHandleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN,
                new IntPtr(Native.HTCAPTION), IntPtr.Zero);
        }

        private void GripPaint(object sender, PaintEventArgs e)
        {
            using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 1.2f))
            {
                for (int i = 0; i < 3; i++)
                {
                    int offset = (int)Math.Round((6 + i * 4) * _uiScale);
                    e.Graphics.DrawLine(p,
                        _gripSize - offset, _gripSize - 2,
                        _gripSize - 2, _gripSize - offset);
                }
            }
        }

        private void ResizeHandleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            Panel p = sender as Panel;
            if (p == null) return;

            _resizeEdges = p.Tag is int ? (int)p.Tag : 0;
            if (_resizeEdges == 0) return;

            _resizing = true;
            _resizeStart = Cursor.Position;
            _resizeStartBounds = Bounds;
            p.Capture = true;
        }

        private void ResizeHandleMouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;

            Point now = Cursor.Position;
            int dx = now.X - _resizeStart.X;
            int dy = now.Y - _resizeStart.Y;

            Rectangle r = _resizeStartBounds;
            int minW = ToPixels(200, _uiScale);
            int minH = Math.Max(ToPixels(100, _uiScale),
                _dragBarHeight + _borderSize * 2 + ToPixels(40, _uiScale));

            if ((_resizeEdges & 1) != 0)
            {
                int newLeft = Math.Min(r.Left + dx, r.Right - minW);
                r.Width = r.Right - newLeft;
                r.X = newLeft;
            }
            if ((_resizeEdges & 2) != 0)
            {
                r.Width = Math.Max(minW, r.Width + dx);
            }
            if ((_resizeEdges & 4) != 0)
            {
                int newTop = Math.Min(r.Top + dy, r.Bottom - minH);
                r.Height = r.Bottom - newTop;
                r.Y = newTop;
            }
            if ((_resizeEdges & 8) != 0)
            {
                r.Height = Math.Max(minH, r.Height + dy);
            }

            Bounds = r;
        }

        private void ResizeHandleMouseUp(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;

            Panel p = sender as Panel;
            if (p != null) p.Capture = false;

            _resizing = false;
            _resizeEdges = 0;
            SaveConfig();
        }

        // ------------------------------------------------------- window state

        private void ApplyBoundsFromConfig()
        {
            if (!IsHandleCreated) return;

            int x = _cfg.X < 0 ? Left : ToPixels(_cfg.X, _uiScale);
            int y = _cfg.Y < 0 ? Top : ToPixels(_cfg.Y, _uiScale);
            int w = Math.Max(ToPixels(200, _uiScale), ToPixels(_cfg.Width, _uiScale));
            int h = Math.Max(ToPixels(90, _uiScale), ToPixels(_cfg.Height, _uiScale));

            SetBounds(x, y, w, h);
        }

        private static double GetSystemScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    double s = g.DpiX / 96.0;
                    if (s > 0 && s <= 4) return s;
                }
            }
            catch { }
            return 1.0;
        }

        private static int ToPixels(int value, double scale)
        {
            return (int)Math.Round(value * scale);
        }

        private static bool ShouldRetry(CoreWebView2WebErrorStatus status)
        {
            switch (status)
            {
                case CoreWebView2WebErrorStatus.ConnectionAborted:
                case CoreWebView2WebErrorStatus.ConnectionReset:
                case CoreWebView2WebErrorStatus.Disconnected:
                case CoreWebView2WebErrorStatus.Timeout:
                case CoreWebView2WebErrorStatus.ServerUnreachable:
                case CoreWebView2WebErrorStatus.CannotConnect:
                case CoreWebView2WebErrorStatus.HostNameNotResolved:
                    return true;
                default:
                    return false;
            }
        }

        private void EnsureWindowStyles()
        {
            if (!IsHandleCreated) return;

            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;

            if (_cfg.ClickThrough)
                ex |= Native.WS_EX_TRANSPARENT;
            else
                ex &= ~Native.WS_EX_TRANSPARENT;

            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
        }

        private void ApplyLayeredAttributes()
        {
            if (!IsHandleCreated) return;

            EnsureWindowStyles();

            double opacity = _cfg.Opacity;
            if (opacity < 0.08) opacity = 0.08;
            if (opacity > 1.0) opacity = 1.0;

            byte alpha = (byte)Math.Round(opacity * 255.0);
            Native.SetLayeredWindowAttributes(Handle, KeyColorRef, alpha,
                Native.LWA_COLORKEY | Native.LWA_ALPHA);
        }

        private void ApplyClickThrough()
        {
            if (!IsHandleCreated) return;

            EnsureWindowStyles();
            ApplyLayeredAttributes();
            LayoutOverlay();
        }

        private void ApplyTopMost()
        {
            TopMost = _cfg.AlwaysOnTop;
            if (_cfg.AlwaysOnTop && IsHandleCreated)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        private void ToggleTopMost()
        {
            _cfg.AlwaysOnTop = !_cfg.AlwaysOnTop;
            ApplyTopMost();
            SaveConfig();
        }

        private void ShowNonTopmost()
        {
            _cfg.AlwaysOnTop = false;
            _cfg.Visible = true;
            ApplyTopMost();
            ShowOverlay();
            SaveConfig();

            if (_tray != null)
            {
                _tray.ShowBalloonTip(2000, "已显示（不置顶）",
                    "窗口已显示并取消置顶。需要重新置顶可在托盘菜单勾选“窗口置顶”。",
                    ToolTipIcon.Info);
            }
        }

        private void ToggleClickThrough()
        {
            _cfg.ClickThrough = !_cfg.ClickThrough;
            ApplyClickThrough();
            SaveConfig();

            if (!_cfg.ClickThrough)
            {
                ShowOverlay();
                _tray.ShowBalloonTip(2500, "已解锁",
                    "拖动顶部条移动，拖动四周边缘或四角缩放。再次按 Ctrl+Alt+L 锁定。",
                    ToolTipIcon.Info);
            }
        }

        private void ToggleVisible()
        {
            if (Visible)
            {
                Hide();
            }
            else
            {
                ShowOverlay();
            }

            SaveConfig();
        }

        private void ShowOverlay()
        {
            if (!Visible) Show();

            EnsureWindowStyles();
            ApplyLayeredAttributes();

            if (_cfg.AlwaysOnTop) TopMost = true;
            Native.SetWindowPos(Handle,
                _cfg.AlwaysOnTop ? Native.HWND_TOPMOST : IntPtr.Zero,
                0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            LayoutOverlay();
        }

        private void ResetPosition()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int logicalRight = (int)Math.Round(wa.Right / _uiScale);

            _cfg.Width = 600;
            _cfg.Height = 200;
            _cfg.X = logicalRight - _cfg.Width - 24;
            _cfg.Y = 24;

            ApplyBoundsFromConfig();
            SaveConfig();
            ShowOverlay();
        }

        private void OpenConfig()
        {
            try
            {
                if (!File.Exists(_configPath)) SaveConfig();
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _configPath + "\"");
            }
            catch (Exception ex)
            {
                Log("OpenConfig failed: " + ex.Message);
            }
        }

        private void OpenInBrowser(string url)
        {
            try
            {
                if (!string.IsNullOrEmpty(url))
                    System.Diagnostics.Process.Start(url);
            }
            catch (Exception ex)
            {
                Log("OpenInBrowser failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------- config

        private void ScheduleSave()
        {
            if (!_initialized || _saveTimer == null) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveConfig()
        {
            try
            {
                if (!_initialized) return;

                _cfg.X = (int)Math.Round(Left / _uiScale);
                _cfg.Y = (int)Math.Round(Top / _uiScale);
                _cfg.Width = (int)Math.Round(Width / _uiScale);
                _cfg.Height = (int)Math.Round(Height / _uiScale);

                _suppressWatchUntil = DateTime.Now.AddMilliseconds(1500);

                string json = ConfigToJson(_cfg);
                string dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = _configPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(_configPath))
                {
                    try { File.Replace(tmp, _configPath, null); }
                    catch
                    {
                        File.Copy(tmp, _configPath, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, _configPath);
                }
            }
            catch (Exception ex)
            {
                Log("SaveConfig failed: " + ex.Message);
            }
        }

        private OverlayConfig LoadConfig(string path)
        {
            OverlayConfig cfg = new OverlayConfig();

            try
            {
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    Dictionary<string, object> d =
                        _json.Deserialize<Dictionary<string, object>>(text);

                    if (d != null)
                    {
                        cfg.Url = GetString(d, "url", cfg.Url);
                        cfg.X = GetInt(d, "x", cfg.X);
                        cfg.Y = GetInt(d, "y", cfg.Y);
                        cfg.Width = GetInt(d, "width", cfg.Width);
                        cfg.Height = GetInt(d, "height", cfg.Height);
                        cfg.Opacity = GetDouble(d, "opacity", cfg.Opacity);
                        cfg.Zoom = GetDouble(d, "zoom", cfg.Zoom);
                        cfg.ClickThrough = GetBool(d, "clickThrough", cfg.ClickThrough);
                        cfg.AlwaysOnTop = GetBool(d, "alwaysOnTop", cfg.AlwaysOnTop);
                        cfg.Muted = GetBool(d, "muted", cfg.Muted);
                        cfg.Visible = GetBool(d, "visible", cfg.Visible);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("LoadConfig failed, using defaults: " + ex.Message);
            }

            Normalize(cfg);
            return cfg;
        }

        private static void Normalize(OverlayConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.Url))
                cfg.Url = OverlayConfig.DefaultUrl;

            cfg.Width = Math.Max(200, Math.Min(cfg.Width, 8000));
            cfg.Height = Math.Max(90, Math.Min(cfg.Height, 8000));

            if (cfg.Opacity < 0.08) cfg.Opacity = 0.08;
            if (cfg.Opacity > 1.0) cfg.Opacity = 1.0;

            if (cfg.Zoom < 0.25) cfg.Zoom = 0.25;
            if (cfg.Zoom > 5.0) cfg.Zoom = 5.0;

            if (cfg.X < -10000) cfg.X = -1;
            if (cfg.Y < -10000) cfg.Y = -1;
        }

        private void ReloadConfigFromDisk()
        {
            if (DateTime.Now < _suppressWatchUntil) return;

            try
            {
                if (!File.Exists(_configPath)) return;

                OverlayConfig newCfg = LoadConfig(_configPath);

                bool urlChanged = newCfg.Url != _cfg.Url;
                bool sizeChanged =
                    newCfg.Width != _cfg.Width ||
                    newCfg.Height != _cfg.Height ||
                    newCfg.X != _cfg.X ||
                    newCfg.Y != _cfg.Y;

                _cfg = newCfg;

                if (sizeChanged)
                {
                    _suppressWatchUntil = DateTime.Now.AddMilliseconds(800);
                    ApplyBoundsFromConfig();
                }

                if (_webReady)
                {
                    try { _web.CoreWebView2.IsMuted = _cfg.Muted; } catch { }
                    try { _web.ZoomFactor = _cfg.Zoom; } catch { }
                }

                ApplyTopMost();
                ApplyClickThrough();
                ApplyLayeredAttributes();

                Visible = _cfg.Visible;

                if (urlChanged) NavigateTo(_cfg.Url);

                Log("Config reloaded from disk.");
            }
            catch (Exception ex)
            {
                Log("ReloadConfigFromDisk failed: " + ex.Message);
            }
        }

        private static string ConfigToJson(OverlayConfig c)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"url\": " + JsonQuote(c.Url) + ",");
            sb.AppendLine("  \"x\": " + c.X + ",");
            sb.AppendLine("  \"y\": " + c.Y + ",");
            sb.AppendLine("  \"width\": " + c.Width + ",");
            sb.AppendLine("  \"height\": " + c.Height + ",");
            sb.AppendLine("  \"opacity\": " + c.Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"zoom\": " + c.Zoom.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"clickThrough\": " + (c.ClickThrough ? "true" : "false") + ",");
            sb.AppendLine("  \"alwaysOnTop\": " + (c.AlwaysOnTop ? "true" : "false") + ",");
            sb.AppendLine("  \"muted\": " + (c.Muted ? "true" : "false") + ",");
            sb.AppendLine("  \"visible\": " + (c.Visible ? "true" : "false"));
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string JsonQuote(string s)
        {
            if (s == null) return "\"\"";
            return new JavaScriptSerializer().Serialize(s);
        }

        private static string GetString(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return def;
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v); } catch { }
            }
            return def;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToBoolean(v); } catch { }
            }
            return def;
        }

        // ------------------------------------------------------------- watcher

        private void StartConfigWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                _watcher = new FileSystemWatcher(dir);
                _watcher.Filter = Path.GetFileName(_configPath);
                _watcher.NotifyFilter =
                    NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                _watcher.Changed += WatchEvent;
                _watcher.Created += WatchEvent;
                _watcher.Renamed += delegate(object s, RenamedEventArgs e) { WatchEvent(s, e); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log("StartConfigWatcher failed: " + ex.Message);
            }
        }

        private void StopConfigWatcher()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            catch { }
        }

        private void WatchEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(delegate
                {
                    if (_reloadTimer != null)
                    {
                        _reloadTimer.Stop();
                        _reloadTimer.Start();
                    }
                }));
            }
            catch { }
        }

        private void StartShowListener()
        {
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    try { _showEvent.WaitOne(); }
                    catch { return; }

                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(ShowOverlay));
                    }
                    catch { return; }
                }
            });
            t.IsBackground = true;
            t.Name = "AmuseOverlayShowListener";
            t.Start();
        }

        // ----------------------------------------------------------------- ui

        private void Log(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                FileInfo fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.Delete(_logPath);

                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message +
                    Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        private static Icon CreateTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32),
                    Color.FromArgb(255, 120, 70),
                    Color.FromArgb(220, 55, 160),
                    45f))
                {
                    g.FillEllipse(brush, 1, 1, 30, 30);
                }

                Point[] triangle = new Point[]
                {
                    new Point(13, 9),
                    new Point(13, 23),
                    new Point(23, 16)
                };
                g.FillPolygon(Brushes.White, triangle);

                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string configPath = DefaultConfigPath();

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length)
                {
                    configPath = Path.GetFullPath(args[i + 1]);
                    i++;
                }
            }

            string configDir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDir)) Directory.CreateDirectory(configDir);

            bool createdNew;
            Mutex mutex = new Mutex(true, "AmuseOverlay_SingleInstance_Mutex", out createdNew);

            if (!createdNew)
            {
                // Already running: ask the existing instance to come to the front.
                try
                {
                    EventWaitHandle ev;
                    if (EventWaitHandle.TryOpenExisting("AmuseOverlay_Show_Event", out ev))
                    {
                        ev.Set();
                        ev.Dispose();
                    }
                }
                catch { }
                return;
            }

            EventWaitHandle showEvent =
                new EventWaitHandle(false, EventResetMode.AutoReset, "AmuseOverlay_Show_Event");

            Application.Run(new OverlayForm(configPath, mutex, showEvent));
        }

        private static string DefaultConfigPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmuseOverlay");
            return Path.Combine(dir, "config.json");
        }
    }
}
