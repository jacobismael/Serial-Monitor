using System.Collections.Generic;

namespace serial.Core;

public sealed class ProtocolDecoderOptions
{
    public string Protocol { get; set; } = "Auto";

    public int RxChannel { get; set; }

    public int TxChannel { get; set; } = -1;

    public int BaudRate { get; set; } = 115200;

    public int DataBits { get; set; } = 8;

    public string Parity { get; set; } = "None";

    public int StopBits { get; set; } = 1;

    public bool IdleHigh { get; set; } = true;

    public int SdaChannel { get; set; } = 5;

    public int SclChannel { get; set; } = 4;

    public int SclkChannel { get; set; } = 6;

    public int MosiChannel { get; set; } = 7;

    public int MisoChannel { get; set; } = -1;

    public int CsChannel { get; set; } = -1;

    public int Cpol { get; set; }

    public int Cpha { get; set; }

    public bool MsbFirst { get; set; } = true;

    public int WordSize { get; set; } = 8;

    public int CanRxChannel { get; set; }

    public int CanBitrate { get; set; } = 500000;

    public int CanSamplePointPercent { get; set; } = 75;
}

public interface ProtocolDecoder
{
    string ProtocolName { get; }

    ProtocolDetectionResult Detect(LogicCapture capture);

    IReadOnlyList<ProtocolFrame> Decode(LogicCapture capture, ProtocolDecoderOptions options);
}
