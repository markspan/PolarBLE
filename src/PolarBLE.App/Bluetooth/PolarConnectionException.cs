namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Raised when the connection sequence fails in a way the user needs to know about. The
/// message is written for the user and is shown verbatim.
/// </summary>
internal sealed class PolarConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);
