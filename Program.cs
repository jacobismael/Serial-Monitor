using System;
using System.IO.Ports;

class Program {
    static async Task Main() {
        string[] ports = SerialPort.GetPortNames();
        string port = "/dev/cu.usbmodem1103";
        int baudRate = 9600;

        using SerialMonitor serial = new SerialMonitor(port, baudRate);
        serial.Open();

        Console.WriteLine("Type text and press Enter to send.");
        Console.WriteLine("Type exit to quit.\n");

        while(true) {
            string? input = Console.ReadLine();
            if(input == null) continue;
            if(input.ToLower().Equals("exit")) break;
            string response = await serial.WriteLine(input);
            if (response == "")
                 Console.WriteLine("No response received.");
            else
                Console.WriteLine(response);
        }
    }
}