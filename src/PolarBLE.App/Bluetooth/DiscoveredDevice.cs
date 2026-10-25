using System.Globalization;

namespace PolarBLE.App.Bluetooth;

/// <summary>A Polar strap seen in a Bluetooth LE advertisement.</summary>
/// <param name="Address">The Bluetooth address, which is the stable identity of the strap.</param>
/// <param name="Name">The advertised local name.</param>
internal sealed record DiscoveredDevice(ulong Address, string Name)
{
    /// <summary>The address formatted the way it is shown to users and used in stream ids.</summary>
    public string AddressText => Address.ToString("X12", CultureInfo.InvariantCulture);

    /// <summary>
    /// What the list box displays. The address travels with the object, so it never has to be
    /// parsed back out of this text.
    /// </summary>
    public override string ToString() => $"{Name} [{AddressText}]";
}
