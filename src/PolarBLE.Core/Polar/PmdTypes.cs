namespace PolarBLE.Core.Polar;

/// <summary>Measurement types addressable through the PMD control point.</summary>
public enum PmdMeasurementType : byte
{
    /// <summary>Electrocardiogram, 3-byte signed samples in microvolts.</summary>
    Ecg = 0x00,

    /// <summary>Photoplethysmogram (not present on the H10).</summary>
    Ppg = 0x01,

    /// <summary>Accelerometer, three signed axes in milli-g.</summary>
    Acc = 0x02,

    /// <summary>Peak-to-peak interval (not streamed by this application).</summary>
    Ppi = 0x03,

    /// <summary>Gyroscope (not present on the H10).</summary>
    Gyroscope = 0x05,

    /// <summary>Magnetometer (not present on the H10).</summary>
    Magnetometer = 0x06,
}

/// <summary>Setting identifiers used in the control-point type/length/value triples.</summary>
public enum PmdSettingType : byte
{
    /// <summary>Sample rate in hertz.</summary>
    SampleRate = 0x00,

    /// <summary>Resolution in bits.</summary>
    Resolution = 0x01,

    /// <summary>Measurement range; for the accelerometer, in g.</summary>
    Range = 0x02,

    /// <summary>Number of channels.</summary>
    ChannelCount = 0x04,
}

/// <summary>Control-point operation codes.</summary>
public enum PmdOpCode : byte
{
    /// <summary>Ask the sensor which settings a measurement supports.</summary>
    GetMeasurementSettings = 0x01,

    /// <summary>Begin streaming a measurement.</summary>
    StartMeasurement = 0x02,

    /// <summary>Stop streaming a measurement.</summary>
    StopMeasurement = 0x03,
}

/// <summary>
/// Error codes returned in the fourth byte of a PMD control-point response.
/// </summary>
public enum PmdError : byte
{
    /// <summary>The request was accepted.</summary>
    Success = 0x00,

    /// <summary>The operation code is not recognised.</summary>
    InvalidOpCode = 0x01,

    /// <summary>The measurement type is not recognised.</summary>
    InvalidMeasurementType = 0x02,

    /// <summary>The sensor does not support this measurement.</summary>
    NotSupported = 0x03,

    /// <summary>One of the requested settings has an invalid length.</summary>
    InvalidLength = 0x04,

    /// <summary>One of the requested settings has an invalid parameter.</summary>
    InvalidParameter = 0x05,

    /// <summary>The measurement is already in the requested state.</summary>
    AlreadyInState = 0x06,

    /// <summary>The requested resolution is invalid.</summary>
    InvalidResolution = 0x07,

    /// <summary>The requested sample rate is invalid.</summary>
    InvalidSampleRate = 0x08,

    /// <summary>The requested range is invalid.</summary>
    InvalidRange = 0x09,

    /// <summary>The requested MTU is invalid.</summary>
    InvalidMtu = 0x0A,

    /// <summary>The requested number of channels is invalid.</summary>
    InvalidNumberOfChannels = 0x0B,

    /// <summary>The sensor is in an invalid state for this request.</summary>
    InvalidState = 0x0C,

    /// <summary>The sensor is charging and cannot measure.</summary>
    DeviceInCharger = 0x0D,
}

/// <summary>A single control-point setting: a type paired with a 16-bit value.</summary>
/// <param name="Type">Which setting is being specified.</param>
/// <param name="Value">The value, encoded little-endian on the wire.</param>
public readonly record struct PmdSetting(PmdSettingType Type, ushort Value);

/// <summary>A decoded PMD control-point response.</summary>
/// <param name="OpCode">The operation the sensor is responding to.</param>
/// <param name="MeasurementType">The measurement the response concerns.</param>
/// <param name="Error">The outcome; <see cref="PmdError.Success"/> means accepted.</param>
public readonly record struct PmdControlPointResponse(
    PmdOpCode OpCode,
    PmdMeasurementType MeasurementType,
    PmdError Error)
{
    /// <summary>True when the sensor accepted the request.</summary>
    public bool IsSuccess => Error == PmdError.Success;

    /// <summary>
    /// Whether this response answers <paramref name="request"/>, judged by the operation code
    /// and measurement type that every request starts with.
    /// </summary>
    /// <remarks>
    /// Matching guards against attributing a late or unsolicited indication, for example the
    /// answer to a previous request that already timed out, to the request now in flight.
    /// </remarks>
    /// <param name="request">The request bytes as written to the control point.</param>
    /// <returns>True if the response belongs to the request.</returns>
    public bool IsResponseTo(ReadOnlySpan<byte> request) =>
        request.Length >= 2 &&
        (byte)OpCode == request[0] &&
        (byte)MeasurementType == request[1];
}
