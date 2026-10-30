using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Screener
{
    internal static class Program
    {
        static Mutex _mutex;

        [STAThread]
        static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            if (args.Length > 0 && args[0] == "/selftest")
                return SelfTest();

            bool created;
            _mutex = new Mutex(true, @"Local\Screener.RegionCapture", out created);
            if (!created)
            {
                MessageBox.Show("Screener уже запущен — значок у часов.", "Screener");
                return 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate { };
            Application.Run(new TrayApp());
            return 0;
        }

        static int SelfTest()
        {
            int x = Native.GetSystemMetrics(76);
            int y = Native.GetSystemMetrics(77);
            using (var bmp = ScreenGrab.Grab(new Rectangle(x, y, 24, 24)))
            {
                if (bmp == null || bmp.Width != 24 || bmp.Height != 24)
                    return 1;
            }
            return 0;
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _icon;
        readonly CaptureController _capture;
        readonly PhoneInbox _phone;
        readonly ToolStripMenuItem _autoStart;

        public TrayApp()
        {
            _icon = new NotifyIcon();
            _icon.Icon = AppIcon.Create();
            _icon.Text = "Screener: Ctrl+Shift и протяните мышь";
            _icon.Visible = true;

            _autoStart = new ToolStripMenuItem("Запускать с Windows");
            _autoStart.Checked = AutoStart.IsEnabled();
            _autoStart.Click += delegate { ToggleAutoStart(); };

            _capture = new CaptureController(_icon);
            _capture.Start();
            _phone = new PhoneInbox(_capture);

            var address = new ToolStripMenuItem(PhoneInbox.AddressLine());
            address.Click += delegate
            {
                try { Clipboard.SetText(PhoneInbox.PrimaryUrl()); }
                catch { }
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(address);
            menu.Items.Add(_autoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitThread(); });
            _icon.ContextMenuStrip = menu;
        }

        void ToggleAutoStart()
        {
            AutoStart.Set(!_autoStart.Checked);
            _autoStart.Checked = AutoStart.IsEnabled();
        }

        protected override void ExitThreadCore()
        {
            _phone.Dispose();
            _capture.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            base.ExitThreadCore();
        }
    }

    internal sealed class PhoneInbox : IDisposable
    {
        public const int Port = 47123;
        const int MaxBytes = 25 * 1024 * 1024;

        readonly CaptureController _capture;
        readonly Form _gate;
        readonly TcpListener _listener;
        readonly Thread _thread;
        volatile bool _stop;

        public PhoneInbox(CaptureController capture)
        {
            _capture = capture;
            _gate = new Form();
            _gate.ShowInTaskbar = false;
            var handle = _gate.Handle;
            if (handle == IntPtr.Zero) throw new InvalidOperationException();
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public static string AddressLine()
        {
            var ip = FirstLanIp();
            if (ip == null) return "Телефон: нет сети";
            return "Телефон: " + ip + ":" + Port;
        }

        public static string PrimaryUrl()
        {
            var ip = FirstLanIp();
            if (ip == null) return "http://127.0.0.1:" + Port + "/shot";
            return "http://" + ip + ":" + Port + "/shot";
        }

        static IPAddress FirstLanIp()
        {
            IPAddress fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var title = (nic.Name + " " + nic.Description).ToLowerInvariant();
                if (title.Contains("virtual") || title.Contains("vmware") || title.Contains("hyper-v")
                    || title.Contains("vethernet") || title.Contains("wsl") || title.Contains("docker")
                    || title.Contains("bluetooth") || title.Contains("loopback"))
                    continue;
                var props = nic.GetIPProperties();
                bool gateway = false;
                foreach (var hop in props.GatewayAddresses)
                {
                    if (hop.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(hop.Address) || hop.Address.Equals(IPAddress.Any)) continue;
                    gateway = true;
                }
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    if (gateway) return addr.Address;
                    if (fallback == null) fallback = addr.Address;
                }
            }
            return fallback;
        }

        void Loop()
        {
            while (!_stop)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (_stop) break; continue; }
                ThreadPool.QueueUserWorkItem(Serve, client);
            }
        }

        void Serve(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;
                using (client)
                using (var stream = client.GetStream())
                {
                    byte[] body;
                    if (!ReadBody(stream, out body))
                    {
                        Write(stream, "400");
                        return;
                    }
                    Bitmap bmp;
                    try
                    {
                        using (var ms = new MemoryStream(body))
                        using (var img = Image.FromStream(ms))
                            bmp = new Bitmap(img);
                    }
                    catch
                    {
                        Write(stream, "400");
                        return;
                    }
                    _gate.BeginInvoke(new Action(delegate { _capture.AcceptPhoneImage(bmp); }));
                    Write(stream, "200");
                }
            }
            catch { try { client.Close(); } catch { } }
        }

        static bool ReadBody(NetworkStream stream, out byte[] body)
        {
            body = null;
            var header = new MemoryStream();
            var one = new byte[1];
            while (header.Length < 16384)
            {
                if (stream.Read(one, 0, 1) != 1) return false;
                header.WriteByte(one[0]);
                if (header.Length >= 4)
                {
                    var buf = header.GetBuffer();
                    int n = (int)header.Length;
                    if (buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                        break;
                }
            }
            var text = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length);
            if (text.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
            {
                body = new byte[0];
                return false;
            }
            int length = 0;
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(lines[i].Substring(15).Trim(), out length);
                }
            }
            if (length <= 0 || length > MaxBytes) return false;
            body = new byte[length];
            int got = 0;
            while (got < length)
            {
                int read = stream.Read(body, got, length - got);
                if (read <= 0) return false;
                got += read;
            }
            return true;
        }

        static void Write(NetworkStream stream, string code)
        {
            var payload = code == "200" ? "ok" : "no";
            var text = "HTTP/1.1 " + code + " OK\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n" + payload;
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _gate.Dispose(); } catch { }
        }
    }

    internal static class AutoStart
    {
        const string Name = "Screener";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                var value = key == null ? null : key.GetValue(Name) as string;
                return !string.IsNullOrEmpty(value);
            }
        }

        public static void Set(bool enable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) return;
                if (enable)
                    key.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else
                    key.DeleteValue(Name, false);
            }
        }
    }

    internal sealed class CaptureController : IDisposable
    {
        const int WhMouseLl = 14;
        const int WhKeyboardLl = 13;
        const int WmMouseMove = 0x0200;
        const int WmLButtonDown = 0x0201;
        const int WmLButtonUp = 0x0202;
        const int WmRButtonDown = 0x0204;
        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;
        const int VkLButton = 0x01;
        const int VkShift = 0x10;
        const int VkControl = 0x11;
        const int VkEscape = 0x1B;

        readonly NotifyIcon _icon;
        FrameForm _frame;
        readonly Native.HookProc _mouseProc;
        readonly Native.HookProc _keyProc;
        readonly System.Windows.Forms.Timer _timer;
        IntPtr _mouseHook;
        IntPtr _keyHook;
        IntPtr _cross;
        bool _tracking;
        bool _button;
        bool _chordDown;
        bool _brokeChord;
        bool _cancel;
        bool _drawing;
        bool _drawCommit;
        bool _penDown;
        DrawPad _pad;
        Point _a;
        Point _b;
        bool _disposed;
        static Bitmap _held;

        public CaptureController(NotifyIcon icon)
        {
            _icon = icon;
            _mouseProc = OnMouse;
            _keyProc = OnKey;
            _cross = Native.LoadCursor(IntPtr.Zero, (IntPtr)32515);
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15;
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _frame = new FrameForm();
            _mouseHook = Native.SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
            _keyHook = Native.SetWindowsHookEx(WhKeyboardLl, _keyProc, IntPtr.Zero, 0);
            _timer.Start();
        }

        void OnTick(object sender, EventArgs e)
        {
            try { Poll(); }
            catch { Cancel(); }
        }

        void Poll()
        {
            if (_disposed) return;
            bool chord = Native.IsDown(VkControl) && Native.IsDown(VkShift);
            var p = Cursor.Position;
            if (Native.IsDown(VkEscape))
                _cancel = true;

            if (_drawing)
            {
                if (_pad != null)
                {
                    if (Native.IsDown(VkLButton))
                    {
                        if (!_penDown)
                            _frame.Raise();
                        _penDown = true;
                        _pad.StrokeScreen(p);
                    }
                    else
                    {
                        _penDown = false;
                        _pad.Lift();
                    }
                }
                if (_cancel)
                    EndDraw(false);
                else if (!chord && _pad != null)
                    EndDraw(true);
                else if (!chord)
                    _drawCommit = true;
                return;
            }

            if (!_tracking)
            {
                if (chord && !_chordDown)
                {
                    _chordDown = true;
                    if (OpenClipboardDraw())
                        return;
                }
                else
                    _chordDown = chord;

                if (!_drawing && chord && Native.IsDown(VkLButton))
                {
                    Arm(p);
                    _button = true;
                }
                return;
            }

            if (_cancel)
            {
                if (!_button)
                    Finish(false);
                else
                    _frame.HideFrame();
                return;
            }

            if (!_button)
            {
                Finish(true);
                return;
            }

            _b = p;
            Native.SetCursor(_cross);
            var live = Norm(_a, _b);
            if (!_brokeChord && (live.Width >= 2 || live.Height >= 2))
            {
                _brokeChord = true;
                Native.Tap(0x87);
            }
            _frame.ShowSelection(live);
        }

        bool OpenClipboardDraw()
        {
            Bitmap bmp = null;
            try
            {
                if (!Clipboard.ContainsImage()) return false;
                using (var src = Clipboard.GetImage())
                {
                    if (src == null || src.Width < 1 || src.Height < 1) return false;
                    bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                        g.DrawImage(src, 0, 0, src.Width, src.Height);
                }
                Native.Tap(0x87);
                var rect = Fit(bmp);
                _drawing = true;
                _drawCommit = false;
                _penDown = false;
                _cancel = false;
                _pad = new DrawPad();
                _pad.Open(bmp, rect);
                bmp = null;
                _frame.ShowSelection(rect);
                _frame.Raise();
                return true;
            }
            catch
            {
                _drawing = false;
                if (_pad != null)
                {
                    try { _pad.Dispose(); } catch { }
                    _pad = null;
                    bmp = null;
                }
                if (bmp != null) bmp.Dispose();
                try { _frame.HideFrame(); } catch { }
                return false;
            }
        }

        static Rectangle Fit(Bitmap bmp)
        {
            int vx = Native.GetSystemMetrics(76);
            int vy = Native.GetSystemMetrics(77);
            int vw = Native.GetSystemMetrics(78);
            int vh = Native.GetSystemMetrics(79);
            int maxW = Math.Max(1, vw - 48);
            int maxH = Math.Max(1, vh - 48);
            double scale = Math.Min(1.0, Math.Min(maxW / (double)bmp.Width, maxH / (double)bmp.Height));
            int w = Math.Max(1, (int)Math.Round(bmp.Width * scale));
            int h = Math.Max(1, (int)Math.Round(bmp.Height * scale));
            return new Rectangle(vx + (vw - w) / 2, vy + (vh - h) / 2, w, h);
        }

        void Arm(Point p)
        {
            _tracking = true;
            _cancel = false;
            _brokeChord = false;
            _a = _b = p;
        }

        void Finish(bool copy)
        {
            var rect = _frame.Clamp(Norm(_a, _b));
            _tracking = false;
            _button = false;
            _cancel = false;
            bool draw = copy && rect.Width >= 2 && rect.Height >= 2
                && Native.IsDown(VkControl) && Native.IsDown(VkShift);
            if (!draw)
                _frame.HideFrame();
            if (draw)
            {
                _drawing = true;
                _drawCommit = false;
                OpenDrawSoon(rect);
                return;
            }
            if (copy && rect.Width >= 2 && rect.Height >= 2)
                CopySoon(rect);
        }

        void OpenDrawSoon(Rectangle rect)
        {
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 40;
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                if (_disposed || !_drawing) return;
                try
                {
                    try { Native.DwmFlush(); } catch { }
                    var bmp = ScreenGrab.Grab(rect);
                    if (bmp == null) throw new InvalidOperationException();
                    if (_drawCommit || !Native.IsDown(VkControl) || !Native.IsDown(VkShift))
                    {
                        _frame.HideFrame();
                        PutClipboard(bmp);
                        _drawing = false;
                        _drawCommit = false;
                        return;
                    }
                    _pad = new DrawPad();
                    _pad.Open(bmp, rect);
                    _frame.ShowSelection(rect);
                    _frame.Raise();
                }
                catch
                {
                    _drawing = false;
                    _drawCommit = false;
                    try { _frame.HideFrame(); } catch { }
                    Fail();
                }
            };
            timer.Start();
        }

        void EndDraw(bool copy)
        {
            _drawing = false;
            _drawCommit = false;
            _cancel = false;
            _frame.HideFrame();
            Bitmap bmp = null;
            if (_pad != null)
            {
                bmp = _pad.Take();
                _pad.Dispose();
                _pad = null;
            }
            if (copy && bmp != null)
                PutClipboard(bmp);
            else if (bmp != null)
                bmp.Dispose();
        }

        void Cancel()
        {
            _cancel = true;
            _tracking = false;
            _button = false;
            _drawing = false;
            try { _frame.HideFrame(); } catch { }
            if (_pad != null)
            {
                try { _pad.Dispose(); } catch { }
                _pad = null;
            }
        }

        IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    var info = (Native.MsLl)Marshal.PtrToStructure(lParam, typeof(Native.MsLl));
                    var pt = new Point(info.pt.X, info.pt.Y);
                    if (!_tracking)
                    {
                        if (!_drawing && msg == WmLButtonDown && Native.IsDown(VkControl) && Native.IsDown(VkShift))
                        {
                            Arm(pt);
                            _button = true;
                            return (IntPtr)1;
                        }
                    }
                    else
                    {
                        _b = pt;
                        if (msg == WmLButtonUp)
                            _button = false;
                        else if (msg == WmRButtonDown)
                            _cancel = true;
                        if (msg != WmMouseMove)
                            return (IntPtr)1;
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (_tracking || _drawing))
                {
                    int msg = wParam.ToInt32();
                    if (msg == WmKeyDown || msg == WmSysKeyDown)
                    {
                        var info = (Native.KbdLl)Marshal.PtrToStructure(lParam, typeof(Native.KbdLl));
                        if (info.vkCode == VkEscape)
                        {
                            _cancel = true;
                            return (IntPtr)1;
                        }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        void CopySoon(Rectangle rect)
        {
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 40;
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                if (_disposed) return;
                try
                {
                    try { Native.DwmFlush(); } catch { }
                    var bmp = ScreenGrab.Grab(rect);
                    if (bmp == null) throw new InvalidOperationException();
                    PutClipboard(bmp);
                }
                catch { Fail(); }
            };
            timer.Start();
        }

        public void AcceptPhoneImage(Bitmap bmp)
        {
            try
            {
                PutClipboard(bmp);
                _icon.BalloonTipTitle = "Screener";
                _icon.BalloonTipText = "Скрин с телефона в буфере";
                _icon.ShowBalloonTip(1500);
            }
            catch { Fail(); }
        }

        void PutClipboard(Bitmap bmp)
        {
            Clipboard.SetImage(bmp);
            var previous = _held;
            _held = bmp;
            if (previous != null) previous.Dispose();
            if (!Clipboard.ContainsImage())
                Fail();
        }

        void Fail()
        {
            try
            {
                _icon.BalloonTipTitle = "Screener";
                _icon.BalloonTipText = "Не удалось скопировать область";
                _icon.ShowBalloonTip(2000);
            }
            catch { }
        }

        static Rectangle Norm(Point a, Point b)
        {
            return new Rectangle(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X),
                Math.Abs(a.Y - b.Y));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
            if (_frame != null) _frame.Dispose();
            if (_pad != null) _pad.Dispose();
        }
    }

    internal sealed class DrawPad : Form
    {
        Bitmap _bmp;
        Point _last;
        bool _down;
        float _map = 1f;
        readonly Pen _pen = new Pen(Color.Red, 3f);

        public DrawPad()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Cursor = Native.PenCursor();
            DoubleBuffered = true;
            _pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            _pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            _pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x08000000;
                p.ExStyle |= 0x00000080;
                return p;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x20)
            {
                Native.SetCursor(Native.Pen());
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == 0x21)
            {
                m.Result = (IntPtr)3;
                return;
            }
            base.WndProc(ref m);
        }

        public void Open(Bitmap bmp, Rectangle screen)
        {
            _bmp = bmp;
            _map = screen.Width > 0 ? bmp.Width / (float)screen.Width : 1f;
            if (_map < 0.01f) _map = 1f;
            _pen.Width = 3f * _map;
            Bounds = screen;
            Show();
            Native.SetWindowPos(Handle, new IntPtr(-1), screen.X, screen.Y, screen.Width, screen.Height, 0x0010 | 0x0040);
            Invalidate();
        }

        Point Map(Point local)
        {
            int x = (int)(local.X * _map);
            int y = (int)(local.Y * _map);
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (_bmp != null && x >= _bmp.Width) x = _bmp.Width - 1;
            if (_bmp != null && y >= _bmp.Height) y = _bmp.Height - 1;
            return new Point(x, y);
        }

        public void StrokeScreen(Point screen)
        {
            var local = new Point(screen.X - Left, screen.Y - Top);
            if (_bmp == null || local.X < 0 || local.Y < 0 || local.X >= Width || local.Y >= Height) return;
            var p = Map(local);
            if (!_down)
            {
                _down = true;
                _last = p;
            }
            Stroke(_last, p);
            _last = p;
        }

        public void Lift()
        {
            _down = false;
        }

        public Bitmap Take()
        {
            var bmp = _bmp;
            _bmp = null;
            Hide();
            return bmp;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _bmp == null) return;
            _down = true;
            _last = Map(e.Location);
            Stroke(_last, _last);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_down || _bmp == null) return;
            var p = Map(e.Location);
            Stroke(_last, p);
            _last = p;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
        }

        void Stroke(Point a, Point b)
        {
            using (var g = Graphics.FromImage(_bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLine(_pen, a, b);
            }
            float inv = _map > 0f ? 1f / _map : 1f;
            int pad = (int)(4f * inv) + 2;
            int l = (int)(Math.Min(a.X, b.X) * inv) - pad;
            int t = (int)(Math.Min(a.Y, b.Y) * inv) - pad;
            int r = (int)(Math.Max(a.X, b.X) * inv) + pad;
            int bt = (int)(Math.Max(a.Y, b.Y) * inv) + pad;
            Invalidate(Rectangle.FromLTRB(l, t, r, bt));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_bmp == null) return;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_bmp, ClientRectangle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pen.Dispose();
                if (_bmp != null) _bmp.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class FrameForm : IDisposable
    {
        readonly Edge[] _edges = { new Edge(true), new Edge(false), new Edge(true), new Edge(false) };

        public Rectangle Clamp(Rectangle rect)
        {
            return Rectangle.Intersect(rect, VirtualScreen());
        }

        public void ShowSelection(Rectangle rect)
        {
            int w = Math.Max(rect.Width, 1);
            int h = Math.Max(rect.Height, 1);
            _edges[0].Place(rect.X, rect.Y, w);
            _edges[1].Place(rect.X, rect.Y, h);
            _edges[2].Place(rect.X, rect.Bottom - 1, w);
            _edges[3].Place(rect.Right - 1, rect.Y, h);
        }

        public void HideFrame()
        {
            for (int i = 0; i < _edges.Length; i++)
                _edges[i].HideEdge();
        }

        public void Raise()
        {
            for (int i = 0; i < _edges.Length; i++)
                _edges[i].Raise();
        }

        public void Dispose()
        {
            for (int i = 0; i < _edges.Length; i++)
                _edges[i].Dispose();
        }

        static Rectangle VirtualScreen()
        {
            return new Rectangle(
                Native.GetSystemMetrics(76),
                Native.GetSystemMetrics(77),
                Native.GetSystemMetrics(78),
                Native.GetSystemMetrics(79));
        }

        sealed class Edge : Form
        {
            readonly bool _horizontal;
            bool _regioned;
            bool _shown;
            Rectangle _place;

            public Edge(bool horizontal)
            {
                _horizontal = horizontal;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                TopMost = true;
                BackColor = Color.FromArgb(255, 40, 40);
                Cursor = Native.PenCursor();
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var p = base.CreateParams;
                    p.ExStyle |= 0x00000080;
                    p.ExStyle |= 0x00000008;
                    p.ExStyle |= 0x08000000;
                    return p;
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x20)
                {
                    Native.SetCursor(Native.Pen());
                    m.Result = (IntPtr)1;
                    return;
                }
                if (m.Msg == 0x21)
                {
                    m.Result = (IntPtr)3;
                    return;
                }
                base.WndProc(ref m);
            }

            public void Place(int x, int y, int length)
            {
                if (length < 1)
                {
                    HideEdge();
                    return;
                }
                int w = _horizontal ? length : 1;
                int h = _horizontal ? 1 : length;
                var place = new Rectangle(x, y, w, h);
                var handle = Handle;
                if (!_regioned)
                {
                    Native.ApplyDash(handle, _horizontal);
                    Native.SetWindowDisplayAffinity(handle, 0x11);
                    _regioned = true;
                }
                if (_place.Equals(place) && _shown)
                    return;
                _place = place;
                Native.SetWindowPos(handle, new IntPtr(-1), x, y, w, h, 0x0010 | 0x0040);
                _shown = true;
            }

            public void Raise()
            {
                if (_shown && IsHandleCreated)
                    Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002);
            }

            public void HideEdge()
            {
                if (_shown && IsHandleCreated)
                    Native.ShowWindow(Handle, 0);
                _shown = false;
                _place = Rectangle.Empty;
            }
        }
    }

    internal static class ScreenGrab
    {
        public static Bitmap Grab(Rectangle rect)
        {
            if (rect.Width < 1 || rect.Height < 1) return null;
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(rect.Location, Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }
    }

    internal static class AppIcon
    {
        public static Icon Create()
        {
            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(Color.FromArgb(255, 40, 40)))
                        g.FillEllipse(brush, 1, 1, 30, 30);
                    using (var pen = new Pen(Color.White, 2.2f))
                        g.DrawRectangle(pen, 9, 10, 13, 10);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    internal static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct WinPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MsLl
        {
            public WinPoint pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KbdLl
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr LoadCursorFromFile(string file);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        static IntPtr _pen;
        static Cursor _penCursor;

        public static IntPtr Pen()
        {
            if (_pen == IntPtr.Zero)
                _pen = LoadCursorFromFile(@"C:\Windows\Cursors\aero_pen.cur");
            if (_pen == IntPtr.Zero)
                _pen = LoadCursor(IntPtr.Zero, (IntPtr)32515);
            return _pen;
        }

        public static Cursor PenCursor()
        {
            if (_penCursor == null)
            {
                try { _penCursor = new Cursor(@"C:\Windows\Cursors\aero_pen.cur"); }
                catch { _penCursor = Cursors.Cross; }
            }
            return _penCursor;
        }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);

        public static void Tap(byte vk)
        {
            byte scan = (byte)MapVirtualKey(vk, 0);
            keybd_event(vk, scan, 0, UIntPtr.Zero);
            keybd_event(vk, scan, 2, UIntPtr.Zero);
        }

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        static IntPtr _dashH;
        static IntPtr _dashV;

        public static void ApplyDash(IntPtr hwnd, bool horizontal)
        {
            if (_dashH == IntPtr.Zero)
            {
                _dashH = MakeDash(true);
                _dashV = MakeDash(false);
            }
            IntPtr copy = CreateRectRgn(0, 0, 0, 0);
            IntPtr src = horizontal ? _dashH : _dashV;
            CombineRgn(copy, src, src, 5);
            if (SetWindowRgn(hwnd, copy, true) == 0)
                DeleteObject(copy);
        }

        static IntPtr MakeDash(bool horizontal)
        {
            const int length = 16000;
            const int on = 4;
            const int step = 7;
            IntPtr acc = CreateRectRgn(0, 0, 0, 0);
            for (int i = 0; i < length; i += step)
            {
                int seg = Math.Min(on, length - i);
                IntPtr piece = horizontal
                    ? CreateRectRgn(i, 0, i + seg, 1)
                    : CreateRectRgn(0, i, 1, i + seg);
                CombineRgn(acc, acc, piece, 2);
                DeleteObject(piece);
            }
            return acc;
        }

        public static bool IsDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        public static void ForceTop(IntPtr hwnd)
        {
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0040);
        }
    }
}
