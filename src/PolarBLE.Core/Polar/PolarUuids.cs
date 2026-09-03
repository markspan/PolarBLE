namespace PolarBLE.Core.Polar;

/// <summary>
/// GATT identifiers used to talk to a Polar H10.
/// </summary>
/// <remarks>
/// The PMD (Polar Measurement Data) service is proprietary; the battery and heart-rate
/// services are the standard Bluetooth SIG ones.
/// </remarks>
public static class PolarUuids
{
    /// <summary>Polar Measurement Data service.</summary>
    public static readonly Guid PmdService = new("FB005C80-02E7-F387-1CAD-8ACD2D8DF0C8");

    /// <summary>PMD control point: write requests here, receive responses as indications.</summary>
    public static readonly Guid PmdControlPoint = new("FB005C81-02E7-F387-1CAD-8ACD2D8DF0C8");

    /// <summary>PMD data characteristic: measurement frames arrive here as notifications.</summary>
    public static readonly Guid PmdData = new("FB005C82-02E7-F387-1CAD-8ACD2D8DF0C8");

    /// <summary>Standard Bluetooth SIG battery service.</summary>
    public static readonly Guid BatteryService = new("0000180F-0000-1000-8000-00805F9B34FB");

    /// <summary>Standard Bluetooth SIG battery level characteristic (percent, one byte).</summary>
    public static readonly Guid BatteryLevel = new("00002A19-0000-1000-8000-00805F9B34FB");
}
