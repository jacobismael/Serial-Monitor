using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace serial.Core;

public sealed class LogicAnalyzerParser
{
    private const int MaximumPayloadLength = 32 * 1024 * 1024;
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<LogicCapture> Append(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            _buffer.Add(data[i]);
        }

        List<LogicCapture> captures = [];

        while (true)
        {
            int magicIndex = FindMagic();
            if (magicIndex < 0)
            {
                TrimGarbageWithoutMagic();
                break;
            }

            if (magicIndex > 0)
            {
                _buffer.RemoveRange(0, magicIndex);
            }

            if (_buffer.Count < LogicAnalyzerProtocol.HeaderLength)
            {
                break;
            }

            byte version = _buffer[4];
            byte messageType = _buffer[5];
            byte channelCount = _buffer[6];
            uint sampleRateHz = ReadUInt32(8);
            uint sampleCount = ReadUInt32(12);
            uint payloadLength = ReadUInt32(16);

            if (!IsHeaderPlausible(version, messageType, channelCount, sampleRateHz, sampleCount, payloadLength))
            {
                _buffer.RemoveAt(0);
                continue;
            }

            int packetLength = LogicAnalyzerProtocol.HeaderLength + (int)payloadLength;
            if (_buffer.Count < packetLength)
            {
                break;
            }

            if (messageType == LogicAnalyzerProtocol.CaptureDataMessage
                && TryDecodeCapture(channelCount, (int)sampleRateHz, (int)sampleCount, (int)payloadLength, out LogicCapture? capture)
                && capture != null)
            {
                captures.Add(capture);
            }

            _buffer.RemoveRange(0, packetLength);
        }

        return captures;
    }

    public void Reset()
    {
        _buffer.Clear();
    }

    private int FindMagic()
    {
        if (_buffer.Count < LogicAnalyzerProtocol.Magic.Length)
        {
            return -1;
        }

        for (int i = 0; i <= _buffer.Count - LogicAnalyzerProtocol.Magic.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < LogicAnalyzerProtocol.Magic.Length; j++)
            {
                if (_buffer[i + j] != LogicAnalyzerProtocol.Magic[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    private void TrimGarbageWithoutMagic()
    {
        int keep = Math.Min(_buffer.Count, LogicAnalyzerProtocol.Magic.Length - 1);
        if (_buffer.Count > keep)
        {
            _buffer.RemoveRange(0, _buffer.Count - keep);
        }
    }

    private uint ReadUInt32(int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = _buffer[offset + i];
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static bool IsHeaderPlausible(
        byte version,
        byte messageType,
        byte channelCount,
        uint sampleRateHz,
        uint sampleCount,
        uint payloadLength)
    {
        if (version != LogicAnalyzerProtocol.Version)
        {
            return false;
        }

        if (messageType != LogicAnalyzerProtocol.CaptureDataMessage
            && messageType != LogicAnalyzerProtocol.StatusMessage
            && messageType != LogicAnalyzerProtocol.ErrorMessage)
        {
            return false;
        }

        if (payloadLength > MaximumPayloadLength)
        {
            return false;
        }

        if (messageType != LogicAnalyzerProtocol.CaptureDataMessage)
        {
            return true;
        }

        if ((channelCount != 8 && channelCount != 16) || sampleRateHz == 0 || sampleCount == 0)
        {
            return false;
        }

        int bytesPerSample = channelCount == 8 ? 1 : 2;
        return payloadLength >= sampleCount * (uint)bytesPerSample;
    }

    private bool TryDecodeCapture(
        byte channelCount,
        int sampleRateHz,
        int sampleCount,
        int payloadLength,
        out LogicCapture? capture)
    {
        capture = null;
        int bytesPerSample = channelCount == 8 ? 1 : 2;
        if (payloadLength < sampleCount * bytesPerSample)
        {
            return false;
        }

        int payloadOffset = LogicAnalyzerProtocol.HeaderLength;
        LogicSample[] samples = new LogicSample[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            int offset = payloadOffset + (i * bytesPerSample);
            uint value = channelCount == 8
                ? _buffer[offset]
                : (uint)(_buffer[offset] | (_buffer[offset + 1] << 8));
            samples[i] = new LogicSample(i, value);
        }

        capture = new LogicCapture(channelCount, sampleRateHz, samples);
        return true;
    }
}
