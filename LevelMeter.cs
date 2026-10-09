using System;
using System.Drawing;
using System.Windows.Forms;

namespace QocastPlayer
{
    // VU meter for the black strip: green / yellow / red segments from -48 dB to 0 dB,
    // falls back slowly like a real meter and holds the peak for a moment.
    public sealed class LevelMeter : System.Windows.Forms.Control
    {
        private const float FloorDb = -48f;
        private const int Segments = 24;
        private const float FallPerTick = 1.5f;     // dB per update (50 ms)
        private const int HoldTicks = 20;           // peak mark stays 1 s

        private float _levelDb = FloorDb;
        private float _holdDb = FloorDb;
        private int _holdLeft;

        public LevelMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(14, 14, 14);
        }

        // new peak 0..1, called every 50 ms
        public void Show(float peak)
        {
            float db = peak > 0f ? Math.Max(FloorDb, 20f * (float)Math.Log10(peak)) : FloorDb;
            _levelDb = Math.Max(db, _levelDb - FallPerTick);
            if (db >= _holdDb || --_holdLeft <= 0)
            {
                _holdDb = db;
                _holdLeft = HoldTicks;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int top = Height / 2 - 4, height = 8;
            float step = (float)Width / Segments;
            int lit = Lit(_levelDb), hold = Lit(_holdDb) - 1;
            for (int i = 0; i < Segments; i++)
            {
                float db = FloorDb * (1f - (i + 1f) / Segments);
                Color on = db > -3f ? Color.Red : db > -12f ? Color.Gold : Color.LimeGreen;
                Color color = i < lit || i == hold && hold > 0 ? on : Color.FromArgb(40, 40, 40);
                using (var brush = new SolidBrush(color))
                    e.Graphics.FillRectangle(brush, i * step, top, step - 1.5f, height);
            }
        }

        private static int Lit(float db)
        {
            return (int)Math.Round(Segments * (db - FloorDb) / -FloorDb);
        }
    }
}
