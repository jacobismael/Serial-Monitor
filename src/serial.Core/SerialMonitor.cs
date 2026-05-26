using System;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace serial.Core;

public sealed class SerialMonitor(string port, int baudRate) : IDisposable
{
    private readonly string _port = port;
    private readonly int _baudRate = baudRate;
    private readonly StringBuilder _rxBuffer = new();
    private readonly System.Threading.Lock _lock = new();

    private SerialPort? _serial;
    private TaskCompletionSource<string>? _pendingResponse;
    private bool _disposed;

    public event Action<string>? DataReceived;
    public event Action<Exception>? ErrorReceived;

    public bool IsOpen => _serial?.IsOpen == true;

    public static string[] GetAvailablePorts()
    {
        return [
            .. SerialPort.GetPortNames()
            .Where(port =>
                port.StartsWith("/dev/cu.usb") ||
                port.StartsWith("/dev/cu.SLAB") ||
                port.StartsWith("/dev/cu.wch") ||
                port.Contains("usb", StringComparison.OrdinalIgnoreCase) ||
                port.Contains("serial", StringComparison.OrdinalIgnoreCase) ||
                port.Contains("modem", StringComparison.OrdinalIgnoreCase))
            .OrderBy(port => port)
        ];
    }

    public void Open()
    {
        ThrowIfDisposed();

        if (_serial?.IsOpen == true)
            throw new InvalidOperationException("Serial port is already open.");

        Console.WriteLine($"Opening port {_port}");
        _serial = new SerialPort(_port, _baudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            Encoding = Encoding.UTF8,
            NewLine = "\r\n",
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        _serial.DataReceived += OnSerialDataReceived;
        _serial.Open();

        Console.WriteLine($"Connected to {_port} at {_baudRate} baud.");
    }

    public void Write(string data, SerialLineEnding lineEnding = SerialLineEnding.None)
    {
        ThrowIfDisposed();

        if (_serial?.IsOpen != true)
        {
            throw new InvalidOperationException("Serial port is not open.");
        }

        string ending = lineEnding switch
        {
            SerialLineEnding.None => "",
            SerialLineEnding.LF => "\n",
            SerialLineEnding.CR => "\r",
            SerialLineEnding.CRLF => "\r\n",
            _ => ""
        };

        _serial.Write(data + ending);
        _serial.BaseStream.Flush();
    }

    public async Task<string> SendCommandAsync(string data, int timeoutMs = 1000)
    {
        ThrowIfDisposed();
        if (_serial?.IsOpen != true)
            throw new InvalidOperationException("Serial port is not open.");

        TaskCompletionSource<string> responseSource;

        lock (_lock)
        {
            if (_pendingResponse != null)
                throw new InvalidOperationException("Another command is already waiting for a response.");
            responseSource = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _pendingResponse = responseSource;
        }


        try
        {

            _serial.WriteLine(data);
            Task<string> responseTask = responseSource.Task;
            Task timeoutTask = Task.Delay(timeoutMs);
            Task completedTask = await Task.WhenAny(responseTask, timeoutTask);
            if (completedTask == timeoutTask) return "";
            return await responseTask;
        }
        finally
        {
            lock (_lock)
            {
                if (_pendingResponse == responseSource) _pendingResponse = null;
            }
        }
    }

    public void Close()
    {
        if (_serial == null) return;
        lock (_lock)
        {
            _pendingResponse?.TrySetResult("");
            _pendingResponse = null;
            _rxBuffer.Clear();
        }
        _serial.DataReceived -= OnSerialDataReceived;
        if (_serial.IsOpen) _serial.Close();
        _serial.Dispose();
        _serial = null;
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serial == null) return;
        try
        {
            string data = _serial.ReadExisting();
            if (string.IsNullOrEmpty(data)) return;
            ProcessReceivedData(data);
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(ex);
        }
    }

    private void ProcessReceivedData(string data)
    {
        string[] completedLines;
        lock (_lock)
        {
            _rxBuffer.Append(data);
            var lines = new System.Collections.Generic.List<string>();

            while (true)
            {
                string bufferText = _rxBuffer.ToString();
                int newlineIndex = bufferText.IndexOf('\n');
                if (newlineIndex < 0) break;
                string line = bufferText[..newlineIndex].Trim('\r', '\n');
                _rxBuffer.Remove(0, newlineIndex + 1);
                lines.Add(line);
                _pendingResponse?.TrySetResult(line);
            }
            completedLines = [.. lines];
        }
        foreach (string line in completedLines)
        {
            DataReceived?.Invoke(line);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
    }
}
