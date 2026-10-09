using LibVLCSharp.WinForms;
using Serilog;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QocastPlayer
{
    // The one floating window: the picture, with a status text on top while there is none.
    // Right-click: mute, louder/quieter, always on top.
    public sealed class PlayerWindow : Form
    {
        private const int VolumeStep = 10;
        private const int WmSizing = 0x0214;
        private const int WmszLeft = 1;
        private const int WmszRight = 2;
        private const int WmszTop = 3;
        private const int WmszTopLeft = 4;
        private const int WmszTopRight = 5;
        private const int WmszBottom = 6;
        private const int WmszBottomLeft = 7;
        private const int WmszBottomRight = 8;
        private const int WmNcHitTest = 0x0084;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 2;
        private const int Edge = 5;              // without title bar: black edge for resizing (px)

        private readonly PlayerConfig _config;
        private readonly Label _status;
        private readonly ToolStripMenuItem _volumeItem;
        private readonly ToolStripMenuItem _muteItem;
        private readonly ToolStripMenuItem _onTopItem;
        private readonly ToolStripMenuItem _titleBarItem;
        private const int BarHeight = 26;
        private readonly Panel _bar;
        private readonly Label _speaker;
        private readonly VolumeSlider _slider;
        private readonly Label _volumeText;
        private readonly LevelMeter _meter;
        private readonly Label _station;
        private readonly AudioSession _audio = new AudioSession();
        private bool _windowsSound;
        private readonly Timer _meterTimer = new Timer { Interval = 50 };

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private double _videoAspectRatio = 16.0 / 9.0;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public VideoView Video { get; }

        // muted / volume changed by the user: (muted, volume)
        public event Action<bool, int> SoundChanged;

        // true: Windows sets volume and mute (AudioSession), VLC plays at full volume
        public bool WindowsSound { get { return _windowsSound; } }

        // station name of the received stream, null while unknown (read every 50 ms)
        public Func<string> StationName { get; set; }

        public PlayerWindow(PlayerConfig config)
        {
            _config = config;

            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(320, 200);
            TopMost = config.always_on_top;

            Video = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };

            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gainsboro,
                BackColor = Color.Black,
                Font = new Font("Segoe UI", 14f),
                Text = "Waiting for QOCAST"
            };
            // bottom bar: speaker (click = mute), volume slider, percentage
            _speaker = new Label
            {
                Dock = DockStyle.Left, Width = 32, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gainsboro, Font = new Font("Segoe MDL2 Assets", 11f), Cursor = Cursors.Hand
            };
            _speaker.Click += (s, e) => _muteItem.Checked = !_muteItem.Checked;
            _slider = new VolumeSlider { Dock = DockStyle.Left, Width = 110 };
            _slider.VolumeChosen += volume => SetVolume(volume);
            _volumeText = new Label
            {
                Dock = DockStyle.Left, Width = 48, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Gray, Font = new Font("Segoe UI", 9f), Padding = new Padding(6, 0, 0, 0)
            };
            // VU meter on the right
            _meter = new LevelMeter { Dock = DockStyle.Right, Width = 150 };
            _meter.MouseDown += DragWindow;
            // station name (callsign) from the stream, in the middle
            _station = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gainsboro, Font = new Font("Segoe UI Semibold", 10f)
            };
            _station.MouseDown += DragWindow;
            _meterTimer.Tick += (s, e) =>
            {
                _meter.Show(_audio.Update(_config.volume, _config.muted));
                if (_audio.Found != _windowsSound)
                {
                    _windowsSound = _audio.Found;
                    OnSoundChanged();
                }
                string station = StationName?.Invoke()?.Trim('\0', ' ') ?? "";
                if (_station.Text != station)
                    _station.Text = station;
            };
            _meterTimer.Start();
            _bar = new Panel
            {
                Dock = DockStyle.Bottom, Height = BarHeight, BackColor = Color.FromArgb(14, 14, 14),
                Padding = new Padding(0, 0, 10, 0)
            };
            _bar.Controls.Add(_station);          // added first = docked last: fills the middle
            _bar.Controls.Add(_meter);
            _bar.Controls.Add(_volumeText);
            _bar.Controls.Add(_slider);
            _bar.Controls.Add(_speaker);          // docked left in reverse order: speaker, slider, text
            _bar.MouseDown += DragWindow;
            _volumeText.MouseDown += DragWindow;

            Controls.Add(Video);
            Controls.Add(_status);
            Controls.Add(_bar);
            _bar.SendToBack();                    // docked first: the bottom strip, picture above it
            _status.BringToFront();

            _muteItem = new ToolStripMenuItem("Mute") { Checked = config.muted, CheckOnClick = true };
            _muteItem.CheckedChanged += (s, e) =>
            {
                _config.muted = _muteItem.Checked;
                OnSoundChanged();
            };
            var louder = new ToolStripMenuItem("Louder", null, (s, e) => ChangeVolume(VolumeStep));
            var quieter = new ToolStripMenuItem("Quieter", null, (s, e) => ChangeVolume(-VolumeStep));

            _onTopItem = new ToolStripMenuItem("Always on top") { Checked = config.always_on_top, CheckOnClick = true };
            _onTopItem.CheckedChanged += (s, e) =>
            {
                TopMost = _onTopItem.Checked;
                _config.always_on_top = _onTopItem.Checked;
            };

            _titleBarItem = new ToolStripMenuItem("Title bar") { Checked = config.title_bar, CheckOnClick = true };
            _titleBarItem.CheckedChanged += (s, e) =>
            {
                _config.title_bar = _titleBarItem.Checked;
                ApplyTitleBar();
            };

            _volumeItem = new ToolStripMenuItem { Enabled = false };
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { _volumeItem, new ToolStripSeparator(), _muteItem, louder, quieter,
                                                      new ToolStripSeparator(), _onTopItem, _titleBarItem });
            ContextMenuStrip = menu;
            _status.ContextMenuStrip = menu;
            Video.ContextMenuStrip = menu;
            _bar.ContextMenuStrip = menu;

            // without title bar: drag the picture to move the window
            Video.MouseDown += DragWindow;
            _status.MouseDown += DragWindow;

            ApplyTitleBar();
            UpdateTitle();
            Bounds = VisibleBounds(new Rectangle(config.window.x, config.window.y, config.window.width, config.window.height));
        }

        // null = hide the text (picture showing). Safe to call from any thread.
        public void ShowStatus(string text)
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(ShowStatus), text); }
                catch (InvalidOperationException) { }   // window closing
                return;
            }
            if (text != null)
            {
                _status.Text = text;
                _status.BringToFront();
            }
            _status.Visible = text != null;
        }

        // Keep the window proportional to the displayed picture. SAR is needed for
        // anamorphic streams such as 720x576 video displayed as 16:9.
        public void SetVideoAspectRatio(uint width, uint height, uint sarNum, uint sarDen)
        {
            if (IsDisposed || width == 0 || height == 0)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<uint, uint, uint, uint>(SetVideoAspectRatio),
                                  width, height, sarNum, sarDen); }
                catch (InvalidOperationException) { }
                return;
            }

            double pixelRatio = sarNum > 0 && sarDen > 0 ? (double)sarNum / sarDen : 1.0;
            _videoAspectRatio = width * pixelRatio / height;
        }

        // Title bar on: a normal window. Off: only the picture inside a thin black edge,
        // which resizes like a window border (WndProc, WM_NCHITTEST).
        private void ApplyTitleBar()
        {
            Rectangle bounds = Bounds;
            FormBorderStyle = _config.title_bar ? FormBorderStyle.Sizable : FormBorderStyle.None;
            Padding = _config.title_bar ? Padding.Empty : new Padding(Edge);
            Bounds = bounds;
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _config.title_bar)
                return;
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        // which edge or corner of the black frame the mouse is on (title bar off)
        private int EdgeHit(Point screenPoint)
        {
            Point p = PointToClient(screenPoint);
            bool left = p.X < Edge, right = p.X >= ClientSize.Width - Edge;
            bool top = p.Y < Edge, bottom = p.Y >= ClientSize.Height - Edge;
            if (top && left) return 13;          // HTTOPLEFT
            if (top && right) return 14;         // HTTOPRIGHT
            if (bottom && left) return 16;       // HTBOTTOMLEFT
            if (bottom && right) return 17;      // HTBOTTOMRIGHT
            if (left) return 10;                 // HTLEFT
            if (right) return 11;                // HTRIGHT
            if (top) return 12;                  // HTTOP
            if (bottom) return 15;               // HTBOTTOM
            return HtCaption;                    // anywhere else on the frame: move
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest && !_config.title_bar)
            {
                int lp = m.LParam.ToInt32();
                m.Result = (IntPtr)EdgeHit(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                return;
            }
            if (m.Msg == WmSizing && m.LParam != IntPtr.Zero)
            {
                NativeRect rect = Marshal.PtrToStructure<NativeRect>(m.LParam);
                KeepVideoAspectRatio((int)m.WParam, ref rect);
                Marshal.StructureToPtr(rect, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        private void KeepVideoAspectRatio(int edge, ref NativeRect rect)
        {
            int frameWidth = Width - ClientSize.Width + Padding.Horizontal;
            int frameHeight = Height - ClientSize.Height + Padding.Vertical + BarHeight;
            int outerWidth = rect.Right - rect.Left;
            int outerHeight = rect.Bottom - rect.Top;

            bool heightDrivesWidth = edge == WmszTop || edge == WmszBottom;
            if (heightDrivesWidth)
            {
                int wantedWidth = (int)Math.Round((outerHeight - frameHeight) * _videoAspectRatio) + frameWidth;
                if (edge == WmszTop || edge == WmszBottom)
                    rect.Right = rect.Left + wantedWidth;
            }
            else
            {
                int wantedHeight = (int)Math.Round((outerWidth - frameWidth) / _videoAspectRatio) + frameHeight;
                if (edge == WmszLeft || edge == WmszTopLeft || edge == WmszBottomLeft)
                    rect.Left = rect.Right - outerWidth;
                if (edge == WmszTopLeft || edge == WmszTopRight)
                    rect.Top = rect.Bottom - wantedHeight;
                else
                    rect.Bottom = rect.Top + wantedHeight;
            }
        }

        private void ChangeVolume(int delta)
        {
            SetVolume(_config.volume + delta);
        }

        private void SetVolume(int volume)
        {
            _config.volume = Math.Max(0, Math.Min(100, volume));
            if (_muteItem.Checked)
                _muteItem.Checked = false;      // raises OnSoundChanged
            else
                OnSoundChanged();
        }

        private void OnSoundChanged()
        {
            UpdateTitle();
            SoundChanged?.Invoke(_config.muted, _config.volume);
        }

        private void UpdateTitle()
        {
            Text = "QOCAST Player - " + (_config.muted ? "muted" : "volume " + _config.volume + "%");
            _volumeItem.Text = _config.muted ? "Muted" : "Volume " + _config.volume + " %";
            _speaker.Text = _config.muted ? "" : "";       // Segoe MDL2: Mute / Volume
            _slider.Volume = _config.volume;
            _slider.Muted = _config.muted;
            _volumeText.Text = _config.volume + " %";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _meterTimer.Stop();
            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _config.window.x = bounds.X;
            _config.window.y = bounds.Y;
            _config.window.width = bounds.Width;
            _config.window.height = bounds.Height;
            base.OnFormClosing(e);
        }

        // saved place on a monitor that is no longer there: put the window on the main screen
        private static Rectangle VisibleBounds(Rectangle wanted)
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle overlap = Rectangle.Intersect(screen.WorkingArea, wanted);
                if (overlap.Width >= 100 && overlap.Height >= 60)
                    return wanted;
            }

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int width = Math.Min(wanted.Width, area.Width);
            int height = Math.Min(wanted.Height, area.Height);
            Log.Information("Saved window place is off screen, moving it to the main screen");
            return new Rectangle(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
        }
    }
}
