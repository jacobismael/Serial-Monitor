using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace serial.Core;

public enum LogicTriggerConditionType
{
    Level,
    Edge,
    BusCompare
}

public enum LogicTriggerOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

public enum LogicTriggerEdge
{
    Rising,
    Falling,
    Either,
    NoChange
}

public enum LogicTriggerCombineMode
{
    And,
    Or
}

public enum LogicTriggerBuilderMode
{
    Basic,
    Advanced
}

public enum LogicQualificationMode
{
    StoreAll,
    StoreWhenTrue
}

public sealed class LogicSignalDefinition
{
    public int Channel { get; set; }

    public string Name { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"CH{Channel}"
        : $"{Name.Trim()} (CH{Channel})";
}

public sealed class LogicBusDefinition
{
    public string Name { get; set; } = "BUS0";

    public List<int> Channels { get; set; } = [];

    public int LsbChannel { get; set; }

    public string Radix { get; set; } = "Hex";

    public string DisplayName => $"{Name}[{Math.Max(0, Channels.Count - 1)}:0]";
}

public sealed class LogicTriggerCondition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public string SignalName { get; set; } = "CH0";

    public List<int> ChannelIndexes { get; set; } = [0];

    public LogicTriggerConditionType ConditionType { get; set; } = LogicTriggerConditionType.Edge;

    public LogicTriggerOperator Operator { get; set; } = LogicTriggerOperator.Equal;

    public string Value { get; set; } = "1";

    public LogicTriggerEdge EdgeType { get; set; } = LogicTriggerEdge.Rising;

    public bool Evaluate(LogicCapture capture, int sampleIndex)
    {
        if (sampleIndex < 0 || sampleIndex >= capture.SampleCount)
        {
            return false;
        }

        return ConditionType switch
        {
            LogicTriggerConditionType.Edge => EvaluateEdge(capture, sampleIndex),
            LogicTriggerConditionType.BusCompare => EvaluateBusCompare(capture, sampleIndex),
            _ => EvaluateLevel(capture, sampleIndex)
        };
    }

    public string ToExpressionText()
    {
        string signal = string.IsNullOrWhiteSpace(SignalName)
            ? string.Join(",", ChannelIndexes.Select(channel => $"CH{channel}"))
            : SignalName.Trim();

        return ConditionType switch
        {
            LogicTriggerConditionType.Edge => $"{signal} {FormatEdge(EdgeType)}",
            LogicTriggerConditionType.BusCompare => $"{signal} {FormatOperator(Operator)} {Value}",
            _ => $"{signal} {FormatOperator(Operator)} {Value}"
        };
    }

    private bool EvaluateLevel(LogicCapture capture, int sampleIndex)
    {
        int channel = ChannelIndexes.FirstOrDefault();
        int current = capture.GetChannelState(sampleIndex, channel) ? 1 : 0;
        long expected = ParseInteger(Value, 0);
        return Compare(current, expected, Operator);
    }

    private bool EvaluateEdge(LogicCapture capture, int sampleIndex)
    {
        if (sampleIndex <= 0)
        {
            return false;
        }

        int channel = ChannelIndexes.FirstOrDefault();
        bool previous = capture.GetChannelState(sampleIndex - 1, channel);
        bool current = capture.GetChannelState(sampleIndex, channel);

        return EdgeType switch
        {
            LogicTriggerEdge.Rising => !previous && current,
            LogicTriggerEdge.Falling => previous && !current,
            LogicTriggerEdge.Either => previous != current,
            LogicTriggerEdge.NoChange => previous == current,
            _ => false
        };
    }

    private bool EvaluateBusCompare(LogicCapture capture, int sampleIndex)
    {
        long current = 0;
        for (int bit = 0; bit < ChannelIndexes.Count; bit++)
        {
            if (capture.GetChannelState(sampleIndex, ChannelIndexes[bit]))
            {
                current |= 1L << bit;
            }
        }

        long expected = ParseInteger(Value, 0);
        return Compare(current, expected, Operator);
    }

    private static bool Compare(long current, long expected, LogicTriggerOperator op)
    {
        return op switch
        {
            LogicTriggerOperator.NotEqual => current != expected,
            LogicTriggerOperator.LessThan => current < expected,
            LogicTriggerOperator.LessThanOrEqual => current <= expected,
            LogicTriggerOperator.GreaterThan => current > expected,
            LogicTriggerOperator.GreaterThanOrEqual => current >= expected,
            _ => current == expected
        };
    }

    public static long ParseInteger(string? value, long fallback)
    {
        string trimmed = (value ?? "").Trim().Replace("_", "", StringComparison.Ordinal);
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        try
        {
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt64(trimmed[2..], 16);
            }

            if (trimmed.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt64(trimmed[2..], 2);
            }

            return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static string FormatOperator(LogicTriggerOperator op)
    {
        return op switch
        {
            LogicTriggerOperator.NotEqual => "!=",
            LogicTriggerOperator.LessThan => "<",
            LogicTriggerOperator.LessThanOrEqual => "<=",
            LogicTriggerOperator.GreaterThan => ">",
            LogicTriggerOperator.GreaterThanOrEqual => ">=",
            _ => "=="
        };
    }

    public static string FormatEdge(LogicTriggerEdge edge)
    {
        return edge switch
        {
            LogicTriggerEdge.Falling => "falling",
            LogicTriggerEdge.Either => "either edge",
            LogicTriggerEdge.NoChange => "no change",
            _ => "rising"
        };
    }
}

public sealed class LogicTriggerExpression
{
    public LogicTriggerBuilderMode Mode { get; set; } = LogicTriggerBuilderMode.Basic;

    public List<LogicTriggerCondition> Conditions { get; set; } = [];

    public LogicTriggerCombineMode CombineMode { get; set; } = LogicTriggerCombineMode.And;

    public bool IsEmpty => Conditions.Count == 0;

    public int? FindMatch(LogicCapture capture)
    {
        if (IsEmpty || capture.SampleCount == 0)
        {
            return null;
        }

        int start = Conditions.Any(condition => condition.ConditionType == LogicTriggerConditionType.Edge)
            ? 1
            : 0;

        for (int sample = start; sample < capture.SampleCount; sample++)
        {
            bool matched = CombineMode == LogicTriggerCombineMode.And
                ? Conditions.All(condition => condition.Evaluate(capture, sample))
                : Conditions.Any(condition => condition.Evaluate(capture, sample));

            if (matched)
            {
                return sample;
            }
        }

        return null;
    }

    public string ToExpressionText()
    {
        if (IsEmpty)
        {
            return "Immediate";
        }

        string separator = CombineMode == LogicTriggerCombineMode.And ? " AND " : " OR ";
        return string.Join(separator, Conditions.Select(condition => condition.ToExpressionText()));
    }
}
