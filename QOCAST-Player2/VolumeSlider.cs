using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QocastPlayer
{
    // A thin volume bar for the black strip under the picture: click or drag to set 0-100 %.
    public sealed class VolumeSlider : System.Windows.Forms.Control
    {
        private const int Inset = 8;           // room for the knob at both ends
        private int _volume;
        private bool _muted;

        // the user chose a volume (0-100)
        public event Action<int> VolumeChosen;

        public VolumeSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(14, 14, 14);
            Cursor = Cursors.Hand;
        }

        public int Volume
        {
            get { return _volume; }
            set { _volume = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public bool Muted
        {
            get { return _muted; }
            set { _muted = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int y = Height / 2;
            int left = Inset, right = Width - Inset;
            int x = left + (right - left) * _volume / 100;
            Color fill = _muted ? Color.DimGray : Color.Gainsboro;
            using (var track = new Pen(Color.FromArgb(70, 70, 70), 3))
            using (var level = new Pen(fill, 3))
            using (var knob = new SolidBrush(fill))
            {
                track.StartCap = track.EndCap = level.StartCap = level.EndCap = LineCap.Round;
                e.Graphics.DrawLine(track, left, y, right, y);
                if (x > left)
                    e.Graphics.DrawLine(level, left, y, x, y);
                e.Graphics.FillEllipse(knob, x - 5, y - 5, 10, 10);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
                Choose(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left)
                Choose(e.X);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            VolumeChosen?.Invoke(_volume + (e.Delta > 0 ? 5 : -5));
        }

        private void Choose(int mouseX)
        {
            int span = Math.Max(1, Width - 2 * Inset);
            int volume = (int)Math.Round(100.0 * (mouseX - Inset) / span);
            volume = Math.Max(0, Math.Min(100, volume));
            if (volume != _volume)
                VolumeChosen?.Invoke(volume);
        }
    }
}
