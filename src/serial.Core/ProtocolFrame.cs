using System;
using System.Collections.Generic;

namespace serial.Core;

public sealed class ProtocolFrame
{
    public string Protocol { get; set; } = "";

    public int StartSampleIndex { get; set; }

    public int EndSampleIndex { get; set; }

    public double StartTimeSeconds { get; set; }

    public double EndTimeSeconds { get; set; }

    public string DecodedText { get; set; } = "";

    public byte[] RawData { get; set; } = [];

    public bool HasError { get; set; }

    public string ErrorMessage { get; set; } = "";

    public int[] Channels { get; set; } = [];

    public string ChannelText => string.Join(",", Channels);

    public string HexText => RawData.Length == 0 ? "" : Convert.ToHexString(RawData);
}

public sealed class ProtocolDetectionResult
{
    public string ProtocolName { get; set; } = "";

    public double Confidence { get; set; }

    public Dictionary<string, int> SuggestedChannels { get; set; } = [];

    public int? SuggestedBaudRate { get; set; }

    public int? SuggestedClockRate { get; set; }

    public string Notes { get; set; } = "";
}
