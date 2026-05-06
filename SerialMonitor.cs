using System;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;


class SerialMonitor : IDisposable {
    private string port;
    private int baudRate;
    private SerialPort? _serial;
    public event Action<string>? DataReceived;
    private bool _disposed;
    private readonly StringBuilder _rxBuffer;
    private readonly object _lock;

    private TaskCompletionSource<string>? _pendingResponse;
    public SerialMonitor(string port, int baudRate) {
        this.port = port;
        this.baudRate = baudRate;
        _rxBuffer = new StringBuilder();
        _lock = new object();
    }
    public void Open() {
        if(_serial?.IsOpen == true) 
            throw new InvalidOperationException("Serial port is already open.");
        
        Console.WriteLine($"Opening port {this.port}");
        _serial = new SerialPort(port, baudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = 500
        };
        _serial.DataReceived += OnSerialDataReceived;
        _serial.Open();

        Console.WriteLine($"Connected to {port} at {baudRate} baud.");
    }

    public async Task<string> WriteLine(string data, int timeoutMs = 1000) {
        if (_serial?.IsOpen != true)
            throw new InvalidOperationException("Serial port is not open.");

        lock (_lock) {
            if (_pendingResponse != null)
                throw new InvalidOperationException("Another command is already waiting for a response.");
            _pendingResponse = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        _serial.WriteLine(data);
        
        Task<string> responseTask = _pendingResponse.Task;
        Task timeoutTask = Task.Delay(timeoutMs);
        Task completedTask = await Task.WhenAny(responseTask, timeoutTask);

        lock (_lock) _pendingResponse = null;
        if (completedTask == timeoutTask) return "";

        return await responseTask;
    }

    public void Close() {
        if(_serial == null) return;
        _serial.DataReceived -= OnSerialDataReceived;
        if (_serial.IsOpen) _serial.Close();
        _serial.Dispose();
        _serial = null;
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e) {
        if(_serial == null) return;
        string data = _serial.ReadExisting();
        lock (_lock) {
            _rxBuffer.Append(data);

            while (true) {
                string bufferText = _rxBuffer.ToString();
                int newlineIndex = bufferText.IndexOf('\n');
                if (newlineIndex < 0) break;
                string line = bufferText.Substring(0, newlineIndex).Trim('\r', '\n');
                _rxBuffer.Remove(0, newlineIndex + 1);
                DataReceived?.Invoke(line);
                if (_pendingResponse != null) _pendingResponse.TrySetResult(line);
            }
        }
    }

    public void Dispose() {
        if (_disposed) return;
        Close();
        _disposed = true;
    }
}