using System.ComponentModel;

namespace PolarBLE.App.Controls;

/// <summary>
/// A small battery gauge.
/// </summary>
/// <remarks>
/// Replaces the original TextProgressBar, which painted no background (leaving the unfilled
/// region unpainted), ignored its own Maximum when formatting the caption, allocated brushes on
/// every paint, and had no way to represent "not known yet".
/// </remarks>
internal sealed class BatteryBar : Control
{
    private int _value;
    private int _maximum = 100;

    public BatteryBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw,
            true);

        TabStop = false;
    }

    /// <summary>Current level. Negative means "not known yet".</summary>
    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = value < 0 ? -1 : Math.Min(value, _maximum);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            AccessibleDescription = HasValue ? $"Battery {Percentage} percent" : "Battery level unknown";
            Invalidate();
        }
    }

    /// <summary>Full-scale value.</summary>
    [DefaultValue(100)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            var clamped = Math.Max(1, value);
            if (clamped == _maximum)
            {
                return;
            }

            _maximum = clamped;
            Invalidate();
        }
    }

    /// <summary>True once a real reading has arrived.</summary>
    public bool HasValue => _value >= 0;

    private int Percentage => (int)Math.Round(_value * 100.0 / _maximum);

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPaint(e);

        var graphics = e.Graphics;
        var bounds = ClientRectangle;

        using (var background = new SolidBrush(SystemColors.ControlDark))
        {
            graphics.FillRectangle(background, bounds);
        }

        if (HasValue)
        {
            var fillWidth = (int)(bounds.Width * (_value / (double)_maximum));
            if (fillWidth > 0)
            {
                // Amber below a quarter, red below a tenth: a flat colour tells the wearer
                // nothing until it is too late to swap the strap.
                var colour = Percentage switch
                {
                    < 10 => Color.Firebrick,
                    < 25 => Color.DarkGoldenrod,
                    _ => Color.SeaGreen,
                };

                using var fill = new SolidBrush(colour);
                graphics.FillRectangle(fill, 0, 0, fillWidth, bounds.Height);
            }
        }

        var caption = HasValue ? $"{Percentage}%" : "—";
        TextRenderer.DrawText(
            graphics,
            caption,
            Font,
            bounds,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        using var border = new Pen(SystemColors.ControlDarkDark);
        graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }
}
