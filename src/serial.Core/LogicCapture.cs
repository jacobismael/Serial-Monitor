using System;
using System.Collections.Generic;
using System.Linq;

namespace serial.Core;

public sealed class LogicCapture
{
    public LogicCapture(int channelCount, int sampleRateHz, IEnumerable<LogicSample> samples)
    {
        if (channelCount != 8 && channelCount != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be 8 or 16.");
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "Sample rate must be positive.");
        }

        ChannelCount = channelCount;
        SampleRateHz = sampleRateHz;
        Samples = samples.ToArray();
    }

    public int ChannelCount { get; }

    public int SampleRateHz { get; }

    public IReadOnlyList<LogicSample> Samples { get; }

    public int? TriggerSampleIndex { get; set; }

    public int SampleCount => Samples.Count;

    public double DurationSeconds => SampleCount == 0 ? 0 : (double)SampleCount / SampleRateHz;

    public bool GetChannelState(int sampleIndex, int channel)
    {
        if (sampleIndex < 0 || sampleIndex >= Samples.Count)
        {
            return false;
        }

        if (channel < 0 || channel >= ChannelCount)
        {
            return false;
        }

        return (Samples[sampleIndex].Value & (1u << channel)) != 0;
    }
}
