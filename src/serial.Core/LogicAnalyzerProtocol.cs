namespace serial.Core;

public static class LogicAnalyzerProtocol
{
    public const int HeaderLength = 20;
    public const byte Version = 1;

    public static readonly byte[] Magic = "LGCM"u8.ToArray();

    public const byte CaptureDataMessage = 0x01;
    public const byte StatusMessage = 0x02;
    public const byte ErrorMessage = 0x03;
}

public enum LogicTriggerMode
{
    None,
    Rising,
    Falling,
    High,
    Low
}
