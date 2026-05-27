using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace serial.Core;

public sealed class ProtocolAnalyzerService
{
    private readonly IReadOnlyList<ProtocolDecoder> _decoders =
    [
        new UartDecoder(),
        new I2cDecoder(),
        new SpiDecoder(),
        new CanDecoder()
    ];

    public IReadOnlyList<ProtocolDetectionResult> DetectAll(LogicCapture capture)
    {
        return _decoders
            .Select(decoder => SafeDetect(decoder, capture))
            .OrderByDescending(result => result.Confidence)
            .ToArray();
    }

    public ProtocolDetectionResult? DetectBest(LogicCapture capture)
    {
        return DetectAll(capture).FirstOrDefault();
    }

    public IReadOnlyList<ProtocolFrame> Decode(
        LogicCapture capture,
        string protocol,
        ProtocolDecoderOptions options)
    {
        if (string.Equals(protocol, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            ProtocolDetectionResult? best = DetectBest(capture);
            if (best == null || best.Confidence <= 0)
            {
                return [];
            }

            ApplyDetection(options, best);
            protocol = best.ProtocolName;
        }

        ProtocolDecoder? decoder = _decoders.FirstOrDefault(
            candidate => string.Equals(candidate.ProtocolName, protocol, StringComparison.OrdinalIgnoreCase));
        if (decoder == null)
        {
            return [];
        }

        return SafeDecode(decoder, capture, options)
            .OrderBy(frame => frame.StartSampleIndex)
            .ToArray();
    }

    public static string ExportFramesCsv(IEnumerable<ProtocolFrame> frames)
    {
        StringBuilder builder = new();
        builder.AppendLine("time,protocol,channels,description,hex,error");

        foreach (ProtocolFrame frame in frames)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{frame.StartTimeSeconds:R},");
            builder.Append(EscapeCsv(frame.Protocol));
            builder.Append(',');
            builder.Append(EscapeCsv(frame.ChannelText));
            builder.Append(',');
            builder.Append(EscapeCsv(frame.DecodedText));
            builder.Append(',');
            builder.Append(EscapeCsv(frame.HexText));
            builder.Append(',');
            builder.Append(EscapeCsv(frame.HasError ? frame.ErrorMessage : ""));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static ProtocolDetectionResult SafeDetect(ProtocolDecoder decoder, LogicCapture capture)
    {
        try
        {
            return decoder.Detect(capture);
        }
        catch (Exception ex)
        {
            return new ProtocolDetectionResult
            {
                ProtocolName = decoder.ProtocolName,
                Confidence = 0,
                Notes = $"Detector failed: {ex.Message}"
            };
        }
    }

    private static IReadOnlyList<ProtocolFrame> SafeDecode(
        ProtocolDecoder decoder,
        LogicCapture capture,
        ProtocolDecoderOptions options)
    {
        try
        {
            return decoder.Decode(capture, options);
        }
        catch
        {
            return [];
        }
    }

    private static void ApplyDetection(ProtocolDecoderOptions options, ProtocolDetectionResult detection)
    {
        if (detection.SuggestedBaudRate.HasValue)
        {
            options.BaudRate = detection.SuggestedBaudRate.Value;
            options.CanBitrate = detection.SuggestedBaudRate.Value;
        }

        if (detection.SuggestedChannels.TryGetValue("RX", out int rx))
        {
            options.RxChannel = rx;
            options.CanRxChannel = rx;
        }

        if (detection.SuggestedChannels.TryGetValue("SDA", out int sda))
        {
            options.SdaChannel = sda;
        }

        if (detection.SuggestedChannels.TryGetValue("SCL", out int scl))
        {
            options.SclChannel = scl;
        }

        if (detection.SuggestedChannels.TryGetValue("SCLK", out int sclk))
        {
            options.SclkChannel = sclk;
        }

        if (detection.SuggestedChannels.TryGetValue("MOSI", out int mosi))
        {
            options.MosiChannel = mosi;
        }
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
