using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PSControllerUI
{
    public class HidEngine : IDisposable
    {
        private const ushort VendorID = 0x054C;
        private const ushort ProductID = 0x0268;

        // Win32 API Constants
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        private const int DIGCF_PRESENT = 0x00000002;
        private const int DIGCF_DEVICEINTERFACE = 0x00000010;

        // Structs for P/Invoke
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        // P/Invoke Signatures
        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, out int RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetProductString(SafeFileHandle HidDeviceObject, byte[] Buffer, int BufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetManufacturerString(SafeFileHandle HidDeviceObject, byte[] Buffer, int BufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetOutputReport(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        // Fields
        private SafeFileHandle? _deviceHandle;
        private FileStream? _fileStream;
        private Thread? _readThread;
        private bool _isReading;
        private byte[] _outputReportBuffer = new byte[36];
        private byte[] _readBuffer0 = new byte[64];
        private byte[] _readBuffer1 = new byte[64];
        private int _activeBuffer = 0;
        private int _packetLogCount = 0;
        private ushort _connectedVid;
        private ushort _connectedPid;
        private HidReportMap? _reportMap;
        private bool _reportMapResolved;

        // Events
        public event Action<bool>? ConnectionStatusChanged;
        public event Action<byte[], int>? ReportReceived;
        public event Action<string>? LogMessage;

        public bool IsConnected => _deviceHandle != null && !_deviceHandle.IsInvalid;
        public bool IsOfficialSony => _connectedVid == 0x054C && _connectedPid == 0x0268;
        public HidReportMap? ReportMap => _reportMap;

        public HidEngine()
        {
            // Set up default output report buffer template (for LEDs and Rumble)
            _outputReportBuffer[0] = 0x01; // Report ID
            _outputReportBuffer[2] = 0xFF; // Right motor duration (forever)
            _outputReportBuffer[4] = 0xFF; // Left motor duration (forever)
            _outputReportBuffer[10] = 0x02; // Default to LED 1

            // Default settings for the 4 LEDs (time_enabled = 0xff, duty_length = 0xff, enabled = 1, duty_off = 0, duty_on = 0xff)
            for (int i = 0; i < 4; i++)
            {
                int baseIndex = 11 + (i * 5);
                _outputReportBuffer[baseIndex] = 0xFF;     // time_enabled
                _outputReportBuffer[baseIndex + 1] = 0xFF; // duty_length
                _outputReportBuffer[baseIndex + 2] = 0x01; // enabled
                _outputReportBuffer[baseIndex + 3] = 0x00; // duty_off
                _outputReportBuffer[baseIndex + 4] = 0xFF; // duty_on
            }
        }

        public bool TryConnect()
        {
            Disconnect();
            _packetLogCount = 0;

            Log("Searching for DualShock 3 controller...");
            string? devicePath = FindDevicePath();

            if (string.IsNullOrEmpty(devicePath))
            {
                Log("DualShock 3 controller not found.");
                return false;
            }

            Log($"Found controller at: {devicePath}");
            Log("Opening device handle...");

            // Open device with overlapped flag for async reading support
            _deviceHandle = CreateFile(
                devicePath!,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (_deviceHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                Log($"Failed to open device handle. Win32 Error: {error}. (Ensure no other application has claimed the device.)");
                _deviceHandle = null;
                return false;
            }

            Log("Controller opened. Initializing handshake...");

            // Parse HID Report Descriptor to learn the exact report layout
            _reportMap = HidReportMap.Parse(_deviceHandle, msg => Log(msg));
            _reportMapResolved = false;
            if (_reportMap != null && _reportMap.IsValid)
            {
                Log($"HID Report Descriptor parsed successfully. Auto-mapping enabled.");
            }
            else
            {
                Log($"HID Report Descriptor parsing failed or incomplete. Will use fallback mapping.");
            }

            // Send wake-up sequence (required for official DualShock 3 controllers)
            if (!SendHandshake())
            {
                Log("Handshake failed or not supported (common for third-party controllers). Proceeding with connection...");
            }
            else
            {
                Log("Handshake succeeded! Device is awake.");
            }

            // Start reading inputs
            _fileStream = new FileStream(_deviceHandle, FileAccess.ReadWrite, 64, true);
            StartReading();

            // Set default LED 1
            SetLed(1);

            ConnectionStatusChanged?.Invoke(true);
            return true;
        }

        private string? FindDevicePath()
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr infoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (infoSet == IntPtr.Zero) return null;

            try
            {
                SP_DEVICE_INTERFACE_DATA interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf(interfaceData);

                int index = 0;
                while (SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int requiredSize = 0;
                    SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);

                    if (requiredSize > 0)
                    {
                        int cbSize = IntPtr.Size == 8 ? 8 : 6;
                        byte[] detailBuffer = new byte[requiredSize];
                        BitConverter.GetBytes(cbSize).CopyTo(detailBuffer, 0);

                        GCHandle pinHandle = GCHandle.Alloc(detailBuffer, GCHandleType.Pinned);
                        try
                        {
                            IntPtr ptr = pinHandle.AddrOfPinnedObject();
                            if (SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, ptr, requiredSize, out _, IntPtr.Zero))
                            {
                                string path = Marshal.PtrToStringUni(new IntPtr(ptr.ToInt64() + 4)) ?? "";

                                // Open device temporarily to read attributes
                                using (SafeFileHandle tempHandle = CreateFile(
                                    path,
                                    0, // Query access
                                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                                    IntPtr.Zero,
                                    OPEN_EXISTING,
                                    0,
                                    IntPtr.Zero))
                                {
                                    if (!tempHandle.IsInvalid)
                                    {
                                        HIDD_ATTRIBUTES attrs = new HIDD_ATTRIBUTES();
                                        attrs.Size = Marshal.SizeOf(attrs);

                                        if (HidD_GetAttributes(tempHandle, ref attrs))
                                        {
                                            if (IsPS3Controller(tempHandle, attrs.VendorID, attrs.ProductID))
                                            {
                                                _connectedVid = attrs.VendorID;
                                                _connectedPid = attrs.ProductID;
                                                Log($"Device identified: VID=0x{attrs.VendorID:X4}, PID=0x{attrs.ProductID:X4}, Official Sony: {(_connectedVid == 0x054C && _connectedPid == 0x0268)}");
                                                return path; // Match found!
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            pinHandle.Free();
                        }
                    }
                    index++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }

            return null;
        }

        private bool IsPS3Controller(SafeFileHandle handle, ushort vid, ushort pid)
        {
            // 1. Check standard Sony VID/PID
            if (vid == 0x054C && pid == 0x0268)
                return true;

            // 2. Check common clone VID/PIDs
            // Gasia clone: 0x0925, 0x0005
            // Shanwan / PanHai / other clones: 0x2563, 0x0575
            // Zeroplus / JMTek clone: 0x0C12, 0x0E16
            if (vid == 0x0925 && pid == 0x0005)
                return true;
            if (vid == 0x2563 && pid == 0x0575)
                return true;
            if (vid == 0x0C12 && pid == 0x0E16)
                return true;

            // 3. Query product / manufacturer strings for keywords
            string product = GetDeviceString(handle, HidD_GetProductString).ToLowerInvariant();
            string manufacturer = GetDeviceString(handle, HidD_GetManufacturerString).ToLowerInvariant();

            if (product.Contains("playstation") || product.Contains("ps3") || product.Contains("dualshock") ||
                product.Contains("gasia") || product.Contains("shanwan") || product.Contains("panhai") ||
                manufacturer.Contains("sony") || manufacturer.Contains("gasia") || manufacturer.Contains("shanwan"))
            {
                return true;
            }

            return false;
        }

        private delegate bool GetStringDelegate(SafeFileHandle handle, byte[] buffer, int bufferLength);

        private string GetDeviceString(SafeFileHandle handle, GetStringDelegate getStringFunc)
        {
            byte[] buffer = new byte[256];
            try
            {
                if (getStringFunc(handle, buffer, buffer.Length))
                {
                    return System.Text.Encoding.Unicode.GetString(buffer).Trim('\0');
                }
            }
            catch
            {
                // Ignore errors
            }
            return "";
        }

        private bool SendHandshake()
        {
            // DualShock 3 initialization Feature Report 0xF4
            byte[] handshake = new byte[5];
            handshake[0] = 0xF4; // Report ID
            handshake[1] = 0x42;
            handshake[2] = 0x03;
            handshake[3] = 0x00;
            handshake[4] = 0x00;

            bool result = HidD_SetFeature(_deviceHandle!, handshake, handshake.Length);
            if (!result)
            {
                int error = Marshal.GetLastWin32Error();
                Log($"Feature report 0xF4 failed. Win32 Error: {error}");
            }
            return result;
        }

        private void StartReading()
        {
            _isReading = true;
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "PS3HIDReadThread"
            };
            _readThread.Start();
        }

        private async void ReadLoop()
        {
            while (_isReading && _fileStream != null && _fileStream.CanRead)
            {
                try
                {
                    byte[] buffer = _activeBuffer == 0 ? _readBuffer0 : _readBuffer1;
                    _activeBuffer ^= 1;
                    int bytesRead = await _fileStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        if (_packetLogCount < 10)
                        {
                            _packetLogCount++;
                            string hex = BitConverter.ToString(buffer, 0, bytesRead);
                            Log($"Diagnostic Packet {_packetLogCount} (Len={bytesRead}): {hex}");
                        }

                        // Resolve report map offsets from first packet
                        if (!_reportMapResolved && _reportMap != null && _reportMap.IsValid)
                        {
                            _reportMap.ResolveOffsetsFromPacket(buffer, bytesRead, msg => Log(msg));
                            _reportMapResolved = true;
                        }

                        ReportReceived?.Invoke(buffer, bytesRead);
                    }
                    else
                    {
                        Log("Device read stream ended.");
                        _isReading = false;
                        _ = Task.Run(() => Disconnect());
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Read error: {ex.Message}");
                    _isReading = false;
                    _ = Task.Run(() => Disconnect());
                    break;
                }
            }
        }

        public void SetRumble(byte leftStrength, bool rightOn)
        {
            if (!IsConnected) return;

            // Update output report buffer
            _outputReportBuffer[3] = (byte)(rightOn ? 0x01 : 0x00); // High-frequency right motor (on/off)
            _outputReportBuffer[5] = leftStrength;                  // Low-frequency left motor (variable)

            SendOutputReport();
        }

        public void SetLed(int playerNum)
        {
            if (!IsConnected) return;

            // Map player number to leds bitmap byte at offset 10:
            // P1 = 0x02, P2 = 0x04, P3 = 0x08, P4 = 0x10
            byte bitmap = 0x00;
            if (playerNum == 1) bitmap = 0x02;
            else if (playerNum == 2) bitmap = 0x04;
            else if (playerNum == 3) bitmap = 0x08;
            else if (playerNum == 4) bitmap = 0x10;

            _outputReportBuffer[10] = bitmap;
            SendOutputReport();
        }

        private void SendOutputReport()
        {
            try
            {
                bool result = HidD_SetOutputReport(_deviceHandle!, _outputReportBuffer, _outputReportBuffer.Length);
                if (!result)
                {
                    int error = Marshal.GetLastWin32Error();
                    Log($"Failed to write output report. Win32 Error: {error}");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to send output report: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            _isReading = false;

            if (_fileStream != null)
            {
                try { _fileStream.Close(); } catch { }
                _fileStream = null;
            }

            if (_deviceHandle != null)
            {
                try { _deviceHandle.Close(); } catch { }
                _deviceHandle = null;
            }

            Log("Disconnected.");
            ConnectionStatusChanged?.Invoke(false);
        }

        private void Log(string message)
        {
            LogMessage?.Invoke($"[HID] {message}");
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
