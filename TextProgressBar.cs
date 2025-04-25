using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PolarBLE
{
    public class TextProgressBar : Panel
    {
        private int _value = 0;
        private int _maximum = 100;

        public int Value
        {
            get => _value;
            set
            {
                _value = Math.Min(_maximum, Math.Max(0, value));
                this.Invalidate(); // Redraw
            }
        }

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(1, value);
                this.Invalidate();
            }
        }

        public TextProgressBar()
        {
            this.DoubleBuffered = true;
            this.ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            float percent = (float)_value / _maximum;
            int fillWidth = (int)(this.Width * percent);

            // Draw progress bar
            if (percent < .10)
            {
                using (Brush progressBrush = new SolidBrush(Color.Red))
                {
                    e.Graphics.FillRectangle(progressBrush, 0, 0, fillWidth, this.Height);
                }
            }
            else
            {
                using (Brush progressBrush = new SolidBrush(Color.Green))
                {
                    e.Graphics.FillRectangle(progressBrush, 0, 0, fillWidth, this.Height);
                }
            }


                // Draw text
                string text = $"{_value}%";
            SizeF textSize = e.Graphics.MeasureString(text, this.Font);
            PointF textPos = new PointF(
                (this.Width - textSize.Width) / 2,
                (this.Height - textSize.Height) / 2
            );

            e.Graphics.DrawString(text, this.Font, Brushes.White, textPos);
        }
    }
}
