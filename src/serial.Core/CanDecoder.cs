using System.Collections.Generic;
using System.Linq;

namespace serial.Core;

public sealed class CanDecoder : ProtocolDecoder
{
    private static readonly int[] CommonBitrates = [125000, 250000, 500000, 1000000];

    public string ProtocolName => "CAN";

    public ProtocolDetectionResult Detect(LogicCapture capture)
    {
        ProtocolDetectionResult best = new()
        {
            ProtocolName = ProtocolName,
            Notes = "CAN decoding expects a logic-level CAN RX/TX signal, not raw CANH/CANL."
        };

        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            int fallingEdges = CountDominantEdges(capture, channel);
            if (fallingEdges < 1)
            {
                continue;
            }

            foreach (int bitrate in CommonBitrates)
            {
                IReadOnlyList<ProtocolFrame> frames = Decode(capture, new ProtocolDecoderOptions
                {
                    CanRxChannel = channel,
                    CanBitrate = bitrate,
                    CanSamplePointPercent = 75
                });
                double confidence = System.Math.Min(1, frames.Count / 3.0);
                if (confidence > best.Confidence)
                {
                    best = new ProtocolDetectionResult
                    {
                        ProtocolName = ProtocolName,
                        Confidence = confidence,
                        SuggestedBaudRate = bitrate,
                        SuggestedChannels = new Dictionary<string, int> { ["RX"] = channel },
                        Notes = "Logic-level CAN only. CRC validation is TODO in MVP."
                    };
                }
            }
        }

        return best;
    }

    public IReadOnlyList<ProtocolFrame> Decode(LogicCapture capture, ProtocolDecoderOptions options)
    {
        int channel = System.Math.Clamp(options.CanRxChannel, 0, capture.ChannelCount - 1);
        int bitSamples = System.Math.Max(1, (int)System.Math.Round((double)capture.SampleRateHz / System.Math.Max(1, options.CanBitrate)));
        int samplePointOffset = System.Math.Max(1, bitSamples * System.Math.Clamp(options.CanSamplePointPercent, 50, 90) / 100);
        List<ProtocolFrame> frames = [];

        for (int sample = 1; sample + bitSamples * 64 < capture.SampleCount; sample++)
        {
            bool previous = capture.GetChannelState(sample - 1, channel);
            bool current = capture.GetChannelState(sample, channel);
            if (!previous || current)
            {
                continue;
            }

            List<int> bits = [];
            for (int bit = 0; bit < 96 && sample + bit * bitSamples + samplePointOffset < capture.SampleCount; bit++)
            {
                bool recessive = capture.GetChannelState(sample + bit * bitSamples + samplePointOffset, channel);
                bits.Add(recessive ? 1 : 0);
            }

            if (bits.Count < 44 || bits[0] != 0)
            {
                continue;
            }

            int id = ReadBits(bits, 1, 11);
            int rtr = bits[12];
            int ide = bits[13];
            int dlc = ReadBits(bits, 15, 4);
            dlc = System.Math.Clamp(dlc, 0, 8);
            List<byte> data = [];
            int dataStart = 19;
            for (int i = 0; i < dlc && dataStart + i * 8 + 7 < bits.Count; i++)
            {
                data.Add((byte)ReadBits(bits, dataStart + i * 8, 8));
            }

            int end = sample + (dataStart + dlc * 8 + 15) * bitSamples;
            frames.Add(new ProtocolFrame
            {
                Protocol = ProtocolName,
                StartSampleIndex = sample,
                EndSampleIndex = System.Math.Min(end, capture.SampleCount - 1),
                StartTimeSeconds = (double)sample / capture.SampleRateHz,
                EndTimeSeconds = (double)System.Math.Min(end, capture.SampleCount - 1) / capture.SampleRateHz,
                DecodedText = $"CAN ID=0x{id:X3} DLC={dlc} DATA={string.Join(" ", data.Select(value => $"{value:X2}"))}",
                RawData = data.ToArray(),
                HasError = ide != 0,
                ErrorMessage = ide != 0 ? "Extended CAN frames are not decoded in MVP. CRC validation TODO." : "CRC validation TODO.",
                Channels = [channel]
            });

            sample += System.Math.Max(bitSamples * 16, 1);
        }

        return frames;
    }

    private static int ReadBits(List<int> bits, int offset, int count)
    {
        int value = 0;
        for (int i = 0; i < count && offset + i < bits.Count; i++)
        {
            value = (value << 1) | bits[offset + i];
        }

        return value;
    }

    private static int CountDominantEdges(LogicCapture capture, int channel)
    {
        int count = 0;
        for (int i = 1; i < capture.SampleCount; i++)
        {
            if (capture.GetChannelState(i - 1, channel) && !capture.GetChannelState(i, channel))
            {
                count++;
            }
        }

        return count;
    }
}
