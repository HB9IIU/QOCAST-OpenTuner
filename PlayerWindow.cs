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

        private readonly PlayerConfig _config;
        private readonly Label _status;
        private readonly ToolStripMenuItem _muteItem;
        private readonly ToolStripMenuItem _onTopItem;
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
            Controls.Add(Video);
            Controls.Add(_status);
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

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { _muteItem, louder, quieter, new ToolStripSeparator(), _onTopItem });
            ContextMenuStrip = menu;
            _status.ContextMenuStrip = menu;
            Video.ContextMenuStrip = menu;

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

        protected override void WndProc(ref Message m)
        {
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
            int frameWidth = Width - ClientSize.Width;
            int frameHeight = Height - ClientSize.Height;
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
            _config.volume = Math.Max(0, Math.Min(100, _config.volume + delta));
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
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
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
