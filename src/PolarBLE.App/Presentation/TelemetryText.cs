using System.Globalization;
using System.Text;
using PolarBLE.Core.Acquisition;

namespace PolarBLE.App.Presentation;

/// <summary>
/// Formats the live per-stream telemetry shown under the device list.
/// </summary>
/// <remarks>
/// Every field has a fixed width and the label is meant for a monospace font, so the rows
/// never shift sideways as a sample count picks up another digit during a recording.
/// </remarks>
internal static class TelemetryText
{
    /// <summary>How long a stream may go without a frame before it is reported as stalled.</summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);

    /// <summary>Formats one row per stream.</summary>
    /// <param name="streams">The streams to describe.</param>
    /// <param name="hasProblems">True if any stream lost data or stalled.</param>
    /// <returns>The text, one line per stream.</returns>
    public static string Format(IEnumerable<MeasurementStream> streams, out bool hasProblems)
    {
        hasProblems = false;
        var text = new StringBuilder();

        foreach (var stream in streams)
        {
            var stats = stream.Statistics.Snapshot();
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(CultureInfo.CurrentCulture, $"{stream.Label,-3} {stats.Samples,12:N0} samples");

            if (stats.Gaps > 0)
            {
                text.Append(CultureInfo.CurrentCulture, $"  {stats.Gaps:N0} gaps, {stats.SamplesLost:N0} lost");
            }

            if (stats.UnsupportedFrames > 0)
            {
                text.Append(CultureInfo.CurrentCulture, $"  {stats.UnsupportedFrames:N0} unreadable");
            }

            var stalled = stats.IsStalled(StallThreshold);
            if (stalled)
            {
                text.Append(CultureInfo.CurrentCulture, $"  idle {stats.IdleTime.TotalSeconds:0} s");
            }

            hasProblems |= stats.HasDataLoss || stalled;
        }

        return text.ToString();
    }
}
