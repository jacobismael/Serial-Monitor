using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using serial.Core;

namespace serial.Desktop;

public sealed class LogicWaveformView : Control
{
    private const double LabelWidth = 118;
    private const double AxisHeight = 28;
    private const double RowHeight = 44;
    private const double MinimumPixelsPerSample = 0.05;
    private const double MaximumPixelsPerSample = 80;

    private LogicCapture? _capture;
    private IReadOnlyList<ProtocolFrame> _protocolFrames = [];
    private IReadOnlyList<string> _channelLabels = [];
    private IReadOnlyList<LogicBusDefinition> _busDefinitions = [];
    private double _pixelsPerSample = 1;
    private double _panSamples;
    private bool _isPanning;
    private Point _lastPanPoint;
    private Point? _cursorPoint;
    private int? _cursorASample;
    private int? _cursorBSample;

    public event Action<string>? CursorChanged;
    public event Action<int?, int?>? MeasurementsChanged;

    public LogicWaveformView()
    {
        ClipToBounds = true;
        Focusable = true;

        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += (_, _) =>
        {
            _cursorPoint = null;
            CurrentSampleIndex = null;
            CurrentChannelIndex = null;
            CursorChanged?.Invoke("Cursor: -");
            InvalidateVisual();
        };
    }

    public LogicCapture? Capture => _capture;

    public int? CurrentSampleIndex { get; private set; }

    public int? CurrentChannelIndex { get; private set; }

    public void SetCapture(LogicCapture? capture)
    {
        _capture = capture;
        _protocolFrames = [];
        _panSamples = 0;
        if (capture != null)
        {
            if (_cursorASample.HasValue && _cursorASample.Value >= capture.SampleCount)
            {
                _cursorASample = null;
            }

            if (_cursorBSample.HasValue && _cursorBSample.Value >= capture.SampleCount)
            {
                _cursorBSample = null;
            }
        }

        Height = capture == null
            ? 320
            : AxisHeight + ((capture.ChannelCount + _busDefinitions.Count) * RowHeight) + 10;
        FitView();
    }

    public void SetSignalLabels(IEnumerable<string>? labels)
    {
        _channelLabels = labels?.ToArray() ?? [];
        InvalidateVisual();
    }

    public void SetBusDefinitions(IEnumerable<LogicBusDefinition>? buses)
    {
        _busDefinitions = buses?.ToArray() ?? [];
        if (_capture != null)
        {
            Height = AxisHeight + ((_capture.ChannelCount + _busDefinitions.Count) * RowHeight) + 10;
        }

        InvalidateVisual();
    }

    public void SetCursorA(int sample)
    {
        _cursorASample = ClampSample(sample);
        MeasurementsChanged?.Invoke(_cursorASample, _cursorBSample);
        InvalidateVisual();
    }

    public void SetCursorB(int sample)
    {
        _cursorBSample = ClampSample(sample);
        MeasurementsChanged?.Invoke(_cursorASample, _cursorBSample);
        InvalidateVisual();
    }

    public void ClearCursors()
    {
        _cursorASample = null;
        _cursorBSample = null;
        MeasurementsChanged?.Invoke(_cursorASample, _cursorBSample);
        InvalidateVisual();
    }

    public void SetProtocolFrames(IEnumerable<ProtocolFrame>? frames)
    {
        _protocolFrames = frames?.ToArray() ?? [];
        InvalidateVisual();
    }

    public void FitView()
    {
        if (_capture == null || _capture.SampleCount <= 1)
        {
            _pixelsPerSample = 1;
            _panSamples = 0;
            InvalidateVisual();
            return;
        }

        double drawingWidth = Math.Max(1, Bounds.Width - LabelWidth - 16);
        _pixelsPerSample = Math.Clamp(drawingWidth / Math.Max(1, _capture.SampleCount - 1), MinimumPixelsPerSample, MaximumPixelsPerSample);
        _panSamples = 0;
        InvalidateVisual();
    }

    public void CenterOnFrame(ProtocolFrame frame)
    {
        LogicCapture? capture = _capture;
        if (capture == null)
        {
            return;
        }

        double visibleSamples = Math.Max(1, (Bounds.Width - LabelWidth) / _pixelsPerSample);
        _panSamples = frame.StartSampleIndex - visibleSamples / 2;
        ClampPan(capture);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect bounds = new(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);

        LogicCapture? capture = _capture;
        if (capture == null || capture.SampleCount == 0)
        {
            DrawText(
                context,
                "No capture loaded.\nConnect FPGA probe or generate a mock capture.\nUse Start Capture, Single Capture, or Generate Mock Capture.",
                new Point(16, 18),
                Brushes.Gray,
                13);
            return;
        }

        using (context.PushClip(bounds))
        {
            DrawAxis(context, capture);
            DrawChannels(context, capture);
            DrawBuses(context, capture);
            DrawTriggerMarker(context, capture);
            DrawProtocolFrames(context, capture);
            DrawMeasurementCursors(context, capture);
            DrawCursor(context, capture);
        }
    }

    private void DrawAxis(DrawingContext context, LogicCapture capture)
    {
        Pen gridPen = new(Brushes.DimGray, 0.5);
        double drawingLeft = LabelWidth;
        double drawingWidth = Math.Max(1, Bounds.Width - drawingLeft);
        int firstSample = Math.Max(0, (int)Math.Floor(_panSamples));
        int lastSample = Math.Min(capture.SampleCount - 1, (int)Math.Ceiling(_panSamples + drawingWidth / _pixelsPerSample));
        int step = ChooseAxisStep(Math.Max(1, lastSample - firstSample));

        for (int sample = firstSample - (firstSample % step); sample <= lastSample; sample += step)
        {
            if (sample < 0)
            {
                continue;
            }

            double x = SampleToX(sample);
            context.DrawLine(gridPen, new Point(x, AxisHeight), new Point(x, Bounds.Height));
            DrawText(
                context,
                FormatTime(sample, capture),
                new Point(x + 3, 6),
                Brushes.Gray,
                11);
        }
    }

    private void DrawChannels(DrawingContext context, LogicCapture capture)
    {
        Pen rowPen = new(Brushes.DimGray, 0.5);
        Pen waveformPen = new(Brushes.Cyan, 1.4);
        double drawingWidth = Math.Max(1, Bounds.Width - LabelWidth);
        int firstSample = Math.Max(0, (int)Math.Floor(_panSamples) - 1);
        int lastSample = Math.Min(capture.SampleCount - 1, (int)Math.Ceiling(_panSamples + drawingWidth / _pixelsPerSample) + 1);

        for (int channel = 0; channel < capture.ChannelCount; channel++)
        {
            double rowTop = AxisHeight + channel * RowHeight;
            double rowBottom = rowTop + RowHeight;
            double yHigh = rowTop + RowHeight * 0.25;
            double yLow = rowTop + RowHeight * 0.70;

            context.DrawLine(rowPen, new Point(0, rowBottom), new Point(Bounds.Width, rowBottom));
            DrawText(context, GetChannelLabel(channel), new Point(12, rowTop + 12), Brushes.White, 13);

            bool state = capture.GetChannelState(firstSample, channel);
            double previousX = SampleToX(firstSample);
            double previousY = state ? yHigh : yLow;

            for (int sample = firstSample + 1; sample <= lastSample; sample++)
            {
                bool nextState = capture.GetChannelState(sample, channel);
                double x = SampleToX(sample);
                context.DrawLine(waveformPen, new Point(previousX, previousY), new Point(x, previousY));

                if (nextState != state)
                {
                    double nextY = nextState ? yHigh : yLow;
                    context.DrawLine(waveformPen, new Point(x, previousY), new Point(x, nextY));
                    previousY = nextY;
                    state = nextState;
                }

                previousX = x;
            }
        }
    }

    private void DrawBuses(DrawingContext context, LogicCapture capture)
    {
        if (_busDefinitions.Count == 0)
        {
            return;
        }

        Pen rowPen = new(Brushes.DimGray, 0.5);
        Pen busPen = new(Brushes.LightGreen, 1.1);
        double drawingWidth = Math.Max(1, Bounds.Width - LabelWidth);
        int firstSample = Math.Max(0, (int)Math.Floor(_panSamples));
        int lastSample = Math.Min(capture.SampleCount - 1, (int)Math.Ceiling(_panSamples + drawingWidth / _pixelsPerSample));

        for (int busIndex = 0; busIndex < _busDefinitions.Count; busIndex++)
        {
            LogicBusDefinition bus = _busDefinitions[busIndex];
            double rowTop = AxisHeight + (capture.ChannelCount + busIndex) * RowHeight;
            double rowBottom = rowTop + RowHeight;
            double y = rowTop + RowHeight * 0.50;
            context.DrawLine(rowPen, new Point(0, rowBottom), new Point(Bounds.Width, rowBottom));
            DrawText(context, bus.DisplayName, new Point(12, rowTop + 12), Brushes.LightGreen, 13);

            int step = Math.Max(1, (int)Math.Round(60 / Math.Max(0.1, _pixelsPerSample)));
            long previous = GetBusValue(capture, bus, firstSample);
            double previousX = SampleToX(firstSample);
            context.DrawLine(busPen, new Point(previousX, y), new Point(SampleToX(lastSample), y));

            for (int sample = firstSample + step; sample <= lastSample; sample += step)
            {
                long value = GetBusValue(capture, bus, sample);
                if (value == previous)
                {
                    continue;
                }

                double x = SampleToX(sample);
                DrawText(context, FormatBusValue(value, bus.Radix), new Point(Math.Max(LabelWidth, previousX + 4), rowTop + 8), Brushes.LightGreen, 11);
                context.DrawLine(busPen, new Point(x, rowTop + 8), new Point(x, rowBottom - 8));
                previous = value;
                previousX = x;
            }

            DrawText(context, FormatBusValue(previous, bus.Radix), new Point(Math.Max(LabelWidth, previousX + 4), rowTop + 8), Brushes.LightGreen, 11);
        }
    }

    private void DrawCursor(DrawingContext context, LogicCapture capture)
    {
        if (_cursorPoint is not Point cursor || cursor.X < LabelWidth)
        {
            return;
        }

        int sampleIndex = XToSample(cursor.X);
        if (sampleIndex < 0 || sampleIndex >= capture.SampleCount)
        {
            return;
        }

        Pen cursorPen = new(Brushes.Yellow, 1);
        context.DrawLine(cursorPen, new Point(cursor.X, AxisHeight), new Point(cursor.X, Bounds.Height));
    }

    private void DrawProtocolFrames(DrawingContext context, LogicCapture capture)
    {
        if (_protocolFrames.Count == 0)
        {
            return;
        }

        Pen markerPen = new(Brushes.Orange, 1);
        IBrush labelBackground = new SolidColorBrush(Color.FromArgb(220, 22, 22, 22));
        Pen labelBorder = new(Brushes.Orange, 0.6);
        double drawingWidth = Math.Max(1, Bounds.Width - LabelWidth);
        int firstSample = Math.Max(0, (int)Math.Floor(_panSamples) - 1);
        int lastSample = Math.Min(capture.SampleCount - 1, (int)Math.Ceiling(_panSamples + drawingWidth / _pixelsPerSample) + 1);
        int drawn = 0;

        foreach (ProtocolFrame frame in _protocolFrames)
        {
            if (frame.EndSampleIndex < firstSample || frame.StartSampleIndex > lastSample)
            {
                continue;
            }

            double x = SampleToX(frame.StartSampleIndex);
            context.DrawLine(markerPen, new Point(x, AxisHeight), new Point(x, Bounds.Height));

            string label = frame.DecodedText.Length > 30
                ? frame.DecodedText[..30] + "..."
                : frame.DecodedText;
            FormattedText formatted = CreateFormattedText(label, Brushes.Orange, 11);
            double labelWidth = Math.Min(260, formatted.Width + 10);
            Rect labelRect = new(Math.Clamp(x + 4, LabelWidth, Math.Max(LabelWidth, Bounds.Width - labelWidth - 4)), AxisHeight + 4, labelWidth, 20);
            context.FillRectangle(labelBackground, labelRect);
            context.DrawRectangle(labelBorder, labelRect);
            context.DrawText(formatted, new Point(labelRect.X + 5, labelRect.Y + 3));

            drawn++;
            if (drawn >= 80)
            {
                break;
            }
        }
    }

    private void DrawTriggerMarker(DrawingContext context, LogicCapture capture)
    {
        if (!capture.TriggerSampleIndex.HasValue)
        {
            return;
        }

        int sample = Math.Clamp(capture.TriggerSampleIndex.Value, 0, Math.Max(0, capture.SampleCount - 1));
        double x = SampleToX(sample);
        if (x < LabelWidth || x > Bounds.Width)
        {
            return;
        }

        Pen triggerPen = new(Brushes.LimeGreen, 1.2);
        context.DrawLine(triggerPen, new Point(x, AxisHeight), new Point(x, Bounds.Height));
        DrawText(context, "T", new Point(x + 4, AxisHeight + 4), Brushes.LimeGreen, 12);
    }

    private void DrawMeasurementCursors(DrawingContext context, LogicCapture capture)
    {
        DrawMeasurementCursor(context, capture, _cursorASample, "A", Brushes.Yellow);
        DrawMeasurementCursor(context, capture, _cursorBSample, "B", Brushes.Orange);

        if (_cursorASample.HasValue && _cursorBSample.HasValue)
        {
            double xA = SampleToX(_cursorASample.Value);
            double xB = SampleToX(_cursorBSample.Value);
            if (Math.Max(xA, xB) >= LabelWidth && Math.Min(xA, xB) <= Bounds.Width)
            {
                int deltaSamples = Math.Abs(_cursorBSample.Value - _cursorASample.Value);
                string text = $"Δ {deltaSamples} samples / {FormatDuration(deltaSamples / (double)capture.SampleRateHz)}";
                double x = Math.Min(xA, xB) + Math.Abs(xB - xA) / 2;
                DrawText(context, text, new Point(Math.Clamp(x + 4, LabelWidth, Math.Max(LabelWidth, Bounds.Width - 180)), AxisHeight + 18), Brushes.Yellow, 11);
            }
        }
    }

    private void DrawMeasurementCursor(DrawingContext context, LogicCapture capture, int? sample, string label, IBrush brush)
    {
        if (!sample.HasValue)
        {
            return;
        }

        int clamped = Math.Clamp(sample.Value, 0, Math.Max(0, capture.SampleCount - 1));
        double x = SampleToX(clamped);
        if (x < LabelWidth || x > Bounds.Width)
        {
            return;
        }

        Pen pen = new(brush, 1);
        context.DrawLine(pen, new Point(x, AxisHeight), new Point(x, Bounds.Height));
        DrawText(context, label, new Point(x + 4, AxisHeight + 34), brush, 12);
    }

    private double SampleToX(int sample)
    {
        return LabelWidth + ((sample - _panSamples) * _pixelsPerSample);
    }

    private int XToSample(double x)
    {
        return (int)Math.Round(_panSamples + ((x - LabelWidth) / _pixelsPerSample));
    }

    private int? ClampSample(int sample)
    {
        LogicCapture? capture = _capture;
        if (capture == null || capture.SampleCount == 0)
        {
            return null;
        }

        return Math.Clamp(sample, 0, capture.SampleCount - 1);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        LogicCapture? capture = _capture;
        if (capture == null)
        {
            return;
        }

        Point point = e.GetPosition(this);
        if (point.X < LabelWidth)
        {
            return;
        }

        double before = _panSamples + ((point.X - LabelWidth) / _pixelsPerSample);
        const double ZoomStep = 1.08;
        double zoomFactor = e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep;
        _pixelsPerSample = Math.Clamp(_pixelsPerSample * zoomFactor, MinimumPixelsPerSample, MaximumPixelsPerSample);
        _panSamples = before - ((point.X - LabelWidth) / _pixelsPerSample);
        ClampPan(capture);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            LogicCapture? capture = _capture;
            Point point = e.GetPosition(this);
            if (capture != null && point.X >= LabelWidth)
            {
                int sample = Math.Clamp(XToSample(point.X), 0, capture.SampleCount - 1);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    SetCursorB(sample);
                }
                else
                {
                    SetCursorA(sample);
                }
            }

            _isPanning = true;
            _lastPanPoint = point;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point point = e.GetPosition(this);
        _cursorPoint = point;

        if (_isPanning && _capture != null)
        {
            double deltaX = point.X - _lastPanPoint.X;
            _panSamples -= deltaX / _pixelsPerSample;
            _lastPanPoint = point;
            ClampPan(_capture);
        }

        UpdateCursorReadout(point);
        InvalidateVisual();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void UpdateCursorReadout(Point point)
    {
        LogicCapture? capture = _capture;
        if (capture == null || point.X < LabelWidth)
        {
            CursorChanged?.Invoke("Cursor: -");
            CurrentSampleIndex = null;
            CurrentChannelIndex = null;
            return;
        }

        int sample = XToSample(point.X);
        if (sample < 0 || sample >= capture.SampleCount)
        {
            CursorChanged?.Invoke("Cursor: -");
            CurrentSampleIndex = null;
            CurrentChannelIndex = null;
            return;
        }

        CurrentSampleIndex = sample;
        int channel = (int)Math.Floor((point.Y - AxisHeight) / RowHeight);
        CurrentChannelIndex = channel >= 0 && channel < capture.ChannelCount ? channel : null;
        string channelText = channel >= 0 && channel < capture.ChannelCount
            ? $" CH{channel}={(capture.GetChannelState(sample, channel) ? 1 : 0)}"
            : "";
        CursorChanged?.Invoke(
            $"Sample: {sample}   Time: {FormatSampleTime(sample, capture)}{channelText}");
    }

    private void ClampPan(LogicCapture capture)
    {
        double visibleSamples = Math.Max(1, (Bounds.Width - LabelWidth) / _pixelsPerSample);
        double maxPan = Math.Max(0, capture.SampleCount - visibleSamples);
        _panSamples = Math.Clamp(_panSamples, 0, maxPan);
    }

    private static int ChooseAxisStep(int visibleSamples)
    {
        int rough = Math.Max(1, visibleSamples / 8);
        int magnitude = 1;
        while (magnitude * 10 < rough)
        {
            magnitude *= 10;
        }

        if (rough <= magnitude * 2)
        {
            return magnitude * 2;
        }

        if (rough <= magnitude * 5)
        {
            return magnitude * 5;
        }

        return magnitude * 10;
    }

    private static string FormatTime(int sample, LogicCapture capture)
    {
        if (capture.TriggerSampleIndex.HasValue)
        {
            int relativeSample = sample - capture.TriggerSampleIndex.Value;
            if (relativeSample == 0)
            {
                return "T";
            }

            string sign = relativeSample > 0 ? "+" : "-";
            return sign + FormatDuration(Math.Abs(relativeSample) / (double)capture.SampleRateHz);
        }

        return FormatDuration((double)sample / capture.SampleRateHz);
    }

    private static string FormatSampleTime(int sample, LogicCapture capture)
    {
        if (capture.TriggerSampleIndex.HasValue)
        {
            int relativeSample = sample - capture.TriggerSampleIndex.Value;
            string sign = relativeSample >= 0 ? "+" : "-";
            return sign + FormatDuration(Math.Abs(relativeSample) / (double)capture.SampleRateHz);
        }

        return FormatDuration((double)sample / capture.SampleRateHz);
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 0.001)
        {
            return $"{seconds * 1_000_000:0.##} us";
        }

        if (seconds < 1)
        {
            return $"{seconds * 1000:0.##} ms";
        }

        return $"{seconds:0.###} s";
    }

    private string GetChannelLabel(int channel)
    {
        return channel >= 0 && channel < _channelLabels.Count ? _channelLabels[channel] : $"CH{channel}";
    }

    private static long GetBusValue(LogicCapture capture, LogicBusDefinition bus, int sample)
    {
        long value = 0;
        for (int bit = 0; bit < bus.Channels.Count; bit++)
        {
            if (capture.GetChannelState(sample, bus.Channels[bit]))
            {
                value |= 1L << bit;
            }
        }

        return value;
    }

    private static string FormatBusValue(long value, string? radix)
    {
        return radix switch
        {
            "Binary" => "0b" + Convert.ToString(value, 2),
            "Unsigned" => value.ToString(CultureInfo.InvariantCulture),
            "Signed" => value.ToString(CultureInfo.InvariantCulture),
            _ => "0x" + value.ToString("X", CultureInfo.InvariantCulture)
        };
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        Point point,
        IBrush brush,
        double fontSize)
    {
        FormattedText formattedText = new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Menlo, Consolas, monospace"),
            fontSize,
            brush);
        context.DrawText(formattedText, point);
    }

    private static FormattedText CreateFormattedText(string text, IBrush brush, double fontSize)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Menlo, Consolas, monospace"),
            fontSize,
            brush);
    }
}
