using System;
using System.Collections.Generic;

namespace serial.Core;

public static class LogicMockCaptureGenerator
{
    public static LogicCapture Generate(int sampleCount = 2048, int sampleRateHz = 1_000_000)
    {
        Random random = new(12345);
        LogicSample[] samples = new LogicSample[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            uint value = 0;

            SetBit(ref value, 0, ((i / 64) % 2) == 1);
            SetBit(ref value, 1, ((i / 16) % 2) == 1);
            SetBit(ref value, 2, i % 96 < 10);
            SetBit(ref value, 3, IsUartLikeLow(i));
            SetBit(ref value, 4, ((i / 37) % 3) == 0);
            SetBit(ref value, 5, ((i / 113) % 2) == 0);
            SetBit(ref value, 6, (i % 211) < 106);
            SetBit(ref value, 7, random.NextDouble() > 0.94 || ((i / 250) % 2) == 1);

            samples[i] = new LogicSample(i, value);
        }

        return new LogicCapture(8, sampleRateHz, samples);
    }

    public static LogicCapture GenerateUart(int sampleRateHz = 1_000_000, int baudRate = 115200)
    {
        int bitSamples = Math.Max(1, (int)Math.Round((double)sampleRateHz / baudRate));
        byte[] bytes = "Logicom\n"u8.ToArray();
        int sampleCount = 600 + bytes.Length * bitSamples * 12;
        LogicSample[] samples = CreateIdleHighSamples(sampleCount, 8);
        int cursor = 180;

        foreach (byte value in bytes)
        {
            WriteUartByte(samples, channel: 0, value, cursor, bitSamples);
            cursor += bitSamples * 11;
        }

        return new LogicCapture(8, sampleRateHz, samples);
    }

    public static LogicCapture GenerateI2c(int sampleRateHz = 1_000_000)
    {
        LogicSample[] samples = CreateIdleHighSamples(2400, 8);
        int cursor = 160;
        int halfPeriod = 16;
        WriteI2cStart(samples, ref cursor, sda: 5, scl: 4, halfPeriod);
        WriteI2cByte(samples, ref cursor, sda: 5, scl: 4, value: 0x78, ack: true, halfPeriod);
        WriteI2cByte(samples, ref cursor, sda: 5, scl: 4, value: 0x00, ack: true, halfPeriod);
        WriteI2cByte(samples, ref cursor, sda: 5, scl: 4, value: 0xAF, ack: true, halfPeriod);
        WriteI2cStop(samples, ref cursor, sda: 5, scl: 4, halfPeriod);
        return new LogicCapture(8, sampleRateHz, samples);
    }

    public static LogicCapture GenerateSpi(int sampleRateHz = 1_000_000)
    {
        LogicSample[] samples = CreateIdleHighSamples(1600, 8);
        int cursor = 180;
        int halfPeriod = 10;
        WriteSpiByte(samples, ref cursor, sclk: 6, mosi: 7, value: 0x9A, halfPeriod);
        WriteSpiByte(samples, ref cursor, sclk: 6, mosi: 7, value: 0xC3, halfPeriod);
        return new LogicCapture(8, sampleRateHz, samples);
    }

    public static LogicCapture GenerateCan(int sampleRateHz = 1_000_000, int bitrate = 125000)
    {
        int bitSamples = Math.Max(1, (int)Math.Round((double)sampleRateHz / bitrate));
        LogicSample[] samples = CreateIdleHighSamples(2000, 8);
        List<int> bits = [];
        bits.Add(0); // SOF
        AppendBits(bits, 0x123, 11);
        bits.Add(0); // RTR
        bits.Add(0); // IDE
        bits.Add(0); // r0
        AppendBits(bits, 2, 4);
        AppendBits(bits, 0x11, 8);
        AppendBits(bits, 0x22, 8);
        AppendBits(bits, 0, 15); // CRC placeholder
        bits.Add(1); // CRC delimiter
        bits.Add(1); // ACK slot
        bits.Add(1); // ACK delimiter
        for (int i = 0; i < 7; i++)
        {
            bits.Add(1);
        }

        int start = 180;
        for (int bit = 0; bit < bits.Count; bit++)
        {
            SetRange(samples, channel: 0, start + bit * bitSamples, bitSamples, bits[bit] == 1);
        }

        return new LogicCapture(8, sampleRateHz, samples);
    }

    private static bool IsUartLikeLow(int sampleIndex)
    {
        int frameIndex = sampleIndex % 220;
        if (frameIndex > 95)
        {
            return true;
        }

        int bit = frameIndex / 10;
        return bit switch
        {
            0 => false,
            1 => true,
            2 => false,
            3 => true,
            4 => true,
            5 => false,
            6 => false,
            7 => true,
            8 => false,
            _ => true
        };
    }

    private static void SetBit(ref uint value, int bit, bool state)
    {
        if (state)
        {
            value |= 1u << bit;
        }
    }

    private static LogicSample[] CreateIdleHighSamples(int sampleCount, int channelCount)
    {
        uint idleValue = channelCount >= 32 ? uint.MaxValue : (1u << channelCount) - 1u;
        LogicSample[] samples = new LogicSample[sampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new LogicSample(i, idleValue);
        }

        return samples;
    }

    private static void WriteUartByte(
        LogicSample[] samples,
        int channel,
        byte value,
        int start,
        int bitSamples)
    {
        SetRange(samples, channel, start, bitSamples, false);
        for (int bit = 0; bit < 8; bit++)
        {
            bool state = ((value >> bit) & 1) == 1;
            SetRange(samples, channel, start + (bit + 1) * bitSamples, bitSamples, state);
        }

        SetRange(samples, channel, start + 9 * bitSamples, bitSamples, true);
    }

    private static void WriteI2cStart(
        LogicSample[] samples,
        ref int cursor,
        int sda,
        int scl,
        int halfPeriod)
    {
        SetRange(samples, sda, cursor, halfPeriod, true);
        SetRange(samples, scl, cursor, halfPeriod, true);
        cursor += halfPeriod;
        SetRange(samples, sda, cursor, halfPeriod, false);
        SetRange(samples, scl, cursor, halfPeriod, true);
        cursor += halfPeriod;
        SetRange(samples, scl, cursor, halfPeriod, false);
        cursor += halfPeriod;
    }

    private static void WriteI2cStop(
        LogicSample[] samples,
        ref int cursor,
        int sda,
        int scl,
        int halfPeriod)
    {
        SetRange(samples, sda, cursor, halfPeriod, false);
        SetRange(samples, scl, cursor, halfPeriod, false);
        cursor += halfPeriod;
        SetRange(samples, scl, cursor, halfPeriod, true);
        cursor += halfPeriod;
        SetRange(samples, sda, cursor, halfPeriod, true);
        cursor += halfPeriod;
    }

    private static void WriteI2cByte(
        LogicSample[] samples,
        ref int cursor,
        int sda,
        int scl,
        byte value,
        bool ack,
        int halfPeriod)
    {
        for (int bit = 7; bit >= 0; bit--)
        {
            WriteI2cBit(samples, ref cursor, sda, scl, ((value >> bit) & 1) == 1, halfPeriod);
        }

        WriteI2cBit(samples, ref cursor, sda, scl, !ack, halfPeriod);
    }

    private static void WriteI2cBit(
        LogicSample[] samples,
        ref int cursor,
        int sda,
        int scl,
        bool state,
        int halfPeriod)
    {
        SetRange(samples, scl, cursor, halfPeriod, false);
        SetRange(samples, sda, cursor, halfPeriod, state);
        cursor += halfPeriod;
        SetRange(samples, scl, cursor, halfPeriod, true);
        SetRange(samples, sda, cursor, halfPeriod, state);
        cursor += halfPeriod;
        SetRange(samples, scl, cursor, halfPeriod, false);
        cursor += halfPeriod;
    }

    private static void WriteSpiByte(
        LogicSample[] samples,
        ref int cursor,
        int sclk,
        int mosi,
        byte value,
        int halfPeriod)
    {
        for (int bit = 7; bit >= 0; bit--)
        {
            bool state = ((value >> bit) & 1) == 1;
            SetRange(samples, mosi, cursor, halfPeriod, state);
            SetRange(samples, sclk, cursor, halfPeriod, false);
            cursor += halfPeriod;
            SetRange(samples, mosi, cursor, halfPeriod, state);
            SetRange(samples, sclk, cursor, halfPeriod, true);
            cursor += halfPeriod;
        }

        SetRange(samples, sclk, cursor, halfPeriod, false);
        cursor += halfPeriod * 2;
    }

    private static void AppendBits(List<int> bits, int value, int count)
    {
        for (int bit = count - 1; bit >= 0; bit--)
        {
            bits.Add((value >> bit) & 1);
        }
    }

    private static void SetRange(LogicSample[] samples, int channel, int start, int length, bool state)
    {
        uint mask = 1u << channel;
        int end = Math.Min(samples.Length, start + length);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            uint value = samples[i].Value;
            value = state ? value | mask : value & ~mask;
            samples[i] = new LogicSample(samples[i].Index, value);
        }
    }
}
