using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using PolarBLE.Core.Polar;

namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Request/response access to the PMD control point.
/// </summary>
/// <remarks>
/// <para>
/// A request is a write; its response arrives later as an indication. The control point
/// services one request at a time, so requests are serialised here rather than relying on
/// every caller to await the previous one, and each indication is matched to the request it
/// answers, so a late answer to an abandoned request can never be mistaken for the current one.
/// </para>
/// <para>
/// Interpreting the response is left to the caller, which knows what a refusal means.
/// </para>
/// </remarks>
internal sealed class PmdControlPointChannel(GattCharacteristic characteristic)
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private readonly GattCharacteristic _characteristic = characteristic;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Written by the requesting thread, read by the indication callback thread.
    private volatile PendingRequest? _pending;

    /// <summary>Subscribes to the indications that carry responses.</summary>
    /// <exception cref="PolarConnectionException">The strap refused the subscription.</exception>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        _characteristic.ValueChanged += OnIndication;
        await _characteristic.SubscribeAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Indicate,
            "PMD control point",
            cancellationToken);
    }

    /// <summary>Writes a request and waits for the strap's response to it.</summary>
    /// <param name="request">The request bytes.</param>
    /// <param name="what">What the request does, for error messages, e.g. "ECG start".</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The strap's response, which may be a refusal.</returns>
    /// <exception cref="PolarConnectionException">The write failed or went unanswered.</exception>
    public async Task<PmdControlPointResponse> SendAsync(
        byte[] request,
        string what,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var pending = new PendingRequest(request);
        _pending = pending;

        try
        {
            var status = await _characteristic
                .WriteValueAsync(CryptographicBuffer.CreateFromByteArray(request))
                .AsTask(cancellationToken);

            if (status != GattCommunicationStatus.Success)
            {
                throw new PolarConnectionException($"Writing the {what} command failed ({status}).");
            }

            try
            {
                return await pending.Response.WaitAsync(ResponseTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new PolarConnectionException(
                    $"The strap did not acknowledge the {what} command within " +
                    $"{ResponseTimeout.TotalSeconds:0} seconds.");
            }
        }
        finally
        {
            _pending = null;
            _gate.Release();
        }
    }

    /// <summary>Writes a request without waiting for a response. Never throws.</summary>
    public Task TrySendAsync(byte[] request) => _characteristic.TryWriteAsync(request);

    /// <summary>Stops listening for responses and, when still possible, unsubscribes. Never throws.</summary>
    /// <param name="deviceConnected">False skips the radio traffic that can no longer succeed.</param>
    public async Task CloseAsync(bool deviceConnected)
    {
        _characteristic.ValueChanged -= OnIndication;
        if (deviceConnected)
        {
            await _characteristic.TryUnsubscribeAsync();
        }
    }

    private void OnIndication(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var pending = _pending;
        if (pending is not null &&
            PmdControlPoint.TryParseResponse(args.CharacteristicValue.ToByteArray(), out var response) &&
            response.IsResponseTo(pending.Request))
        {
            pending.Complete(response);
        }
    }

    /// <summary>A request in flight and the completion its response will resolve.</summary>
    private sealed class PendingRequest(byte[] request)
    {
        private readonly TaskCompletionSource<PmdControlPointResponse> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Request { get; } = request;

        public Task<PmdControlPointResponse> Response => _completion.Task;

        public void Complete(PmdControlPointResponse response) => _completion.TrySetResult(response);
    }
}
