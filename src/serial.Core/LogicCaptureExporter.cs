using System;
using System.Globalization;
using System.Text;

namespace serial.Core;

public static class LogicCaptureExporter
{
    public static string ToCsv(LogicCapture capture)
    {
        StringBuilder builder = new();
        builder.Append("sample_index,time_seconds");
        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            builder.Append(CultureInfo.InvariantCulture, $",ch{channel}");
        }

        builder.AppendLine();

        for (int sample = 0; sample < capture.SampleCount; sample++)
        {
            double timeSeconds = (double)sample / capture.SampleRateHz;
            builder.Append(CultureInfo.InvariantCulture, $"{sample},{timeSeconds:R}");
            for (int channel = 0; channel < capture.ChannelCount; channel++)
            {
                builder.Append(capture.GetChannelState(sample, channel) ? ",1" : ",0");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string ToVcd(LogicCapture capture)
    {
        string[] identifiers = CreateIdentifiers(capture.ChannelCount);
        double nanosecondsPerSample = 1_000_000_000.0 / capture.SampleRateHz;

        StringBuilder builder = new();
        builder.AppendLine("$date");
        builder.AppendLine($"    {DateTime.Now:O}");
        builder.AppendLine("$end");
        builder.AppendLine("$version");
        builder.AppendLine("    Logicom");
        builder.AppendLine("$end");
        builder.AppendLine("$timescale 1 ns $end");
        builder.AppendLine("$scope module logicom $end");
        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            builder.AppendLine($"$var wire 1 {identifiers[channel]} ch{channel} $end");
        }

        builder.AppendLine("$upscope $end");
        builder.AppendLine("$enddefinitions $end");
        builder.AppendLine("$dumpvars");
        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            builder.AppendLine($"{(capture.GetChannelState(0, channel) ? '1' : '0')}{identifiers[channel]}");
        }

        builder.AppendLine("$end");

        bool[] previous = new bool[capture.ChannelCount];
        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            previous[channel] = capture.GetChannelState(0, channel);
        }

        for (int sample = 1; sample < capture.SampleCount; sample++)
        {
            bool wroteTimestamp = false;
            for (int channel = 0; channel < capture.ChannelCount; channel++)
            {
                bool state = capture.GetChannelState(sample, channel);
                if (state == previous[channel])
                {
                    continue;
                }

                if (!wroteTimestamp)
                {
                    long timestampNs = (long)Math.Round(sample * nanosecondsPerSample);
                    builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "#{0}", timestampNs));
                    wroteTimestamp = true;
                }

                builder.AppendLine($"{(state ? '1' : '0')}{identifiers[channel]}");
                previous[channel] = state;
            }
        }

        return builder.ToString();
    }

    private static string[] CreateIdentifiers(int count)
    {
        string[] identifiers = new string[count];
        for (int i = 0; i < count; i++)
        {
            identifiers[i] = ((char)('!' + i)).ToString(CultureInfo.InvariantCulture);
        }

        return identifiers;
    }
}
