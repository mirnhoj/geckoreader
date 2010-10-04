using System;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Runtime.Remoting;
using System.Collections.Generic;
using ComponentAce.Compression.Libs.zlib;

namespace GeckoReader
{
    class Program
    {
        private const int WIILOAD_VERSION_MAYOR = 0;
        private const int WIILOAD_VERSION_MINOR = 5;

        private const string BinaryData = "\x1b[2B]";
        private const string ClearScreen = "\x1b[2J]";

        public enum FtStatus
        {
            Ok = 0,
            InvalidHandle = 1,
            DeviceNotFound = 2,
            DeviceNotOpened = 3,
            IoError = 4,
            InsufficientResources = 5,
            InvalidParameter = 6,
            InvalidBaudRate = 7,
            DeviceNotOpenedForErase = 8,
            DeviceNotOpenedForWrite = 9,
            FailedToWriteDevice = 10,
            EepromReadFailed = 11,
            EepromWriteFailed = 12,
            EepromEraseFailed = 13,
            EepromNotPresent = 14,
            EepromNotProgrammed = 15,
            InvalidArgs = 16,
            NotSupported = 17,
            OtherError = 18
        }

        [DllImport("ftd2xx.dll")]
        extern static FtStatus FT_Open(int deviceNumber, out IntPtr handle);
        
        [DllImport("ftd2xx.dll")]
        extern static FtStatus FT_GetComPortNumber(IntPtr handle, out int portnumber);

        [DllImport("ftd2xx.dll")]
        extern static FtStatus FT_Close(IntPtr handle);

        private static bool paused;
        private static bool logTime;
        private static StreamWriter saveToFile;
        private static bool newLineForF00ls;
        private static string currentSendFile;
        private static string currentArguments;
        private static string lastSendFile;
        private static string lastSendArguments;
//        private static bool showBootmiiOutput;
//        private static bool inBootmii;

        private static readonly string seperator = new string('-', 79) + "\r\n";
        private static SerialPort serialPort;
        private static ArgumentsMessage argumentsMessage;

        private class ArgumentsMessage : MarshalByRefObject
        {
            public delegate void OnArgumentsReceiveHandler(string[] args);

            public event OnArgumentsReceiveHandler OnArgumentsReceive;

            public void SendArguments(string[] args)
            {
                if (OnArgumentsReceive != null)
                {
                    OnArgumentsReceive(args);
                }
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool isFirstInstance;
            using (Mutex mutex = new Mutex(true, "GeckoReader", out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    SendArguments(args);
                    return;
                }

                Console.WriteLine("GeckoReader by r-win\n");
                Console.WriteLine("This tool will read the data send by the USB Gecko device to your pc");
                Console.WriteLine("and show the output in this console window.");
                Console.WriteLine();
                Console.TreatControlCAsInput = true;

                IntPtr ptr = Process.GetCurrentProcess().MainWindowHandle;
                try
                {
                    bool read_port = false;
                    int port = 0;

                    try
                    {
                        IntPtr handle;
                        FtStatus retval = FT_Open(0, out handle);
                        if (retval != FtStatus.Ok)
                        {
                            read_port = true; // throw new Exception("Cannot start the driver needed for USB Gecko");
                        }

                        if (!read_port)
                        {
                            retval = FT_GetComPortNumber(handle, out port);
                            if (retval != FtStatus.Ok)
                            {
                                read_port = true; // throw new Exception("Cannot get com port");
                            }

                            if (handle != IntPtr.Zero)
                            {
                                FT_Close(handle);
                            }
                        }
                    }
                    catch (EntryPointNotFoundException)
                    {
                        read_port = true;
                    }

                    if (read_port)
                    {
                        port = ReadPort();
                    }

                    using (serialPort = new SerialPort(string.Format("COM{0}", port), 115200, Parity.None, 8, StopBits.One))
                    {
                        serialPort.Open();

                        // Create IPC Server
                        BinaryServerFormatterSinkProvider serverProv = new BinaryServerFormatterSinkProvider();
                        serverProv.TypeFilterLevel = System.Runtime.Serialization.Formatters.TypeFilterLevel.Full;
                        BinaryClientFormatterSinkProvider clientProv = new BinaryClientFormatterSinkProvider();

                        Dictionary<string, string> properties = new Dictionary<string,string>();
                        properties["portName"] = "GeckoReader";
                        IpcChannel ipc = new IpcChannel(properties, clientProv, serverProv);
                        ChannelServices.RegisterChannel(ipc, false);
                        RemotingConfiguration.RegisterWellKnownServiceType(typeof(ArgumentsMessage), "GeckoReader.rem", WellKnownObjectMode.Singleton);

                        argumentsMessage = (ArgumentsMessage)Activator.GetObject(typeof(ArgumentsMessage), "ipc://GeckoReader/GeckoReader.rem");
                        argumentsMessage.OnArgumentsReceive += ReceiveArguments;

                        if (args.Length > 0)
                        {
                            // Sending dol file...
                            ReceiveArguments(args);
                        }

                        Console.WriteLine("Current setup is using COM{0}", port);
                        Console.WriteLine("* Press p to pause");
                        Console.WriteLine("* Press s to send a file");
                        Console.WriteLine("* Press t to log the time");
                        Console.WriteLine("* Press f to save the log to a file");
//                        Console.WriteLine("* Press b to toogle BootMii output (default blocked)");
                        Console.WriteLine("* Press Ctrl-C to stop.");
                        Console.WriteLine();

                        while (true)
                        {
                            if (Console.KeyAvailable)
                            {
                                ConsoleKeyInfo key = Console.ReadKey(true);
                                if (key.Key == ConsoleKey.C && key.Modifiers == ConsoleModifiers.Control)
                                {
                                    break;
                                }

                                switch (key.KeyChar)
                                {
                                    case 'p':
                                        if (!paused)
                                        {
                                            paused = true;
                                            LogLine(seperator);
                                            LogLine("Output is paused. Press r to resume, d to discard the input buffer, or c to clear the screen.\r\n");
                                            LogLine(seperator);
                                        }
                                        break;
                                    case 'r':
                                        paused = false;
                                        break;
                                    case 'c':
                                        LogLine(seperator);
                                        LogLine("Console window is cleared\r\n");
                                        LogLine(seperator);
                                        Console.Clear();
                                        break;
//                                  case 'b':
//                                      showBootmiiOutput = !showBootmiiOutput;
//                                      break;
                                    case 'S':
                                        if (!string.IsNullOrEmpty(lastSendFile))
                                        {
                                            LogLine(seperator);
                                            if (File.Exists(lastSendFile))
                                            {
                                                LogLine(string.Format("Resending file: '{0}'\r\n", lastSendFile));
                                                SendFile(serialPort, lastSendFile, lastSendArguments);
                                                LogLine("\nFile sent successfully, press S to send the file again.\r\n");
                                            }
                                            else
                                            {
                                                LogLine(string.Format("Cannot resend file: '{0}', the file is not found.\r\n", lastSendFile));
                                            }
                                            LogLine(seperator);
                                        }
                                        break;
                                    case 's':
                                        OpenFileDialog ofd = new OpenFileDialog { Filter = "DOL/ELF Files|*.elf;*.dol|Zip Files|*.zip|All Files|*.*", Multiselect = false, Title = "Select a file..." };
                                        if (ofd.ShowDialog() == DialogResult.OK)
                                        {
                                            Console.Write("Enter arguments: ");
                                            currentArguments = Console.ReadLine();
                                            currentSendFile = ofd.FileName;
                                        }
                                        break;
                                    case 'd':
                                        serialPort.DiscardInBuffer();
                                        LogLine(seperator);
                                        LogLine("The content of the buffer is discarded.\r\n");
                                        LogLine(seperator);
                                        break;
                                    case 't':
                                        logTime = true;
                                        break;
                                    case 'f':
                                        if (saveToFile != null)
                                        {
                                            saveToFile.Close();
                                            saveToFile = null;
                                            LogLine(seperator);
                                            LogLine("Stopped saving output to file.\r\n");
                                            LogLine(seperator);
                                        }
                                        else
                                        {
                                            SaveFileDialog sd = new SaveFileDialog { Filter = "Log files|*.txt;*.log" };
                                            if (sd.ShowDialog() == DialogResult.OK)
                                            {
                                                LogLine(seperator);
                                                LogLine(string.Format("Start logging to file '{0}'\r\n", sd.FileName));
                                                LogLine(seperator);
                                                saveToFile = new StreamWriter(sd.FileName, false);
                                            }
                                            else
                                            {
                                                LogLine(seperator);
                                                LogLine("Logging to file cancelled\r\n");
                                                LogLine(seperator);
                                            }
                                        }
                                        break;
                                }
                            }

                            if (!string.IsNullOrEmpty(currentSendFile))
                            {
                                LogLine(seperator);
                                LogLine(string.Format("Start sending file: '{0}'\r\n", currentSendFile));
                                SendFile(serialPort, currentSendFile, currentArguments);
                                LogLine("\nFile sent successfully, press shift+s to send the file again.\r\n");
                                LogLine(seperator);

                                currentSendFile = string.Empty;
                            }

                            if (serialPort.BytesToRead == 0 || paused)
                            {
                                Thread.Sleep(100);
                            }
                            else
                            {
                                // Search for control characters
                                if (serialPort.BytesToRead > 0)
                                {
                                    string line = serialPort.ReadExisting();
                                    if (line.Contains(ClearScreen))
                                    {
                                        string[] splitted = line.Split(new[] { ClearScreen }, StringSplitOptions.None);
                                        LogLine(seperator);
                                        LogLine("Console window cleared by application.\r\n");
                                        LogLine(seperator);
                                        Console.Clear();
                                        line = splitted[splitted.Length - 1];
                                    }
                                    if (line.EndsWith(BinaryData))
                                    {
                                        LogLine(line.Replace(BinaryData, string.Empty));
                                        ReceiveBinaryData(serialPort);
                                        continue;
                                    }
                                    LogLine(line);
                                }
                            }
                        }
                        serialPort.Close();
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine(ex);
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                }
                catch (DllNotFoundException)
                {
                    Console.WriteLine("Cannot find the USB Gecko driver. Did you install your Gecko correctly?");
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error occurred: {0}", ex.Message);
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                }
            }
        }

        private static void SendArguments(string[] args)
        {
            IpcChannel ipc = new IpcChannel("GeckoReader - Sender");
            ChannelServices.RegisterChannel(ipc, false);

            ArgumentsMessage argumentsMessage = (ArgumentsMessage)Activator.GetObject(typeof(ArgumentsMessage), "ipc://GeckoReader/GeckoReader.rem");
            argumentsMessage.SendArguments(args);
            ChannelServices.UnregisterChannel(ipc);
        }

        private static void ReceiveArguments(string[] args)
        {
            string[] arguments = new string[args.Length - 1];
            Array.Copy(args, 1, arguments, 0, arguments.Length);
//            SendFile(serialPort, args[0], string.Join(" ", arguments));

            currentArguments = string.Join(" ", arguments);
            currentSendFile = args[0];
        }

        private static void ReceiveBinaryData(SerialPort serialPort)
        {
            // Starting binary transfer
            LogLine(seperator);
            LogLine("Application started a binary transfer\n");

            // Get the length of the transfer
            byte[] buffer = new byte[4];
            serialPort.Read(buffer, 0, 4);
            int length = buffer[0] << 24 | buffer[1] << 16 | buffer[2] << 8 | buffer[3];
            int filenameLength = serialPort.ReadByte();

            byte[] file = new byte[length];

            // Read bytes in blocks of 2k
            const int blockLength = 2048;
            int bytesRead = 0;
            ReportProgress(0, length);
            while (length > bytesRead)
            {
                int bytesToRead = length - bytesRead > blockLength ? blockLength : length - bytesRead;
                bytesRead += serialPort.Read(file, bytesRead, bytesToRead);
                ReportProgress(bytesRead, length + filenameLength);
            }

            string filename = string.Empty;
            if (filenameLength > 0)
            {
                byte[] fBytes = new byte[filenameLength];
                serialPort.Read(fBytes, 0, filenameLength);
                ReportProgress(bytesRead + filenameLength, length + filenameLength);

                filename = Encoding.ASCII.GetString(fBytes);
            }

            string newFilename = GetFilename(filename);
            LogLine(string.Format("File saved as '{0}'", newFilename));
            using (Stream fs = new FileStream(newFilename, FileMode.Create))
            {
                fs.Write(file, 0, file.Length);
            }
            LogLine(seperator);
        }

        private static string GetFilename(string filename)
        {
            if (!File.Exists(filename))
            {
                return filename;
            }

            string fname = Path.GetFileNameWithoutExtension(filename);
            string ext = Path.GetExtension(filename);
            int index = 1;

            string fileToCheck;
            do
            {
                fileToCheck = string.Format("{0} ({1}){2}", fname, index++, ext);
            } while (File.Exists(fileToCheck));

            return fileToCheck;    
        }

        private static void ReportProgress(int part, int total)
        {
            // Reserve 50 pixels for the total
            Console.CursorLeft = 0;
            double percentage = part == 0 ? 0 : (part*1.0)/total;
            int dots = (int) Math.Round(50*percentage);
            Console.Write("[");
            if (dots > 0)
                Console.Write(new string('.', dots));
            if (dots < 50)
                Console.Write(new string(' ', 50 - dots));
            Console.Write("] {0} of {1}", part, total);
        }

        private static void LogLine(string line)
        {
            string[] splitted = line.Split(new[] {"\r\n"}, StringSplitOptions.None);
            for (int i = 0; i < splitted.Length; i++)
            {
                if (i == splitted.Length - 1 && splitted[i].Length == 0) continue;
                if (logTime && !seperator.StartsWith(splitted[i]))
                {
                    splitted[i] = string.Format("{0}: {1}", DateTime.Now.ToString("HH:mm:ss"), splitted[i]);
                }
                else if (newLineForF00ls) // This line is a seperator line
                {
                    splitted[i] = string.Format("\r\n{0}", splitted[i]);
                    newLineForF00ls = false;
                }

/*
                if (!showBootmiiOutput)
                {
                    // First, check if this is the start of Bootmii output
                    if (!inBootmii && splitted[i].StartsWith("\r\nBootMii v"))
                    {
                        inBootmii = true;
                        continue;
                    }
                    if (inBootmii && splitted[i].StartsWith("Starting boot2..."))
                    {
                        inBootmii = false;
                        continue;
                    }
                    if (inBootmii)
                    {
                        continue;
                    }
                }
*/
            }

            line = string.Join("\r\n", splitted);
            newLineForF00ls = !line.EndsWith(Environment.NewLine);
            Console.Write(line);
            if (saveToFile != null)
            {
                saveToFile.Write(line);
            }
        }

        private static void SendFile(SerialPort serialPort, string fileName, string arguments)
        {
	        byte[] buf = new byte[16];
            Array.Copy(Encoding.ASCII.GetBytes("HAXX"), buf, 4);
	        buf[4] = WIILOAD_VERSION_MAYOR;
	        buf[5] = WIILOAD_VERSION_MINOR;
            buf[6] = (byte) (((arguments.Length + 1) >> 8) & 0xFF);
            buf[7] = (byte) ((arguments.Length + 1) & 0xFF);
            
            // Read file content
            byte[] buffer;
            using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                buffer = new byte[fs.Length];
                fs.Read(buffer, 0, buffer.Length);
            }

            uint fSize = EndianFlip((uint)buffer.Length);
            bool compress = !Compare(buffer, "PK\x03\x04", 4);
            if (compress)
            {
                byte []compressedFile = CompressFile(buffer);
                if (compressedFile.Length < buffer.Length)
                {
                    buffer = compressedFile;
                }
                else
                {
                    compress = false;
                }
            }
            uint cSize = EndianFlip((uint)buffer.Length);

            int amountOfBytesToSend = 16 + buffer.Length + ((arguments.Length <= 0) ? 1 : arguments.Length);
            ReportProgress(0, amountOfBytesToSend);

            if (compress)
            {
                Array.Copy(BitConverter.GetBytes(cSize), 0, buf, 8, 4);
                Array.Copy(BitConverter.GetBytes(fSize), 0, buf, 12, 4);
            }
            else
            {
                Array.Copy(BitConverter.GetBytes(fSize), 0, buf, 8, 4);
            }
            serialPort.Write(buf, 0, 16);

            ReportProgress(16, amountOfBytesToSend);

            const int blockSize = 2048;
            int bytesWritten = 0;
            while (bytesWritten < buffer.Length)
            {
                int bytesToWrite = buffer.Length - bytesWritten > blockSize ? blockSize : buffer.Length - bytesWritten;
                serialPort.Write(buffer, bytesWritten, bytesToWrite);
                bytesWritten += bytesToWrite;
                ReportProgress(16 + bytesWritten, amountOfBytesToSend);
            }

            lastSendFile = fileName;
            lastSendArguments = arguments;

            if (arguments.Length <= 0)
            {
                serialPort.Write(new[] {'0'}, 0, 1);
                ReportProgress(16 + bytesWritten + 1, amountOfBytesToSend);
                return;
            }
            byte[] a = Encoding.ASCII.GetBytes(arguments.Replace(' ', '\0'));
            serialPort.Write(a, 0, a.Length);
            ReportProgress(bytesWritten + a.Length, amountOfBytesToSend);
            serialPort.Write(new[] { '\0' }, 0, 1);
            ReportProgress(16 + bytesWritten + a.Length + 1, amountOfBytesToSend);
        }

        private static void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[2000];
            int len;
            while ((len = input.Read(buffer, 0, 2000)) > 0)
            {
                output.Write(buffer, 0, len);
            }
            output.Flush();
        }

        private static byte[] CompressFile(byte[] input)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (ZOutputStream oStream = new ZOutputStream(output, zlibConst.Z_BEST_SPEED))
                {
                    using (MemoryStream iStream = new MemoryStream(input))
                    {
                        CopyStream(iStream, oStream);
                    }
                }
                return output.ToArray();
            }
        }

        private static bool Compare(byte[] buffer, string str, int count)
        {
            string b = Encoding.ASCII.GetString(buffer, 0, count);
            return b.Equals(str);
        }

        private static uint EndianFlip(uint value)
        {
            return (
                     ((value & 0xFF000000) >> 24) |
                     ((value & 0x00FF0000) >> 8) |
                     ((value & 0x0000FF00) << 8) |
                     ((value & 0x000000FF) << 24));
        }

        private static int ReadPort()
        {
            string p = string.Empty;
            string wiiload = Environment.GetEnvironmentVariable("WIILOAD");
            if (!string.IsNullOrEmpty(wiiload) && wiiload.StartsWith("COM"))
            {
                p = wiiload.Substring(3);
                int port;
                if (Int32.TryParse(p, out port))
                {
                    return port;
                }
            }

            Console.WriteLine("Cannot determine your COM port automatically.");
            Console.Write("Please enter your COM port number: ");
            while (true)
            {
                ConsoleKeyInfo k = Console.ReadKey(true);
                if ((k.Key >= ConsoleKey.D0 && k.Key <= ConsoleKey.D9) ||
                    (k.Key >= ConsoleKey.NumPad0 && k.Key <= ConsoleKey.NumPad9))
                {
                    Console.Write(k.KeyChar);
                    p += k.KeyChar;
                }
                if (k.Key == ConsoleKey.Backspace)
                {
                    Console.Write(k.KeyChar);
                    p = p.Substring(0, p.Length - 1);
                }
                if (k.Key != ConsoleKey.Enter) continue;
                break;
            }
            Console.WriteLine();
            return Convert.ToInt32(p);
        }
    }
}
