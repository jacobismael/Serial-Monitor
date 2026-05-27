using System.Collections.Generic;
using System.Linq;

namespace serial.Core;

public sealed class I2cDecoder : ProtocolDecoder
{
    public string ProtocolName => "I2C";

    public ProtocolDetectionResult Detect(LogicCapture capture)
    {
        ProtocolDetectionResult best = new()
        {
            ProtocolName = ProtocolName,
            Notes = "No I2C-like pair detected."
        };

        for (int scl = 0; scl < capture.ChannelCount; scl++)
        {
            int clockEdges = CountRisingEdges(capture, scl);
            if (clockEdges < 8)
            {
                continue;
            }

            for (int sda = 0; sda < capture.ChannelCount; sda++)
            {
                if (sda == scl)
                {
                    continue;
                }

                int starts = CountStartConditions(capture, sda, scl);
                int stops = CountStopConditions(capture, sda, scl);
                double confidence = (starts > 0 && stops > 0) ? 0.75 : 0.25;
                confidence += System.Math.Min(0.2, clockEdges / 200.0);
                if (confidence > best.Confidence)
                {
                    best = new ProtocolDetectionResult
                    {
                        ProtocolName = ProtocolName,
                        Confidence = confidence,
                        SuggestedChannels = new Dictionary<string, int> { ["SDA"] = sda, ["SCL"] = scl },
                        Notes = $"START={starts}, STOP={stops}, SCL edges={clockEdges}."
                    };
                }
            }
        }

        return best;
    }

    public IReadOnlyList<ProtocolFrame> Decode(LogicCapture capture, ProtocolDecoderOptions options)
    {
        int sda = System.Math.Clamp(options.SdaChannel, 0, capture.ChannelCount - 1);
        int scl = System.Math.Clamp(options.SclChannel, 0, capture.ChannelCount - 1);
        List<ProtocolFrame> frames = [];
        List<int> bits = [];
        int transactionStart = -1;

        for (int sample = 1; sample < capture.SampleCount; sample++)
        {
            bool previousSda = capture.GetChannelState(sample - 1, sda);
            bool currentSda = capture.GetChannelState(sample, sda);
            bool currentScl = capture.GetChannelState(sample, scl);

            if (previousSda && !currentSda && currentScl)
            {
                bits.Clear();
                transactionStart = sample;
                continue;
            }

            if (!previousSda && currentSda && currentScl && transactionStart >= 0)
            {
                AddTransactionFrame(capture, frames, bits, transactionStart, sample, sda, scl);
                bits.Clear();
                transactionStart = -1;
                continue;
            }

            bool previousScl = capture.GetChannelState(sample - 1, scl);
            if (!previousScl && currentScl && transactionStart >= 0)
            {
                bits.Add(currentSda ? 1 : 0);
            }
        }

        return frames;
    }

    private void AddTransactionFrame(
        LogicCapture capture,
        List<ProtocolFrame> frames,
        List<int> bits,
        int start,
        int end,
        int sda,
        int scl)
    {
        if (bits.Count < 9)
        {
            return;
        }

        List<byte> bytes = [];
        List<bool> acks = [];
        for (int offset = 0; offset + 8 < bits.Count; offset += 9)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value << 1) | bits[offset + bit];
            }

            bytes.Add((byte)value);
            acks.Add(bits[offset + 8] == 0);
        }

        if (bytes.Count == 0)
        {
            return;
        }

        int address = bytes[0] >> 1;
        string direction = (bytes[0] & 0x01) == 0 ? "WRITE" : "READ";
        string data = string.Join(" ", bytes.Skip(1).Select(value => $"0x{value:X2}"));
        string ackText = acks.All(ack => ack) ? "ACK" : "NACK";

        frames.Add(new ProtocolFrame
        {
            Protocol = ProtocolName,
            StartSampleIndex = start,
            EndSampleIndex = end,
            StartTimeSeconds = (double)start / capture.SampleRateHz,
            EndTimeSeconds = (double)end / capture.SampleRateHz,
            DecodedText = $"I2C {direction} addr=0x{address:X2} data=[{data}] {ackText}",
            RawData = bytes.ToArray(),
            Channels = [sda, scl],
            HasError = !acks.All(ack => ack),
            ErrorMessage = acks.All(ack => ack) ? "" : "One or more NACK bits"
        });
    }

    private static int CountRisingEdges(LogicCapture capture, int channel)
    {
        int edges = 0;
        for (int i = 1; i < capture.SampleCount; i++)
        {
            if (!capture.GetChannelState(i - 1, channel) && capture.GetChannelState(i, channel))
            {
                edges++;
            }
        }

        return edges;
    }

    private static int CountStartConditions(LogicCapture capture, int sda, int scl)
    {
        int count = 0;
        for (int i = 1; i < capture.SampleCount; i++)
        {
            if (capture.GetChannelState(i - 1, sda) && !capture.GetChannelState(i, sda) && capture.GetChannelState(i, scl))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountStopConditions(LogicCapture capture, int sda, int scl)
    {
        int count = 0;
        for (int i = 1; i < capture.SampleCount; i++)
        {
            if (!capture.GetChannelState(i - 1, sda) && capture.GetChannelState(i, sda) && capture.GetChannelState(i, scl))
            {
                count++;
            }
        }

        return count;
    }
}
