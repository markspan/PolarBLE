using System.Buffers.Binary;

namespace PolarBLE.Core.Polar;

/// <summary>
/// The ten-byte header that opens every PMD data frame.
/// </summary>
/// <param name="MeasurementType">Which measurement this frame carries.</param>
/// <param name="TimestampNanoseconds">
/// Device timestamp in nanoseconds since the Polar epoch (2000-01-01). It refers to the last
/// sample in the frame.
/// </param>
/// <param name="FrameType">
/// Encoding of the payload. Its meaning is measurement-specific: for ECG, 0x00 is uncompressed;
/// for the accelerometer, 0x01 is uncompressed and 0x02 is delta-compressed.
/// </param>
public readonly record struct PmdFrameHeader(
    PmdMeasurementType MeasurementType,
    ulong TimestampNanoseconds,
    byte FrameType)
{
    /// <summary>Length of the header in bytes.</summary>
    public const int Length = 10;

    /// <summary>The epoch that <see cref="TimestampNanoseconds"/> counts from.</summary>
    public static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The device timestamp expressed as UTC.</summary>
    public DateTime TimestampUtc => Epoch.AddTicks((long)(TimestampNanoseconds / 100UL));

    /// <summary>
    /// Attempts to split a raw notification into its header and payload.
    /// </summary>
    /// <param name="frame">The raw notification bytes.</param>
    /// <param name="header">The decoded header when this returns true.</param>
    /// <param name="payload">The remaining bytes when this returns true.</param>
    /// <returns>False if the notification is too short to be a PMD frame.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> frame,
        out PmdFrameHeader header,
        out ReadOnlySpan<byte> payload)
    {
        if (frame.Length < Length)
        {
            header = default;
            payload = default;
            return false;
        }

        header = new PmdFrameHeader(
            (PmdMeasurementType)frame[0],
            BinaryPrimitives.ReadUInt64LittleEndian(frame[1..9]),
            frame[9]);
        payload = frame[Length..];
        return true;
    }
}
