using System.ComponentModel;

namespace PolarBLE.App.Controls;

/// <summary>
/// A small battery gauge that can also show "not known yet".
/// </summary>
/// <remarks>
/// The fill turns amber below a quarter and red below a tenth: a flat colour tells the wearer
/// nothing until it is too late to swap the strap.
/// </remarks>
internal sealed class BatteryBar : Control
{
    private const int Unknown = -1;

    private int _value = Unknown;
    private int _maximum = 100;

    public BatteryBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw,
            true);

        TabStop = false;
        AccessibleDescription = DescribeValue();
    }

    /// <summary>Current level. Negative means "not known yet".</summary>
    [DefaultValue(Unknown)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = value < 0 ? Unknown : Math.Min(value, _maximum);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            AccessibleDescription = DescribeValue();
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

    private string DescribeValue() =>
        HasValue ? $"Battery {Percentage} percent" : "Battery level unknown";

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

        var caption = HasValue ? $"{Percentage}%" : "--";
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
