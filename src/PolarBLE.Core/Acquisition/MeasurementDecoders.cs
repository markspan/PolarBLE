using PolarBLE.Core.Polar;

namespace PolarBLE.Core.Acquisition;

/// <summary>
/// The decoders for the measurements a Polar H10 streams. Each is a thin adapter that exposes
/// one of the static PMD codecs through <see cref="IMeasurementDecoder"/>.
/// </summary>
public static class MeasurementDecoders
{
    /// <summary>ECG: one channel in microvolts.</summary>
    public static IMeasurementDecoder Ecg { get; } = new EcgDecoder();

    /// <summary>Accelerometer: three interleaved axes in milli-g.</summary>
    public static IMeasurementDecoder Acc { get; } = new AccDecoder();

    private sealed class EcgDecoder : IMeasurementDecoder
    {
        public PmdMeasurementType MeasurementType => PmdMeasurementType.Ecg;

        public int ChannelCount => EcgFrameDecoder.Channels;

        public bool IsSupported(byte frameType) => EcgFrameDecoder.IsSupported(frameType);

        public int SampleCount(byte frameType, ReadOnlySpan<byte> payload) =>
            EcgFrameDecoder.SampleCount(payload);

        public int Decode(byte frameType, ReadOnlySpan<byte> payload, Span<float> destination) =>
            EcgFrameDecoder.Decode(payload, destination);
    }

    private sealed class AccDecoder : IMeasurementDecoder
    {
        public PmdMeasurementType MeasurementType => PmdMeasurementType.Acc;

        public int ChannelCount => AccFrameDecoder.Axes;

        public bool IsSupported(byte frameType) => AccFrameDecoder.IsSupported(frameType);

        public int SampleCount(byte frameType, ReadOnlySpan<byte> payload) =>
            AccFrameDecoder.SampleCount(frameType, payload);

        public int Decode(byte frameType, ReadOnlySpan<byte> payload, Span<float> destination) =>
            AccFrameDecoder.Decode(frameType, payload, destination);
    }
}
