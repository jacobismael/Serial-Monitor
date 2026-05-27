using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace serial.Core;

public sealed class UartDecoder : ProtocolDecoder
{
    private static readonly int[] CommonBaudRates = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    public string ProtocolName => "UART";

    public ProtocolDetectionResult Detect(LogicCapture capture)
    {
        ProtocolDetectionResult best = new()
        {
            ProtocolName = ProtocolName,
            Confidence = 0,
            Notes = "No UART-like activity detected."
        };

        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            int fallingEdges = CountFallingEdges(capture, channel);
            if (fallingEdges < 2)
            {
                continue;
            }

            foreach (int baudRate in CommonBaudRates)
            {
                IReadOnlyList<ProtocolFrame> frames = Decode(capture, new ProtocolDecoderOptions
                {
                    RxChannel = channel,
                    BaudRate = baudRate,
                    DataBits = 8,
                    StopBits = 1,
                    IdleHigh = true
                });
                int validFrames = frames.Count(frame => !frame.HasError);
                if (validFrames == 0)
                {
                    continue;
                }

                double printableRatio = frames.Count(frame => frame.RawData.Length > 0 && frame.RawData.All(value => value is >= 32 and <= 126))
                    / Math.Max(1.0, frames.Count);
                int distinctValues = frames
                    .Where(frame => frame.RawData.Length > 0)
                    .Select(frame => frame.RawData[0])
                    .Distinct()
                    .Count();
                double diversityScore = Math.Min(1, distinctValues / 4.0);
                double confidence = Math.Min(1, (validFrames / 12.0) * 0.45 + printableRatio * 0.4 + diversityScore * 0.15);
                if (printableRatio < 0.35 && diversityScore < 0.5)
                {
                    confidence *= 0.25;
                }
                if (confidence > best.Confidence)
                {
                    best = new ProtocolDetectionResult
                    {
                        ProtocolName = ProtocolName,
                        Confidence = confidence,
                        SuggestedBaudRate = baudRate,
                        SuggestedChannels = new Dictionary<string, int> { ["RX"] = channel },
                        Notes = $"Detected {validFrames} candidate UART frames."
                    };
                }
            }
        }

        return best;
    }

    public IReadOnlyList<ProtocolFrame> Decode(LogicCapture capture, ProtocolDecoderOptions options)
    {
        int channel = Math.Clamp(options.RxChannel, 0, capture.ChannelCount - 1);
        int bitSamples = Math.Max(1, (int)Math.Round((double)capture.SampleRateHz / Math.Max(1, options.BaudRate)));
        if (bitSamples < 4)
        {
            return [];
        }

        int frameSamples = bitSamples * (1 + options.DataBits + options.StopBits);
        List<ProtocolFrame> frames = [];

        for (int sample = 1; sample + frameSamples < capture.SampleCount; sample++)
        {
            bool previous = capture.GetChannelState(sample - 1, channel);
            bool current = capture.GetChannelState(sample, channel);
            if (previous != options.IdleHigh || current == options.IdleHigh)
            {
                continue;
            }

            int startMiddle = sample + bitSamples / 2;
            if (capture.GetChannelState(startMiddle, channel) == options.IdleHigh)
            {
                continue;
            }

            int value = 0;
            for (int bit = 0; bit < options.DataBits; bit++)
            {
                int bitSample = sample + bitSamples + (bitSamples / 2) + bit * bitSamples;
                if (capture.GetChannelState(bitSample, channel) == options.IdleHigh)
                {
                    value |= 1 << bit;
                }
            }

            bool stopOk = true;
            for (int stop = 0; stop < options.StopBits; stop++)
            {
                int stopSample = sample + bitSamples + (options.DataBits * bitSamples) + (bitSamples / 2) + stop * bitSamples;
                stopOk &= capture.GetChannelState(stopSample, channel) == options.IdleHigh;
            }

            byte decoded = (byte)(value & 0xff);
            frames.Add(new ProtocolFrame
            {
                Protocol = ProtocolName,
                StartSampleIndex = sample,
                EndSampleIndex = sample + frameSamples,
                StartTimeSeconds = (double)sample / capture.SampleRateHz,
                EndTimeSeconds = (double)(sample + frameSamples) / capture.SampleRateHz,
                DecodedText = decoded >= 32 && decoded <= 126 ? ((char)decoded).ToString() : ".",
                RawData = [decoded],
                HasError = !stopOk,
                ErrorMessage = stopOk ? "" : "Invalid stop bit",
                Channels = [channel]
            });

            sample += Math.Max(1, frameSamples - 1);
        }

        return frames;
    }

    private static int CountFallingEdges(LogicCapture capture, int channel)
    {
        int edges = 0;
        for (int i = 1; i < capture.SampleCount; i++)
        {
            if (capture.GetChannelState(i - 1, channel) && !capture.GetChannelState(i, channel))
            {
                edges++;
            }
        }

        return edges;
    }
}
