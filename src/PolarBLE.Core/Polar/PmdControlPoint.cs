using System.Buffers.Binary;

namespace PolarBLE.Core.Polar;

/// <summary>
/// Builds PMD control-point requests and decodes their responses.
/// </summary>
/// <remarks>
/// <para>
/// A start-measurement request is <c>0x02 &lt;measurementType&gt;</c> followed by one
/// type/length/value triple per setting: the setting id, an array length (the number of
/// 16-bit values that follow, always 1 here), then the value little-endian.
/// </para>
/// <para>
/// A response arrives as an indication on the control point and is
/// <c>[0xF0][opCode][measurementType][errorCode][...]</c>. The original implementation never
/// enabled those indications and never checked the error code, so a rejected configuration
/// was indistinguishable from a healthy stream that had not started yet.
/// </para>
/// </remarks>
public static class PmdControlPoint
{
    /// <summary>First byte of every control-point response frame.</summary>
    public const byte ResponseCode = 0xF0;

    /// <summary>Minimum length of a well-formed control-point response.</summary>
    public const int ResponseLength = 4;

    /// <summary>
    /// Builds a start-measurement request.
    /// </summary>
    /// <param name="measurementType">The measurement to start.</param>
    /// <param name="settings">The settings to request, in the order they should be sent.</param>
    /// <returns>The bytes to write to <see cref="PolarUuids.PmdControlPoint"/>.</returns>
    public static byte[] BuildStart(PmdMeasurementType measurementType, params PmdSetting[] settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var request = new byte[2 + (settings.Length * 4)];
        request[0] = (byte)PmdOpCode.StartMeasurement;
        request[1] = (byte)measurementType;

        var offset = 2;
        foreach (var setting in settings)
        {
            request[offset] = (byte)setting.Type;
            request[offset + 1] = 0x01; // one 16-bit value follows
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(offset + 2), setting.Value);
            offset += 4;
        }

        return request;
    }

    /// <summary>
    /// Builds a stop-measurement request. The original implementation never sent one, so the
    /// strap kept streaming (and draining its battery) after the application exited.
    /// </summary>
    /// <param name="measurementType">The measurement to stop.</param>
    /// <returns>The bytes to write to <see cref="PolarUuids.PmdControlPoint"/>.</returns>
    public static byte[] BuildStop(PmdMeasurementType measurementType) =>
        [(byte)PmdOpCode.StopMeasurement, (byte)measurementType];

    /// <summary>
    /// Attempts to decode a control-point response indication.
    /// </summary>
    /// <param name="frame">The raw indication payload.</param>
    /// <param name="response">The decoded response when this returns true.</param>
    /// <returns>True if <paramref name="frame"/> is a well-formed response frame.</returns>
    public static bool TryParseResponse(ReadOnlySpan<byte> frame, out PmdControlPointResponse response)
    {
        if (frame.Length < ResponseLength || frame[0] != ResponseCode)
        {
            response = default;
            return false;
        }

        response = new PmdControlPointResponse(
            (PmdOpCode)frame[1],
            (PmdMeasurementType)frame[2],
            (PmdError)frame[3]);
        return true;
    }
}
