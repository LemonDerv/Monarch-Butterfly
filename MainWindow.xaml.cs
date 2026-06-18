using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace PSControllerUI
{
    public partial class MainWindow : Window
    {
        private HidEngine _hidEngine;
        private ViGEmClientWrapper _vigemClient;
        private byte[]? _prevData;
        private bool _isConnecting;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private volatile bool _xboxEmulationEnabled;
        private volatile bool _kbdMouseMappingEnabled;

        private enum ModalType
        {
            ControllerMapping,
            KeyboardMapping,
            Haptics,
            Settings
        }
        private ModalType _currentModalType;
        private List<ComboBox> _activeMappingCombos = new List<ComboBox>();
        private ComboBox? _hapticsLedCombo;
        private Slider? _hapticsRumbleSlider;
        private Slider? _hapticsDeadzoneSlider;
        private int _playerLed = 1;

        // Settings checkboxes
        private CheckBox? _settingsShowNotifications;
        private CheckBox? _settingsMinimizeOnClose;
        private CheckBox? _settingsStartMinimized;
        private CheckBox? _settingsRunAtStartup;
        private bool _isExiting = false;

        // Uptime Tracking
        private DispatcherTimer _uptimeTimer;
        private Stopwatch _uptimeStopwatch;

        // System Resource Tracking Fields
        private ulong _prevIdleTime;
        private ulong _prevKernelTime;
        private ulong _prevUserTime;
        private readonly Random _random = new Random();

        // --- Performance: Throttling ---
        private long _lastVisualizerTick; // Stopwatch ticks for 30 FPS gate
        private static readonly long FrameIntervalTicks = Stopwatch.Frequency / 30; // ~33ms
        private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();

        // --- Performance: Cached gauge geometry objects ---
        private PathFigure? _cpuFigure; private ArcSegment? _cpuArc;
        private PathFigure? _ramFigure; private ArcSegment? _ramArc;
        private PathFigure? _lv2Figure; private ArcSegment? _lv2Arc;
        private PathFigure? _iopFigure; private ArcSegment? _iopArc;

        // --- Performance: Xbox report dedup ---
        private XUSB_REPORT _lastXboxReport;

        // System Resource P/Invokes
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(
            out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

        private static ulong FileTimeToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        }

        // Dynamic Keyboard Mappings
        private ushort _mapDpadUp = 0x26;    // Up Arrow
        private ushort _mapDpadDown = 0x28;  // Down Arrow
        private ushort _mapDpadLeft = 0x25;  // Left Arrow
        private ushort _mapDpadRight = 0x27; // Right Arrow
        private ushort _mapCross = 0x20;     // Space
        private ushort _mapCircle = 0xA2;    // Left Control
        private ushort _mapSquare = 0x52;    // R Key
        private ushort _mapTriangle = 0x09;  // Tab
        private ushort _mapStart = 0x0D;     // Enter
        private ushort _mapSelect = 0x1B;    // Escape
        private int _stickDeadzone = 15;     // Stick Deadzone

        // Dynamic Xbox 360 Mappings
        private ushort _mapXboxCross = (ushort)Xbox360Buttons.A;
        private ushort _mapXboxCircle = (ushort)Xbox360Buttons.B;
        private ushort _mapXboxSquare = (ushort)Xbox360Buttons.X;
        private ushort _mapXboxTriangle = (ushort)Xbox360Buttons.Y;
        private ushort _mapXboxL1 = (ushort)Xbox360Buttons.LeftShoulder;
        private ushort _mapXboxR1 = (ushort)Xbox360Buttons.RightShoulder;
        private ushort _mapXboxSelect = (ushort)Xbox360Buttons.Back;
        private ushort _mapXboxStart = (ushort)Xbox360Buttons.Start;
        private ushort _mapXboxPS = (ushort)Xbox360Buttons.Guide;

        // Key Databases for Dropdowns
        private static readonly Dictionary<string, ushort> AvailableKeys = new Dictionary<string, ushort>
        {
            { "Spacebar", 0x20 },
            { "Left Control (Ctrl)", 0xA2 },
            { "Left Shift", 0xA0 },
            { "Enter / Return", 0x0D },
            { "Escape (Esc)", 0x1B },
            { "Tab Key", 0x09 },
            { "Up Arrow", 0x26 },
            { "Down Arrow", 0x28 },
            { "Left Arrow", 0x25 },
            { "Right Arrow", 0x27 },
            { "A Key", 0x41 },
            { "B Key", 0x42 },
            { "C Key", 0x43 },
            { "D Key", 0x44 },
            { "E Key", 0x45 },
            { "F Key", 0x46 },
            { "G Key", 0x47 },
            { "Q Key", 0x51 },
            { "R Key", 0x52 },
            { "S Key", 0x53 },
            { "W Key", 0x57 },
            { "X Key", 0x58 },
            { "Y Key", 0x59 },
            { "Z Key", 0x5A },
            { "1 Key", 0x31 },
            { "2 Key", 0x32 },
            { "3 Key", 0x33 },
            { "4 Key", 0x34 }
        };

        private static readonly Dictionary<string, ushort> AvailableXboxButtons = new Dictionary<string, ushort>
        {
            { "A Button", (ushort)Xbox360Buttons.A },
            { "B Button", (ushort)Xbox360Buttons.B },
            { "X Button", (ushort)Xbox360Buttons.X },
            { "Y Button", (ushort)Xbox360Buttons.Y },
            { "Left Shoulder (LB)", (ushort)Xbox360Buttons.LeftShoulder },
            { "Right Shoulder (RB)", (ushort)Xbox360Buttons.RightShoulder },
            { "Back Button", (ushort)Xbox360Buttons.Back },
            { "Start Button", (ushort)Xbox360Buttons.Start },
            { "Xbox Guide Button", (ushort)Xbox360Buttons.Guide },
            { "Left Thumb Click", (ushort)Xbox360Buttons.LeftThumbClick },
            { "Right Thumb Click", (ushort)Xbox360Buttons.RightThumbClick }
        };

        // Colors for Concept 1 UI states (Frozen for thread-safety and reduced WPF overhead)
        private static readonly SolidColorBrush _activeBrush = CreateFrozenBrush(0x9B, 0x51, 0xE0);
        private static readonly SolidColorBrush _inactiveBrush = CreateFrozenBrush(0x23, 0x1A, 0x33);
        private static readonly SolidColorBrush _greenBrush = CreateFrozenBrush(0x00, 0xFF, 0x87);
        private static readonly SolidColorBrush _redBrush = CreateFrozenBrush(0xFF, 0x3B, 0x30);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public MainWindow()
        {
            InitializeComponent();

            _hidEngine = new HidEngine();
            _hidEngine.ConnectionStatusChanged += OnConnectionStatusChanged;
            _hidEngine.ReportReceived += OnReportReceived;
            _hidEngine.LogMessage += AddLog;

            _vigemClient = new ViGEmClientWrapper();
            if (!_vigemClient.IsSupported)
            {
                ViGEmWarningPanel.Visibility = Visibility.Visible;
                XboxEmulationToggle.IsEnabled = false;
                AddLog("[ViGEm] Driver not found on this system. Xbox 360 Emulation is disabled.");
            }
            else
            {
                AddLog("[ViGEm] Bus client loaded successfully.");
            }

            // Initialize system times baseline for CPU usage
            if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                _prevIdleTime = FileTimeToUInt64(idleTime);
                _prevKernelTime = FileTimeToUInt64(kernelTime);
                _prevUserTime = FileTimeToUInt64(userTime);
            }

            // Set up uptime timer
            _uptimeStopwatch = new Stopwatch();
            _uptimeStopwatch.Start(); // Start immediately for application uptime
            _uptimeTimer = new DispatcherTimer();
            _uptimeTimer.Interval = TimeSpan.FromSeconds(1);
            _uptimeTimer.Tick += UptimeTimer_Tick;
            _uptimeTimer.Start(); // Start immediately

            // Set app version log
            AddLog("[System] Application started. Ready for connection.");
            
            // Initialize visualizer
            ResetVisualizer();

            // Populate system resources immediately
            UpdateSystemResources();

            // Initialize tray icon
            InitializeNotifyIcon();

            // Bind Loaded event for start-minimized logic
            this.Loaded += Window_Loaded;

            // Initialize volatile toggle state flags
            _xboxEmulationEnabled = XboxEmulationToggle.IsChecked == true;
            _kbdMouseMappingEnabled = KbdMouseMappingToggle.IsChecked == true;
        }

        private void OnConnectionStatusChanged(bool connected)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (connected)
                {
                    QuickConnectText.Text = "Disconnect PS3";
                    
                    StatusPillDot.Fill = _greenBrush;
                    StatusPillText.Text = "Connected to USB";

                    AddLog("[System] Controller is active and sending inputs.");
                }
                else
                {
                    QuickConnectText.Text = "Attach to PS3";

                    StatusPillDot.Fill = _redBrush;
                    StatusPillText.Text = "Disconnected";

                    // Reset visualizer elements
                    ResetVisualizer();

                    // Disconnect emulation if active
                    if (XboxEmulationToggle.IsChecked == true)
                    {
                        _vigemClient.Disconnect();
                    }
                }
            }));
        }

        private void UptimeTimer_Tick(object? sender, EventArgs e)
        {
            TimeSpan elapsed = _uptimeStopwatch.Elapsed;
            UptimePillText.Text = string.Format("{0:00}:{1:00}:{2:00}", elapsed.Hours, elapsed.Minutes, elapsed.Seconds);

            // Update real-time PC system resources
            UpdateSystemResources();
        }

        private void UpdateSystemResources()
        {
            // 1. CPU Usage (using Win32 GetSystemTimes)
            double cpuUsage = 0;
            if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                ulong idle = FileTimeToUInt64(idleTime);
                ulong kernel = FileTimeToUInt64(kernelTime);
                ulong user = FileTimeToUInt64(userTime);

                ulong idleDiff = idle - _prevIdleTime;
                ulong kernelDiff = kernel - _prevKernelTime;
                ulong userDiff = user - _prevUserTime;

                _prevIdleTime = idle;
                _prevKernelTime = kernel;
                _prevUserTime = user;

                ulong totalDiff = kernelDiff + userDiff;
                if (totalDiff > 0)
                {
                    double cpu = 1.0 - ((double)idleDiff / totalDiff);
                    cpuUsage = Math.Clamp(cpu, 0.0, 1.0);
                }
            }

            // 2. RAM Usage & Pagefile Commit (LV2) Usage (using Win32 GlobalMemoryStatusEx)
            double ramUsage = 0;
            double vmemUsage = 0;
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                ramUsage = memStatus.dwMemoryLoad / 100.0;
                
                if (memStatus.ullTotalPageFile > 0)
                {
                    vmemUsage = 1.0 - ((double)memStatus.ullAvailPageFile / memStatus.ullTotalPageFile);
                }
            }

            // 3. IOP Usage (Simulated active system I/O load)
            double iopUsage = 0.04 + (_random.NextDouble() * 0.06) + (cpuUsage * 0.05);
            iopUsage = Math.Clamp(iopUsage, 0.0, 1.0);

            // Update circular gauges (reusing cached geometry objects)
            UpdateCircularGauge(CpuGaugePath, cpuUsage, 1.0, ref _cpuFigure, ref _cpuArc);
            UpdateCircularGauge(RamGaugePath, ramUsage, 1.0, ref _ramFigure, ref _ramArc);
            UpdateCircularGauge(Lv2GaugePath, vmemUsage, 1.0, ref _lv2Figure, ref _lv2Arc);
            UpdateCircularGauge(IopGaugePath, iopUsage, 1.0, ref _iopFigure, ref _iopArc);

            CpuText.Text = $"{(cpuUsage * 100):0}%";
            RamText.Text = $"{(ramUsage * 100):0}%";
            Lv2Text.Text = $"{(vmemUsage * 100):0}%";
            IopText.Text = $"{(iopUsage * 100):0}%";
        }

        private void ConnectButton_Click(object sender, MouseButtonEventArgs e)
        {
            // For the custom button in Quick Actions
            ToggleConnection();
        }

        private void ToggleConnection()
        {
            if (_isConnecting) return;

            if (_hidEngine.IsConnected)
            {
                _hidEngine.Disconnect();
            }
            else
            {
                _isConnecting = true;
                AddLog("[HID] Scanning USB devices...");
                
                // Try connecting in a separate task
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool result = _hidEngine.TryConnect();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isConnecting = false;
                        if (!result)
                        {
                            MessageBox.Show("Could not connect to DualShock 3 controller.\n\nMake sure it is plugged in via USB and no other driver (like DsHidMini) has exclusively claimed it.", "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }));
                });
            }
        }

        private void OnReportReceived(byte[] data, int length)
        {
            if (data == null || length < 7) return;

            // Route based on whether this is a genuine Sony DualShock 3 or a third-party clone.
            // Clones often send 64-byte reports with Report ID 0x01 mimicking DS3 format,
            // but with a completely different internal byte layout (generic HID/DirectInput).
            if (_hidEngine.IsOfficialSony && length >= 49 && data[0] == 0x01)
            {
                // --- Official Sony DualShock 3 report format ---
                bool inputChanged = _prevData == null || _prevData.Length < 49 ||
                    data[2] != _prevData[2] || data[3] != _prevData[3] || data[4] != _prevData[4] ||
                    data[6] != _prevData[6] || data[7] != _prevData[7] ||
                    data[8] != _prevData[8] || data[9] != _prevData[9] ||
                    data[18] != _prevData[18] || data[19] != _prevData[19];

                if (inputChanged)
                {
                    long now = _frameStopwatch.ElapsedTicks;
                    if (now - _lastVisualizerTick >= FrameIntervalTicks)
                    {
                        _lastVisualizerTick = now;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            UpdateVisualizer(data);
                        }), DispatcherPriority.Input);
                    }
                }

                if (inputChanged && _xboxEmulationEnabled && _vigemClient.IsSupported)
                {
                    UpdateXboxEmulation(data);
                }

                if (_kbdMouseMappingEnabled)
                {
                    UpdateKeyboardMouseMapping(data);
                }

                if (_prevData == null || _prevData.Length != data.Length)
                {
                    _prevData = new byte[data.Length];
                }
                Array.Copy(data, _prevData, data.Length);
            }
            else
            {
                // --- Generic HID / DirectInput clone controller ---
                ProcessGenericGamepadReport(data, length);
            }
        }

        private void ProcessGenericGamepadReport(byte[] data, int length)
        {
            if (data == null || length < 7) return;

            var map = _hidEngine.ReportMap;

            // --- Read Axes using auto-detected offsets ---
            byte lx, ly, rx, ry;
            if (map != null && map.IsValid && map.LeftStickX != null)
            {
                lx = map.LeftStickX.ReadByte(data, length, map.PreparsedData);
                ly = map.LeftStickY?.ReadByte(data, length, map.PreparsedData) ?? 128;
                rx = map.RightStickX?.ReadByte(data, length, map.PreparsedData) ?? 128;
                ry = map.RightStickY?.ReadByte(data, length, map.PreparsedData) ?? 128;
            }
            else
            {
                // Fallback: standard bytes 1-4
                lx = data[1]; ly = data[2]; rx = data[3]; ry = data[4];
            }

            double lXOffset = ((lx - 128.0) / 128.0) * 18.0;
            double lYOffset = ((ly - 128.0) / 128.0) * 18.0;
            double rXOffset = ((rx - 128.0) / 128.0) * 18.0;
            double rYOffset = ((ry - 128.0) / 128.0) * 18.0;

            // --- Read D-Pad (Hat Switch) ---
            bool dUp, dRight, dDown, dLeft;
            if (map?.HatSwitch != null)
            {
                map.ReadHatSwitch(data, length, out dUp, out dRight, out dDown, out dLeft);
            }
            else
            {
                byte hat = (byte)(data[5] & 0x0F);
                dUp = hat == 0 || hat == 1 || hat == 7;
                dRight = hat == 1 || hat == 2 || hat == 3;
                dDown = hat == 3 || hat == 4 || hat == 5;
                dLeft = hat == 5 || hat == 6 || hat == 7;
            }

            // --- Read Buttons using auto-detected offsets ---
            bool square, cross, circle, triangle;
            bool l1, r1, l2, r2;
            bool select, start, l3, r3, ps;

            if (map != null && map.IsValid && map.BtnCross != null)
            {
                square   = map.BtnSquare?.IsPressed(data, length, map.PreparsedData) ?? false;
                cross    = map.BtnCross?.IsPressed(data, length, map.PreparsedData) ?? false;
                circle   = map.BtnCircle?.IsPressed(data, length, map.PreparsedData) ?? false;
                triangle = map.BtnTriangle?.IsPressed(data, length, map.PreparsedData) ?? false;
                l1       = map.BtnL1?.IsPressed(data, length, map.PreparsedData) ?? false;
                r1       = map.BtnR1?.IsPressed(data, length, map.PreparsedData) ?? false;
                l2       = map.BtnL2?.IsPressed(data, length, map.PreparsedData) ?? false;
                r2       = map.BtnR2?.IsPressed(data, length, map.PreparsedData) ?? false;
                select   = map.BtnSelect?.IsPressed(data, length, map.PreparsedData) ?? false;
                start    = map.BtnStart?.IsPressed(data, length, map.PreparsedData) ?? false;
                l3       = map.BtnL3?.IsPressed(data, length, map.PreparsedData) ?? false;
                r3       = map.BtnR3?.IsPressed(data, length, map.PreparsedData) ?? false;
                ps       = map.BtnPS?.IsPressed(data, length, map.PreparsedData) ?? false;
            }
            else
            {
                // Fallback: hardcoded layout
                square   = (data[5] & 0x10) != 0;
                cross    = (data[5] & 0x20) != 0;
                circle   = (data[5] & 0x40) != 0;
                triangle = (data[5] & 0x80) != 0;
                l1       = (data[6] & 0x01) != 0;
                r1       = (data[6] & 0x02) != 0;
                l2       = (data[6] & 0x04) != 0;
                r2       = (data[6] & 0x08) != 0;
                select   = (data[6] & 0x10) != 0;
                start    = (data[6] & 0x20) != 0;
                l3       = (data[6] & 0x40) != 0;
                r3       = (data[6] & 0x80) != 0;
                ps       = length >= 8 && (data[7] & 0x01) != 0;
            }
            // Read analog trigger values if available; fallback to digital buttons
            byte l2Analog = map?.LeftTrigger?.ReadByte(data, length, map.PreparsedData) ?? (byte)(l2 ? 255 : 0);
            byte r2Analog = map?.RightTrigger?.ReadByte(data, length, map.PreparsedData) ?? (byte)(r2 ? 255 : 0);

            // Perform UI update via Dispatcher
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LeftStickTranslate.X = lXOffset;
                LeftStickTranslate.Y = lYOffset;
                RightStickTranslate.X = rXOffset;
                RightStickTranslate.Y = rYOffset;

                L2TriggerGaugeFill.Height = (l2Analog / 255.0) * 35.0;
                R2TriggerGaugeFill.Height = (r2Analog / 255.0) * 35.0;

                DpadUpVisual.Fill = dUp ? _activeBrush : Brushes.Transparent;
                DpadDownVisual.Fill = dDown ? _activeBrush : Brushes.Transparent;
                DpadLeftVisual.Fill = dLeft ? _activeBrush : Brushes.Transparent;
                DpadRightVisual.Fill = dRight ? _activeBrush : Brushes.Transparent;

                L1BumperVisual.Fill = l1 ? _activeBrush : _inactiveBrush;
                R1BumperVisual.Fill = r1 ? _activeBrush : _inactiveBrush;

                BtnTriangle.Fill = triangle ? _activeBrush : _inactiveBrush;
                BtnCircle.Fill = circle ? _activeBrush : _inactiveBrush;
                BtnCross.Fill = cross ? _activeBrush : _inactiveBrush;
                BtnSquare.Fill = square ? _activeBrush : _inactiveBrush;

                BtnSelect.Fill = select ? _activeBrush : _inactiveBrush;
                BtnStart.Fill = start ? _activeBrush : _inactiveBrush;
                BtnPS.Fill = ps ? _activeBrush : _inactiveBrush;
            }), DispatcherPriority.Input);

            // Also feed to emulation if active
            if (_xboxEmulationEnabled && _vigemClient.IsSupported)
            {
                UpdateXboxEmulationGeneric(lx, ly, rx, ry, cross, circle, square, triangle, l1, r1, l2Analog, r2Analog, select, start, l3, r3, ps, dUp, dDown, dLeft, dRight);
            }
        }

        private void UpdateXboxEmulationGeneric(
            byte lx, byte ly, byte rx, byte ry,
            bool cross, bool circle, bool square, bool triangle,
            bool l1, bool r1, byte l2Analog, byte r2Analog,
            bool select, bool start, bool l3, bool r3, bool ps,
            bool dUp, bool dDown, bool dLeft, bool dRight)
        {
            XUSB_REPORT report = new XUSB_REPORT();

            ushort buttons = 0;
            if (select) buttons |= _mapXboxSelect;
            if (l3) buttons |= (ushort)Xbox360Buttons.LeftThumbClick;
            if (r3) buttons |= (ushort)Xbox360Buttons.RightThumbClick;
            if (start) buttons |= _mapXboxStart;
            if (dUp) buttons |= (ushort)Xbox360Buttons.DpadUp;
            if (dRight) buttons |= (ushort)Xbox360Buttons.DpadRight;
            if (dDown) buttons |= (ushort)Xbox360Buttons.DpadDown;
            if (dLeft) buttons |= (ushort)Xbox360Buttons.DpadLeft;

            if (l1) buttons |= _mapXboxL1;
            if (r1) buttons |= _mapXboxR1;
            if (triangle) buttons |= _mapXboxTriangle;
            if (circle) buttons |= _mapXboxCircle;
            if (cross) buttons |= _mapXboxCross;
            if (square) buttons |= _mapXboxSquare;
            if (ps) buttons |= _mapXboxPS;

            report.wButtons = buttons;
            report.bLeftTrigger = l2Analog;
            report.bRightTrigger = r2Analog;

            report.sThumbLX = (short)((lx - 128) * 256);
            report.sThumbLY = (short)(-(ly - 128) * 256);
            report.sThumbRX = (short)((rx - 128) * 256);
            report.sThumbRY = (short)(-(ry - 128) * 256);

            if (report.wButtons != _lastXboxReport.wButtons ||
                report.bLeftTrigger != _lastXboxReport.bLeftTrigger ||
                report.bRightTrigger != _lastXboxReport.bRightTrigger ||
                report.sThumbLX != _lastXboxReport.sThumbLX ||
                report.sThumbLY != _lastXboxReport.sThumbLY ||
                report.sThumbRX != _lastXboxReport.sThumbRX ||
                report.sThumbRY != _lastXboxReport.sThumbRY)
            {
                _lastXboxReport = report;
                _vigemClient.Update(report);
            }
        }

        private void UpdateVisualizer(byte[] data)
        {
            // 1. Analog Sticks
            byte lx = data[6];
            byte ly = data[7];
            byte rx = data[8];
            byte ry = data[9];

            // Map sticks to visualizer (offset -18 to +18 pixels)
            double lXOffset = ((lx - 128.0) / 128.0) * 18.0;
            double lYOffset = ((ly - 128.0) / 128.0) * 18.0;
            double rXOffset = ((rx - 128.0) / 128.0) * 18.0;
            double rYOffset = ((ry - 128.0) / 128.0) * 18.0;

            LeftStickTranslate.X = lXOffset;
            LeftStickTranslate.Y = lYOffset;
            RightStickTranslate.X = rXOffset;
            RightStickTranslate.Y = rYOffset;

            // 2. Circular gauges (System Overview is handled independently via the system resources timer)

            // 3. Triggers (L2 / R2 pressure)
            byte l2Pressure = data[18];
            byte r2Pressure = data[19];

            L2TriggerGaugeFill.Height = (l2Pressure / 255.0) * 35.0;
            R2TriggerGaugeFill.Height = (r2Pressure / 255.0) * 35.0;

            // 4. Digital Buttons mapping
            bool select = (data[2] & 0x01) != 0;
            bool l3 = (data[2] & 0x02) != 0;
            bool r3 = (data[2] & 0x04) != 0;
            bool start = (data[2] & 0x08) != 0;
            bool dUp = (data[2] & 0x10) != 0;
            bool dRight = (data[2] & 0x20) != 0;
            bool dDown = (data[2] & 0x40) != 0;
            bool dLeft = (data[2] & 0x80) != 0;

            bool l2 = (data[3] & 0x01) != 0;
            bool r2 = (data[3] & 0x02) != 0;
            bool l1 = (data[3] & 0x04) != 0;
            bool r1 = (data[3] & 0x08) != 0;
            bool triangle = (data[3] & 0x10) != 0;
            bool circle = (data[3] & 0x20) != 0;
            bool cross = (data[3] & 0x40) != 0;
            bool square = (data[3] & 0x80) != 0;

            bool ps = (data[4] & 0x01) != 0;

            DpadUpVisual.Fill = dUp ? _activeBrush : Brushes.Transparent;
            DpadDownVisual.Fill = dDown ? _activeBrush : Brushes.Transparent;
            DpadLeftVisual.Fill = dLeft ? _activeBrush : Brushes.Transparent;
            DpadRightVisual.Fill = dRight ? _activeBrush : Brushes.Transparent;

            L1BumperVisual.Fill = l1 ? _activeBrush : _inactiveBrush;
            R1BumperVisual.Fill = r1 ? _activeBrush : _inactiveBrush;

            BtnTriangle.Fill = triangle ? _activeBrush : _inactiveBrush;
            BtnCircle.Fill = circle ? _activeBrush : _inactiveBrush;
            BtnCross.Fill = cross ? _activeBrush : _inactiveBrush;
            BtnSquare.Fill = square ? _activeBrush : _inactiveBrush;

            BtnSelect.Fill = select ? _activeBrush : _inactiveBrush;
            BtnStart.Fill = start ? _activeBrush : _inactiveBrush;
            BtnPS.Fill = ps ? _activeBrush : _inactiveBrush;
        }

        private void ResetVisualizer()
        {
            LeftStickTranslate.X = 0;
            LeftStickTranslate.Y = 0;
            RightStickTranslate.X = 0;
            RightStickTranslate.Y = 0;

            L2TriggerGaugeFill.Height = 0;
            R2TriggerGaugeFill.Height = 0;

            DpadUpVisual.Fill = Brushes.Transparent;
            DpadDownVisual.Fill = Brushes.Transparent;
            DpadLeftVisual.Fill = Brushes.Transparent;
            DpadRightVisual.Fill = Brushes.Transparent;

            L1BumperVisual.Fill = _inactiveBrush;
            R1BumperVisual.Fill = _inactiveBrush;

            BtnTriangle.Fill = _inactiveBrush;
            BtnCircle.Fill = _inactiveBrush;
            BtnCross.Fill = _inactiveBrush;
            BtnSquare.Fill = _inactiveBrush;

            BtnSelect.Fill = _inactiveBrush;
            BtnStart.Fill = _inactiveBrush;
            BtnPS.Fill = _inactiveBrush;

            // Reset circular gauges (system overview remains active via the timer)
        }

        private void UpdateCircularGauge(Path path, double value, double maxValue, ref PathFigure? cachedFigure, ref ArcSegment? cachedArc)
        {
            double percentage = value / maxValue;
            if (percentage < 0) percentage = 0;
            if (percentage > 0.999) percentage = 0.999; // Avoid full circle closing singularity

            double angle = percentage * 360.0;
            double radians = angle * Math.PI / 180.0;

            // Dimensions: Center = 25, 25. Radius = 20. StartPoint = 25, 5
            const double cx = 25, cy = 25, r = 20;

            double endX = cx + r * Math.Sin(radians);
            double endY = cy - r * Math.Cos(radians);

            bool isLargeArc = angle > 180.0;

            if (cachedFigure == null || cachedArc == null)
            {
                // First call: create and cache geometry objects
                cachedArc = new ArcSegment
                {
                    Point = new Point(endX, endY),
                    Size = new Size(r, r),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = isLargeArc
                };
                cachedFigure = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
                cachedFigure.Segments.Add(cachedArc);

                PathGeometry geom = new PathGeometry();
                geom.Figures.Add(cachedFigure);
                path.Data = geom;
            }
            else
            {
                // Subsequent calls: update existing objects in-place (no allocation)
                cachedArc.Point = new Point(endX, endY);
                cachedArc.IsLargeArc = isLargeArc;
            }
        }

        private void UpdateXboxEmulation(byte[] data)
        {
            XUSB_REPORT report = new XUSB_REPORT();

            ushort buttons = 0;
            if ((data[2] & 0x01) != 0) buttons |= _mapXboxSelect;
            if ((data[2] & 0x02) != 0) buttons |= (ushort)Xbox360Buttons.LeftThumbClick;
            if ((data[2] & 0x04) != 0) buttons |= (ushort)Xbox360Buttons.RightThumbClick;
            if ((data[2] & 0x08) != 0) buttons |= _mapXboxStart;
            if ((data[2] & 0x10) != 0) buttons |= (ushort)Xbox360Buttons.DpadUp;
            if ((data[2] & 0x20) != 0) buttons |= (ushort)Xbox360Buttons.DpadRight;
            if ((data[2] & 0x40) != 0) buttons |= (ushort)Xbox360Buttons.DpadDown;
            if ((data[2] & 0x80) != 0) buttons |= (ushort)Xbox360Buttons.DpadLeft;

            if ((data[3] & 0x04) != 0) buttons |= _mapXboxL1;
            if ((data[3] & 0x08) != 0) buttons |= _mapXboxR1;
            if ((data[3] & 0x10) != 0) buttons |= _mapXboxTriangle;
            if ((data[3] & 0x20) != 0) buttons |= _mapXboxCircle;
            if ((data[3] & 0x40) != 0) buttons |= _mapXboxCross;
            if ((data[3] & 0x80) != 0) buttons |= _mapXboxSquare;

            if ((data[4] & 0x01) != 0) buttons |= _mapXboxPS;

            report.wButtons = buttons;
            report.bLeftTrigger = data[18];
            report.bRightTrigger = data[19];

            report.sThumbLX = (short)((data[6] - 128) * 256);
            report.sThumbLY = (short)(-(data[7] - 128) * 256);
            report.sThumbRX = (short)((data[8] - 128) * 256);
            report.sThumbRY = (short)(-(data[9] - 128) * 256);

            // --- Performance: Only send to ViGEm if the report actually changed ---
            if (report.wButtons != _lastXboxReport.wButtons ||
                report.bLeftTrigger != _lastXboxReport.bLeftTrigger ||
                report.bRightTrigger != _lastXboxReport.bRightTrigger ||
                report.sThumbLX != _lastXboxReport.sThumbLX ||
                report.sThumbLY != _lastXboxReport.sThumbLY ||
                report.sThumbRX != _lastXboxReport.sThumbRX ||
                report.sThumbRY != _lastXboxReport.sThumbRY)
            {
                _lastXboxReport = report;
                _vigemClient.Update(report);
            }
        }

        private void UpdateKeyboardMouseMapping(byte[] data)
        {
            int lx = data[6];
            int ly = data[7];

            int deadzone = _stickDeadzone;
            double dxNorm = (lx - 128.0) / 128.0;
            double dyNorm = (ly - 128.0) / 128.0;

            if (Math.Abs(lx - 128) > deadzone || Math.Abs(ly - 128) > deadzone)
            {
                int moveX = (int)(Math.Sign(dxNorm) * Math.Pow(dxNorm, 2) * 14.0);
                int moveY = (int)(Math.Sign(dyNorm) * Math.Pow(dyNorm, 2) * 14.0);
                InputSimulator.SendMouseMove(moveX, moveY);
            }

            if (_prevData != null)
            {
                CheckKeyTransition(data[2], _prevData[2], 0x10, _mapDpadUp);
                CheckKeyTransition(data[2], _prevData[2], 0x40, _mapDpadDown);
                CheckKeyTransition(data[2], _prevData[2], 0x80, _mapDpadLeft);
                CheckKeyTransition(data[2], _prevData[2], 0x20, _mapDpadRight);
                CheckKeyTransition(data[2], _prevData[2], 0x08, _mapStart);
                CheckKeyTransition(data[2], _prevData[2], 0x01, _mapSelect);

                CheckKeyTransition(data[3], _prevData[3], 0x40, _mapCross);
                CheckKeyTransition(data[3], _prevData[3], 0x20, _mapCircle);
                CheckKeyTransition(data[3], _prevData[3], 0x80, _mapSquare);
                CheckKeyTransition(data[3], _prevData[3], 0x10, _mapTriangle);

                bool prevL1 = (_prevData[3] & 0x04) != 0;
                bool currL1 = (data[3] & 0x04) != 0;
                if (currL1 != prevL1) InputSimulator.SendMouseClick(true, currL1);

                bool prevR1 = (_prevData[3] & 0x08) != 0;
                bool currR1 = (data[3] & 0x08) != 0;
                if (currR1 != prevR1) InputSimulator.SendMouseClick(false, currR1);
            }
        }

        private void CheckKeyTransition(byte currByte, byte prevByte, byte mask, ushort vk)
        {
            bool currPressed = (currByte & mask) != 0;
            bool prevPressed = (prevByte & mask) != 0;

            if (currPressed && !prevPressed) InputSimulator.SendKeyPress(vk);
            else if (!currPressed && prevPressed) InputSimulator.SendKeyRelease(vk);
        }

        private void XboxEmulationToggle_Click(object sender, RoutedEventArgs e)
        {
            if (XboxEmulationToggle.IsChecked == true)
            {
                if (!_vigemClient.IsSupported)
                {
                    XboxEmulationToggle.IsChecked = false;
                    _xboxEmulationEnabled = false;
                    MessageBox.Show("ViGEmBus driver is not installed on this system.\nXbox emulation cannot be activated.", "Feature Disabled", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog("[ViGEm] Activating Virtual Xbox 360 Controller...");
                if (_vigemClient.Connect() && _vigemClient.PlugInTarget())
                {
                    AddLog("[ViGEm] Virtual Xbox 360 controller connected successfully.");
                    _xboxEmulationEnabled = true;
                }
                else
                {
                    AddLog("[ViGEm] Failed to connect to virtual controller bus.");
                    _vigemClient.Disconnect();
                    XboxEmulationToggle.IsChecked = false;
                    _xboxEmulationEnabled = false;
                }
            }
            else
            {
                AddLog("[ViGEm] Disconnecting virtual controller...");
                _xboxEmulationEnabled = false;
                _vigemClient.Disconnect();
                AddLog("[ViGEm] Virtual controller unplugged.");
            }
        }

        private void KbdMouseMappingToggle_Click(object sender, RoutedEventArgs e)
        {
            if (KbdMouseMappingToggle.IsChecked == true)
            {
                _kbdMouseMappingEnabled = true;
                AddLog("[Simulator] Keyboard & Mouse Mapper activated.");
            }
            else
            {
                _kbdMouseMappingEnabled = false;
                AddLog("[Simulator] Keyboard & Mouse Mapper deactivated.");
            }
        }

        private void RumbleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RumbleValText == null) return;
            
            byte val = (byte)e.NewValue;
            RumbleValText.Text = val.ToString();

            if (_hidEngine.IsConnected)
            {
                _hidEngine.SetRumble(val, val > 0);
            }
        }

        private void DownloadViGEm_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/ViGEm/ViGEmBus/releases",
                    UseShellExecute = true
                });
                AddLog("[System] Opened ViGEmBus releases page in browser.");
            }
            catch (Exception ex)
            {
                AddLog($"[System] Failed to open link: {ex.Message}");
            }
        }

        // Card Clicks (Concept 1 UI)
        private void MemoryEditorCard_Click(object sender, MouseButtonEventArgs e)
        {
            AddLog("[System] Opened Controller Layout Mappings dialog.");
            ShowInteractiveMappingModal("🎮 Edit Controller Mappings (Xbox 360)", new string[][] {
                new string[] { "Cross (✖) Button", "Cross" },
                new string[] { "Circle (●) Button", "Circle" },
                new string[] { "Square (■) Button", "Square" },
                new string[] { "Triangle (▲) Button", "Triangle" },
                new string[] { "L1 Bumper", "L1" },
                new string[] { "R1 Bumper", "R1" },
                new string[] { "Select Button", "Select" },
                new string[] { "Start Button", "Start" },
                new string[] { "PS Button", "PS" }
            }, true);
        }

        private void ModuleCard_Click(object sender, MouseButtonEventArgs e)
        {
            AddLog("[System] Opened Keyboard & Mouse Mappings dialog.");
            ShowInteractiveMappingModal("⚡ Manage Keyboard & Mouse Mappings", new string[][] {
                new string[] { "D-Pad Up", "DpadUp" },
                new string[] { "D-Pad Down", "DpadDown" },
                new string[] { "D-Pad Left", "DpadLeft" },
                new string[] { "D-Pad Right", "DpadRight" },
                new string[] { "Cross (✖) Button", "Cross" },
                new string[] { "Circle (●) Button", "Circle" },
                new string[] { "Square (■) Button", "Square" },
                new string[] { "Triangle (▲) Button", "Triangle" },
                new string[] { "Start Button", "Start" },
                new string[] { "Select Button", "Select" }
            }, false);
        }

        private void SystemInfoCard_Click(object sender, MouseButtonEventArgs e)
        {
            AddLog("[System] Opened Haptics & Configuration Details dialog.");
            ShowHapticsOptionsModal();
        }

        private void CloseModal_Click(object sender, RoutedEventArgs e)
        {
            // Turn off test rumble if it was active
            if (_currentModalType == ModalType.Haptics && _hidEngine.IsConnected)
            {
                _hidEngine.SetRumble(0, false);
            }
            ModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void SaveModal_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModalType == ModalType.ControllerMapping || _currentModalType == ModalType.KeyboardMapping)
            {
                bool isXbox = _currentModalType == ModalType.ControllerMapping;
                var dict = isXbox ? AvailableXboxButtons : AvailableKeys;

                foreach (var combo in _activeMappingCombos)
                {
                    string fieldName = combo.Tag as string ?? "";
                    if (combo.SelectedItem is string selectedKey && dict.TryGetValue(selectedKey, out ushort newCode))
                    {
                        if (isXbox)
                        {
                            UpdateXboxMappingField(fieldName, newCode);
                        }
                        else
                        {
                            UpdateMappingField(fieldName, newCode);
                        }
                    }
                }
                AddLog("[System] Mapping configuration saved successfully.");
            }
            else if (_currentModalType == ModalType.Haptics)
            {
                // Save LED Assignment
                if (_hapticsLedCombo != null)
                {
                    int player = _hapticsLedCombo.SelectedIndex + 1;
                    _playerLed = player;
                    if (_hidEngine.IsConnected)
                    {
                        _hidEngine.SetLed(player);
                    }
                    AddLog($"[HID] Assigned hardware LED to Player {player}.");
                }

                // Save Rumble Test (Sync with active slider)
                if (_hapticsRumbleSlider != null)
                {
                    byte val = (byte)_hapticsRumbleSlider.Value;
                    RumbleSlider.Value = val;
                    // Ensure haptic motor is turned off after closing modal
                    if (_hidEngine.IsConnected)
                    {
                        _hidEngine.SetRumble(0, false);
                    }
                }

                // Save Stick Deadzone
                if (_hapticsDeadzoneSlider != null)
                {
                    _stickDeadzone = (int)_hapticsDeadzoneSlider.Value;
                    AddLog($"[Simulator] Stick deadzone saved: {_stickDeadzone} units.");
                }
            }
            else if (_currentModalType == ModalType.Settings)
            {
                if (_settingsShowNotifications != null)
                {
                    SettingsManager.Instance.ShowNotifications = _settingsShowNotifications.IsChecked == true;
                }
                if (_settingsMinimizeOnClose != null)
                {
                    SettingsManager.Instance.MinimizeOnClose = _settingsMinimizeOnClose.IsChecked == true;
                }
                if (_settingsStartMinimized != null)
                {
                    SettingsManager.Instance.StartMinimized = _settingsStartMinimized.IsChecked == true;
                }
                if (_settingsRunAtStartup != null)
                {
                    SettingsManager.Instance.RunAtStartup = _settingsRunAtStartup.IsChecked == true;
                }

                SettingsManager.Save();
                AddLog("[System] Application preferences saved successfully.");
            }

            ModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void RestoreDefaultModal_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModalType == ModalType.ControllerMapping || _currentModalType == ModalType.KeyboardMapping)
            {
                bool isXbox = _currentModalType == ModalType.ControllerMapping;
                var dict = isXbox ? AvailableXboxButtons : AvailableKeys;

                foreach (var combo in _activeMappingCombos)
                {
                    string fieldName = combo.Tag as string ?? "";
                    ushort defaultVal = GetDefaultMappingValue(fieldName, isXbox);

                    // Find key corresponding to default value
                    string defaultKeyName = "";
                    foreach (var kvp in dict)
                    {
                        if (kvp.Value == defaultVal)
                        {
                            defaultKeyName = kvp.Key;
                            break;
                        }
                    }

                    combo.SelectedItem = defaultKeyName;
                }
                AddLog("[System] Mappings reset to defaults in editor. Click Save to apply.");
            }
            else if (_currentModalType == ModalType.Haptics)
            {
                if (_hapticsLedCombo != null)
                {
                    _hapticsLedCombo.SelectedIndex = 0; // Default to Player 1
                }
                if (_hapticsRumbleSlider != null)
                {
                    _hapticsRumbleSlider.Value = 0; // Default to 0
                    if (_hidEngine.IsConnected)
                    {
                        _hidEngine.SetRumble(0, false);
                    }
                }
                if (_hapticsDeadzoneSlider != null)
                {
                    _hapticsDeadzoneSlider.Value = 15; // Default to 15
                }
                AddLog("[System] Haptics reset to defaults in editor. Click Save to apply.");
            }
            else if (_currentModalType == ModalType.Settings)
            {
                if (_settingsShowNotifications != null)
                {
                    _settingsShowNotifications.IsChecked = true;
                }
                if (_settingsMinimizeOnClose != null)
                {
                    _settingsMinimizeOnClose.IsChecked = false;
                }
                if (_settingsStartMinimized != null)
                {
                    _settingsStartMinimized.IsChecked = false;
                }
                if (_settingsRunAtStartup != null)
                {
                    _settingsRunAtStartup.IsChecked = false;
                }
                AddLog("[System] Preferences reset to defaults in editor. Click Save to apply.");
            }
        }

        private ushort GetDefaultMappingValue(string fieldName, bool isXbox)
        {
            if (isXbox)
            {
                switch (fieldName)
                {
                    case "Cross": return (ushort)Xbox360Buttons.A;
                    case "Circle": return (ushort)Xbox360Buttons.B;
                    case "Square": return (ushort)Xbox360Buttons.X;
                    case "Triangle": return (ushort)Xbox360Buttons.Y;
                    case "L1": return (ushort)Xbox360Buttons.LeftShoulder;
                    case "R1": return (ushort)Xbox360Buttons.RightShoulder;
                    case "Select": return (ushort)Xbox360Buttons.Back;
                    case "Start": return (ushort)Xbox360Buttons.Start;
                    case "PS": return (ushort)Xbox360Buttons.Guide;
                    default: return 0;
                }
            }
            else
            {
                switch (fieldName)
                {
                    case "DpadUp": return 0x26;    // Up Arrow
                    case "DpadDown": return 0x28;  // Down Arrow
                    case "DpadLeft": return 0x25;  // Left Arrow
                    case "DpadRight": return 0x27; // Right Arrow
                    case "Cross": return 0x20;     // Space
                    case "Circle": return 0xA2;    // Left Control
                    case "Square": return 0x52;    // R Key
                    case "Triangle": return 0x09;  // Tab
                    case "Start": return 0x0D;     // Enter
                    case "Select": return 0x1B;    // Escape
                    default: return 0;
                }
            }
        }

        private void ShowInteractiveMappingModal(string title, string[][] mappingItems, bool isXbox)
        {
            _currentModalType = isXbox ? ModalType.ControllerMapping : ModalType.KeyboardMapping;
            _activeMappingCombos.Clear();

            ModalTitle.Text = title;
            ModalContentStack.Children.Clear();

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.0, GridUnitType.Star) });

            // Add Header Row
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            
            TextBlock hdr1 = new TextBlock { Text = "PS3 Controller Input", FontWeight = FontWeights.Bold, Foreground = _activeBrush, FontSize = 12, Padding = new Thickness(6, 8, 6, 8) };
            TextBlock hdr2 = new TextBlock { Text = isXbox ? "Xbox 360 Target Input" : "Windows Keyboard/Mouse Output", FontWeight = FontWeights.Bold, Foreground = Brushes.White, FontSize = 12, Padding = new Thickness(6, 8, 6, 8) };
            
            Border bh1 = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x3C)), BorderThickness = new Thickness(0, 0, 0, 15), Child = hdr1 };
            Border bh2 = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x3C)), BorderThickness = new Thickness(0, 0, 0, 15), Child = hdr2 };
            
            Grid.SetRow(bh1, 0); Grid.SetColumn(bh1, 0);
            Grid.SetRow(bh2, 0); Grid.SetColumn(bh2, 1);
            grid.Children.Add(bh1); grid.Children.Add(bh2);

            var dict = isXbox ? AvailableXboxButtons : AvailableKeys;

            for (int i = 0; i < mappingItems.Length; i++)
            {
                int r = i + 1;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                string label = mappingItems[i][0];
                string fieldName = mappingItems[i][1];
                ushort currentValue = GetCurrentMapValue(fieldName, isXbox);

                TextBlock txtLabel = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(6, 6, 6, 6)
                };

                // Find key corresponding to current value
                string currentKeyName = "";
                foreach (var kvp in dict)
                {
                    if (kvp.Value == currentValue)
                    {
                        currentKeyName = kvp.Key;
                        break;
                    }
                }

                ComboBox combo = new ComboBox
                {
                    ItemsSource = dict.Keys,
                    SelectedItem = currentKeyName,
                    Style = (Style)FindResource("DarkComboBoxStyle"),
                    ItemContainerStyle = (Style)FindResource("DarkComboBoxItemStyle"),
                    FontSize = 11,
                    Height = 24,
                    Margin = new Thickness(4, 2, 4, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = fieldName
                };

                _activeMappingCombos.Add(combo);

                Border bLabel = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x3C)), BorderThickness = new Thickness(0, 0, 0, 1), Child = txtLabel };
                Border bCombo = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x3C)), BorderThickness = new Thickness(0, 0, 0, 1), Child = combo };

                Grid.SetRow(bLabel, r);
                Grid.SetColumn(bLabel, 0);
                Grid.SetRow(bCombo, r);
                Grid.SetColumn(bCombo, 1);

                grid.Children.Add(bLabel);
                grid.Children.Add(bCombo);
            }

            ModalContentStack.Children.Add(grid);
            ModalOverlay.Visibility = Visibility.Visible;
        }

        private void ShowHapticsOptionsModal()
        {
            _currentModalType = ModalType.Haptics;

            ModalTitle.Text = "⚙ Haptics & Configuration Options";
            ModalContentStack.Children.Clear();

            // LED Selection Row
            StackPanel ledPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            ledPanel.Children.Add(new TextBlock { Text = "Assign Controller Player LED", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = _activeBrush, Margin = new Thickness(0, 0, 0, 5) });
            
            ComboBox ledCombo = new ComboBox
            {
                ItemsSource = new string[] { "Player 1 (LED 1)", "Player 2 (LED 2)", "Player 3 (LED 3)", "Player 4 (LED 4)" },
                SelectedIndex = _playerLed - 1,
                Style = (Style)FindResource("DarkComboBoxStyle"),
                ItemContainerStyle = (Style)FindResource("DarkComboBoxItemStyle"),
                FontSize = 11,
                Height = 24
            };
            
            _hapticsLedCombo = ledCombo;
            ledPanel.Children.Add(ledCombo);
            ModalContentStack.Children.Add(ledPanel);

            // Rumble Test Row
            StackPanel rumblePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            rumblePanel.Children.Add(new TextBlock { Text = "Hardware Rumble Test Motor", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = _activeBrush, Margin = new Thickness(0, 0, 0, 5) });
            
            Grid rumbleGrid = new Grid();
            rumbleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            rumbleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            Slider rumbleSlider = new Slider { Minimum = 0, Maximum = 255, Value = RumbleSlider.Value, Style = (Style)FindResource("GlowingSliderStyle"), VerticalAlignment = VerticalAlignment.Center };
            TextBlock rumbleText = new TextBlock { Text = ((int)RumbleSlider.Value).ToString(), Width = 30, FontSize = 12, Foreground = Brushes.White, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            
            rumbleSlider.ValueChanged += (s, ev) =>
            {
                byte val = (byte)rumbleSlider.Value;
                rumbleText.Text = val.ToString();
                
                // Immediate rumble feedback for testing purposes
                if (_hidEngine.IsConnected)
                {
                    _hidEngine.SetRumble(val, val > 0);
                }
            };
            
            _hapticsRumbleSlider = rumbleSlider;
            
            Grid.SetColumn(rumbleSlider, 0);
            Grid.SetColumn(rumbleText, 1);
            rumbleGrid.Children.Add(rumbleSlider);
            rumbleGrid.Children.Add(rumbleText);
            rumblePanel.Children.Add(rumbleGrid);
            ModalContentStack.Children.Add(rumblePanel);

            // Deadzone Row
            StackPanel deadzonePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            deadzonePanel.Children.Add(new TextBlock { Text = "Analog Stick Deadzone", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = _activeBrush, Margin = new Thickness(0, 0, 0, 5) });
            
            Grid deadzoneGrid = new Grid();
            deadzoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            deadzoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            Slider deadzoneSlider = new Slider { Minimum = 0, Maximum = 50, Value = _stickDeadzone, Style = (Style)FindResource("GlowingSliderStyle"), VerticalAlignment = VerticalAlignment.Center };
            TextBlock deadzoneText = new TextBlock { Text = _stickDeadzone.ToString(), Width = 30, FontSize = 12, Foreground = Brushes.White, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            
            deadzoneSlider.ValueChanged += (s, ev) =>
            {
                deadzoneText.Text = ((int)deadzoneSlider.Value).ToString();
            };
            
            _hapticsDeadzoneSlider = deadzoneSlider;
            
            Grid.SetColumn(deadzoneSlider, 0);
            Grid.SetColumn(deadzoneText, 1);
            deadzoneGrid.Children.Add(deadzoneSlider);
            deadzoneGrid.Children.Add(deadzoneText);
            deadzonePanel.Children.Add(deadzoneGrid);
            ModalContentStack.Children.Add(deadzonePanel);

            ModalOverlay.Visibility = Visibility.Visible;
        }

        private ushort GetCurrentMapValue(string fieldName, bool isXbox)
        {
            if (isXbox)
            {
                switch (fieldName)
                {
                    case "Cross": return _mapXboxCross;
                    case "Circle": return _mapXboxCircle;
                    case "Square": return _mapXboxSquare;
                    case "Triangle": return _mapXboxTriangle;
                    case "L1": return _mapXboxL1;
                    case "R1": return _mapXboxR1;
                    case "Select": return _mapXboxSelect;
                    case "Start": return _mapXboxStart;
                    case "PS": return _mapXboxPS;
                    default: return 0;
                }
            }
            else
            {
                switch (fieldName)
                {
                    case "DpadUp": return _mapDpadUp;
                    case "DpadDown": return _mapDpadDown;
                    case "DpadLeft": return _mapDpadLeft;
                    case "DpadRight": return _mapDpadRight;
                    case "Cross": return _mapCross;
                    case "Circle": return _mapCircle;
                    case "Square": return _mapSquare;
                    case "Triangle": return _mapTriangle;
                    case "Start": return _mapStart;
                    case "Select": return _mapSelect;
                    default: return 0;
                }
            }
        }

        private void UpdateMappingField(string fieldName, ushort keyVal)
        {
            switch (fieldName)
            {
                case "DpadUp": _mapDpadUp = keyVal; break;
                case "DpadDown": _mapDpadDown = keyVal; break;
                case "DpadLeft": _mapDpadLeft = keyVal; break;
                case "DpadRight": _mapDpadRight = keyVal; break;
                case "Cross": _mapCross = keyVal; break;
                case "Circle": _mapCircle = keyVal; break;
                case "Square": _mapSquare = keyVal; break;
                case "Triangle": _mapTriangle = keyVal; break;
                case "Start": _mapStart = keyVal; break;
                case "Select": _mapSelect = keyVal; break;
            }
            AddLog($"[Simulator] Remapped {fieldName} to key code 0x{keyVal:X2}.");
        }

        private void UpdateXboxMappingField(string fieldName, ushort xboxVal)
        {
            switch (fieldName)
            {
                case "Cross": _mapXboxCross = xboxVal; break;
                case "Circle": _mapXboxCircle = xboxVal; break;
                case "Square": _mapXboxSquare = xboxVal; break;
                case "Triangle": _mapXboxTriangle = xboxVal; break;
                case "L1": _mapXboxL1 = xboxVal; break;
                case "R1": _mapXboxR1 = xboxVal; break;
                case "Select": _mapXboxSelect = xboxVal; break;
                case "Start": _mapXboxStart = xboxVal; break;
                case "PS": _mapXboxPS = xboxVal; break;
            }
            AddLog($"[ViGEm] Remapped controller {fieldName} to Xbox 360 button.");
        }

        // Quick Actions
        private void ReloadDriver_Click(object sender, MouseButtonEventArgs e)
        {
            AddLog("[System] Quick Action: Reloading driver engine...");
            if (_hidEngine.IsConnected)
            {
                _hidEngine.Disconnect();
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                {
                    _hidEngine.TryConnect();
                });
            }
            else
            {
                _hidEngine.TryConnect();
            }
        }

        private void ClearLogs_Click(object sender, MouseButtonEventArgs e)
        {
            LogListBox.Items.Clear();
            AddLog("[System] Log console cleared.");
        }

        private void AddLog(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                LogListBox.Items.Add($"[{time}] {message}");

                if (LogListBox.Items.Count > 0)
                {
                    LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
                }

                if (LogListBox.Items.Count > 100)
                {
                    LogListBox.Items.RemoveAt(0);
                }
            }));
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            if (_hidEngine != null) _hidEngine.Dispose();
            if (_vigemClient != null) _vigemClient.Dispose();
        }

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = "Monarch Butterfly";

            try
            {
                var iconUri = new Uri("pack://application:,,,/task bar icon.png", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    using (var stream = streamInfo.Stream)
                    using (var bitmap = new System.Drawing.Bitmap(stream))
                    {
                        IntPtr hIcon = bitmap.GetHicon();
                        _notifyIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"[System] Failed to load tray icon: {ex.Message}");
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Open Monarch Butterfly", null, (s, e) => RestoreFromTray());
            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.Visible = true;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                this.Hide();
                if (SettingsManager.Instance.ShowNotifications && _notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(3000, "Monarch Butterfly", "Minimized to Background Apps.", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (SettingsManager.Instance.MinimizeOnClose && !_isExiting)
            {
                e.Cancel = true;
                this.Hide();
                if (SettingsManager.Instance.ShowNotifications && _notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(3000, "Monarch Butterfly", "Minimized to Background Apps.", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                base.OnClosing(e);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (SettingsManager.Instance.StartMinimized)
            {
                this.Hide();
                this.WindowState = WindowState.Normal;
            }
        }

        private void SettingsMenu_Click(object sender, MouseButtonEventArgs e)
        {
            AddLog("[System] Opened Application Settings & Preferences.");
            ShowSettingsModal();
        }

        private void ShowSettingsModal()
        {
            _currentModalType = ModalType.Settings;
            ModalTitle.Text = "⚙ Application Settings & Preferences";
            ModalContentStack.Children.Clear();

            Style toggleStyle = (Style)FindResource("ToggleSwitchStyle");
            SolidColorBrush descBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xA8, 0xC5)); // Sharper, higher-contrast description text color

            // 1. Show Notifications Option
            StackPanel notifPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            CheckBox chkNotif = new CheckBox
            {
                Style = toggleStyle,
                IsChecked = SettingsManager.Instance.ShowNotifications
            };
            chkNotif.Content = new TextBlock
            {
                Text = "Show System Tray Balloon Notifications",
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, -2, 0, 0)
            };
            notifPanel.Children.Add(chkNotif);
            notifPanel.Children.Add(new TextBlock
            {
                Text = "Displays notifications in the Windows system tray when the application is minimized to Background Apps.",
                FontSize = 10.5,
                Foreground = descBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(54, 4, 0, 0)
            });
            _settingsShowNotifications = chkNotif;
            ModalContentStack.Children.Add(notifPanel);

            // 2. Minimize on Close Option
            StackPanel closePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            CheckBox chkClose = new CheckBox
            {
                Style = toggleStyle,
                IsChecked = SettingsManager.Instance.MinimizeOnClose
            };
            chkClose.Content = new TextBlock
            {
                Text = "Minimize to System Tray on Close",
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, -2, 0, 0)
            };
            closePanel.Children.Add(chkClose);
            closePanel.Children.Add(new TextBlock
            {
                Text = "Hides the application window to the background tray when clicking the 'X' button instead of exiting the driver.",
                FontSize = 10.5,
                Foreground = descBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(54, 4, 0, 0)
            });
            _settingsMinimizeOnClose = chkClose;
            ModalContentStack.Children.Add(closePanel);

            // 3. Start Minimized Option
            StackPanel startMinPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            CheckBox chkStartMin = new CheckBox
            {
                Style = toggleStyle,
                IsChecked = SettingsManager.Instance.StartMinimized
            };
            chkStartMin.Content = new TextBlock
            {
                Text = "Start Minimized in Tray",
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, -2, 0, 0)
            };
            startMinPanel.Children.Add(chkStartMin);
            startMinPanel.Children.Add(new TextBlock
            {
                Text = "Launches the application hidden directly in the system tray when starting up.",
                FontSize = 10.5,
                Foreground = descBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(54, 4, 0, 0)
            });
            _settingsStartMinimized = chkStartMin;
            ModalContentStack.Children.Add(startMinPanel);

            // 4. Run at Startup Option
            StackPanel startupPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            CheckBox chkStartup = new CheckBox
            {
                Style = toggleStyle,
                IsChecked = SettingsManager.Instance.RunAtStartup
            };
            chkStartup.Content = new TextBlock
            {
                Text = "Run at Windows Startup",
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, -2, 0, 0)
            };
            startupPanel.Children.Add(chkStartup);
            startupPanel.Children.Add(new TextBlock
            {
                Text = "Automatically runs the Monarch Butterfly driver dashboard when you log in to Windows.",
                FontSize = 10.5,
                Foreground = descBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(54, 4, 0, 0)
            });
            _settingsRunAtStartup = chkStartup;
            ModalContentStack.Children.Add(startupPanel);

            ModalOverlay.Visibility = Visibility.Visible;
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            _isExiting = true;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            System.Windows.Application.Current.Shutdown();
        }
    }
}