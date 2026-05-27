using System.Collections.Generic;
using System.Linq;

namespace serial.Core;

public sealed class SpiDecoder : ProtocolDecoder
{
    public string ProtocolName => "SPI";

    public ProtocolDetectionResult Detect(LogicCapture capture)
    {
        int bestChannel = 0;
        int bestEdges = 0;
        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            int edges = CountEdges(capture, channel);
            if (edges > bestEdges)
            {
                bestEdges = edges;
                bestChannel = channel;
            }
        }

        return new ProtocolDetectionResult
        {
            ProtocolName = ProtocolName,
            Confidence = bestEdges > 16 ? 0.65 : 0.1,
            SuggestedChannels = new Dictionary<string, int>
            {
                ["SCLK"] = bestChannel,
                ["MOSI"] = System.Math.Min(capture.ChannelCount - 1, bestChannel + 1)
            },
            Notes = $"Clock-like edges={bestEdges}."
        };
    }

    public IReadOnlyList<ProtocolFrame> Decode(LogicCapture capture, ProtocolDecoderOptions options)
    {
        int sclk = System.Math.Clamp(options.SclkChannel, 0, capture.ChannelCount - 1);
        int mosi = System.Math.Clamp(options.MosiChannel, 0, capture.ChannelCount - 1);
        int? miso = options.MisoChannel >= 0 && options.MisoChannel < capture.ChannelCount ? options.MisoChannel : null;
        int? cs = options.CsChannel >= 0 && options.CsChannel < capture.ChannelCount ? options.CsChannel : null;
        List<ProtocolFrame> frames = [];
        List<byte> mosiBytes = [];
        List<byte> misoBytes = [];
        int wordSize = System.Math.Clamp(options.WordSize, 1, 32);
        int bitsInWord = 0;
        int mosiValue = 0;
        int misoValue = 0;
        int frameStart = -1;

        for (int sample = 1; sample < capture.SampleCount; sample++)
        {
            if (cs.HasValue && capture.GetChannelState(sample, cs.Value))
            {
                if (mosiBytes.Count > 0)
                {
                    AddFrame(capture, frames, frameStart, sample, [.. mosiBytes], [.. misoBytes], sclk, mosi, miso, cs);
                    mosiBytes.Clear();
                    misoBytes.Clear();
                    bitsInWord = 0;
                }

                continue;
            }

            bool previousClock = capture.GetChannelState(sample - 1, sclk);
            bool currentClock = capture.GetChannelState(sample, sclk);
            bool sampleEdge = options.Cpol == 0 ? !previousClock && currentClock : previousClock && !currentClock;
            if (!sampleEdge)
            {
                continue;
            }

            if (frameStart < 0)
            {
                frameStart = sample;
            }

            int mosiBit = capture.GetChannelState(sample, mosi) ? 1 : 0;
            int misoBit = miso.HasValue && capture.GetChannelState(sample, miso.Value) ? 1 : 0;
            if (options.MsbFirst)
            {
                mosiValue = (mosiValue << 1) | mosiBit;
                misoValue = (misoValue << 1) | misoBit;
            }
            else
            {
                mosiValue |= mosiBit << bitsInWord;
                misoValue |= misoBit << bitsInWord;
            }

            bitsInWord++;
            if (bitsInWord == wordSize)
            {
                mosiBytes.Add((byte)(mosiValue & 0xff));
                if (miso.HasValue)
                {
                    misoBytes.Add((byte)(misoValue & 0xff));
                }

                bitsInWord = 0;
                mosiValue = 0;
                misoValue = 0;
            }
        }

        if (mosiBytes.Count > 0)
        {
            AddFrame(capture, frames, frameStart, capture.SampleCount - 1, [.. mosiBytes], [.. misoBytes], sclk, mosi, miso, cs);
        }

        return frames;
    }

    private void AddFrame(
        LogicCapture capture,
        List<ProtocolFrame> frames,
        int start,
        int end,
        byte[] mosiBytes,
        byte[] misoBytes,
        int sclk,
        int mosi,
        int? miso,
        int? cs)
    {
        string mosiText = string.Join(" ", mosiBytes.Select(value => $"{value:X2}"));
        string misoText = misoBytes.Length > 0 ? $" MISO=[{string.Join(" ", misoBytes.Select(value => $"{value:X2}"))}]" : "";
        List<int> channels = [sclk, mosi];
        if (miso.HasValue)
        {
            channels.Add(miso.Value);
        }

        if (cs.HasValue)
        {
            channels.Add(cs.Value);
        }

        frames.Add(new ProtocolFrame
        {
            Protocol = ProtocolName,
            StartSampleIndex = start,
            EndSampleIndex = end,
            StartTimeSeconds = (double)start / capture.SampleRateHz,
            EndTimeSeconds = (double)end / capture.SampleRateHz,
            DecodedText = $"SPI MOSI=[{mosiText}]{misoText}",
            RawData = mosiBytes,
            Channels = channels.ToArray()
        });
    }

    private static int CountEdges(LogicCapture capture, int channel)
    {
        int edges = 0;
        for (int i = 1; i < capture.SampleCount; i++)
        {
            if (capture.GetChannelState(i - 1, channel) != capture.GetChannelState(i, channel))
            {
                edges++;
            }
        }

        return edges;
    }
}
