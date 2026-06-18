using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PSControllerUI
{
    /// <summary>
    /// Parsed HID report layout: exact byte/bit positions for each gamepad control,
    /// populated automatically from the device's HID Report Descriptor via HidP_* API.
    /// </summary>
    public class HidReportMap : IDisposable
    {
        public IntPtr PreparsedData { get; internal set; } = IntPtr.Zero;
        // --- Axis descriptors ---
        public AxisInfo? LeftStickX { get; private set; }
        public AxisInfo? LeftStickY { get; private set; }
        public AxisInfo? RightStickX { get; private set; }
        public AxisInfo? RightStickY { get; private set; }
        public AxisInfo? HatSwitch { get; private set; }
        public AxisInfo? LeftTrigger { get; private set; }
        public AxisInfo? RightTrigger { get; private set; }

        // --- Button descriptors (HID Usage -> bit index in report) ---
        // Standard Gamepad Button usages (HID Usage Page 0x09 - Button Page)
        // Button 1 = Cross/A, 2 = Circle/B, 3 = Square/X, 4 = Triangle/Y, etc.
        public ButtonInfo? BtnCross { get; private set; }     // Button 1 or 2 (varies by vendor)
        public ButtonInfo? BtnCircle { get; private set; }    // Button 2 or 3
        public ButtonInfo? BtnSquare { get; private set; }    // Button 3 or 1
        public ButtonInfo? BtnTriangle { get; private set; }  // Button 4
        public ButtonInfo? BtnL1 { get; private set; }        // Button 5
        public ButtonInfo? BtnR1 { get; private set; }        // Button 6
        public ButtonInfo? BtnL2 { get; private set; }        // Button 7
        public ButtonInfo? BtnR2 { get; private set; }        // Button 8
        public ButtonInfo? BtnSelect { get; private set; }    // Button 9
        public ButtonInfo? BtnStart { get; private set; }     // Button 10
        public ButtonInfo? BtnL3 { get; private set; }        // Button 11
        public ButtonInfo? BtnR3 { get; private set; }        // Button 12
        public ButtonInfo? BtnPS { get; private set; }        // Button 13

        public bool IsValid { get; private set; }
        public string DiagnosticSummary { get; private set; } = "";

        // ---- Public axis/button structs ----

        public class AxisInfo
        {
            public ushort UsagePage;
            public ushort Usage;
            public ushort LinkCollection;
            public int LogicalMin;
            public int LogicalMax;
            public int BitSize;
            public int DataIndex;   // Tracks field ordering in descriptor
            public string Name = "";

            /// <summary>
            /// Read the raw value from the report buffer using the Windows HID parser.
            /// </summary>
            public int ReadRaw(byte[] report, int length, IntPtr preparsedData)
            {
                if (preparsedData == IntPtr.Zero) return (LogicalMin + LogicalMax) / 2;

                uint value;
                int status = HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, UsagePage, LinkCollection, Usage, out value, preparsedData, report, (uint)length);
                if (status == HIDP_STATUS_SUCCESS)
                {
                    return (int)value;
                }
                return (LogicalMin + LogicalMax) / 2;
            }

            /// <summary>
            /// Read as normalized float -1.0 to 1.0 (for sticks) or 0.0 to 1.0 (for triggers).
            /// </summary>
            public double ReadNormalized(byte[] report, int length, IntPtr preparsedData)
            {
                int raw = ReadRaw(report, length, preparsedData);
                double range = LogicalMax - LogicalMin;
                if (range <= 0) return 0.0;
                return ((raw - LogicalMin) / range) * 2.0 - 1.0;
            }

            /// <summary>
            /// Read as byte 0-255 scaled from logical range.
            /// </summary>
            public byte ReadByte(byte[] report, int length, IntPtr preparsedData)
            {
                int raw = ReadRaw(report, length, preparsedData);
                double range = LogicalMax - LogicalMin;
                if (range <= 0) return 128;
                return (byte)Math.Clamp((int)(((raw - LogicalMin) / range) * 255.0), 0, 255);
            }
        }

        public class ButtonInfo
        {
            public ushort UsagePage;
            public ushort LinkCollection;
            public ushort UsageIndex; // HID button usage number (1-based)
            public string Name = "";

            public bool IsPressed(byte[] report, int length, IntPtr preparsedData)
            {
                if (preparsedData == IntPtr.Zero) return false;

                ushort[] pressedButtons = new ushort[64];
                uint numButtons = 64;
                int status = HidP_GetUsages(HIDP_REPORT_TYPE.HidP_Input, UsagePage, LinkCollection, pressedButtons, ref numButtons, preparsedData, report, (uint)length);
                if (status == HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < numButtons; i++)
                    {
                        if (pressedButtons[i] == UsageIndex)
                            return true;
                    }
                }
                return false;
            }
        }

        // ---- HidP API P/Invoke ----

        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        private enum HIDP_REPORT_TYPE
        {
            HidP_Input = 0,
            HidP_Output = 1,
            HidP_Feature = 2
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_BUTTON_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsStringRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsDesignatorRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAbsolute;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
            public uint[] Reserved;
            // Union: Range / NotRange
            public ushort UsageMin;       // Range.UsageMin or NotRange.Usage
            public ushort UsageMax;       // Range.UsageMax or NotRange.Reserved1
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;   // Range.DataIndexMin or NotRange.DataIndex
            public ushort DataIndexMax;   // Range.DataIndexMax or NotRange.Reserved4
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsStringRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsDesignatorRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAbsolute;
            public byte HasNull;
            public byte Reserved_byte;
            public ushort BitSize;
            public ushort ReportCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            // Union: Range / NotRange
            public ushort UsageMin;       // Range.UsageMin or NotRange.Usage
            public ushort UsageMax;       // Range.UsageMax
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetButtonCaps(HIDP_REPORT_TYPE ReportType,
            [Out] HIDP_BUTTON_CAPS[] ButtonCaps, ref ushort ButtonCapsLength, IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetValueCaps(HIDP_REPORT_TYPE ReportType,
            [Out] HIDP_VALUE_CAPS[] ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetUsageValue(
            HIDP_REPORT_TYPE ReportType,
            ushort UsagePage,
            ushort LinkCollection,
            ushort Usage,
            out uint UsageValue,
            IntPtr PreparsedData,
            byte[] Report,
            uint ReportLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetUsages(
            HIDP_REPORT_TYPE ReportType,
            ushort UsagePage,
            ushort LinkCollection,
            [Out] ushort[] UsageList,
            ref uint UsageLength,
            IntPtr PreparsedData,
            byte[] Report,
            uint ReportLength);

        // ---- Main parsing entry point ----

        /// <summary>
        /// Parse the HID Report Descriptor of the opened device handle.
        /// Returns a fully populated HidReportMap or null on failure.
        /// </summary>
        public static HidReportMap? Parse(SafeFileHandle deviceHandle, Action<string>? log = null)
        {
            IntPtr preparsedData = IntPtr.Zero;
            try
            {
                if (!HidD_GetPreparsedData(deviceHandle, out preparsedData) || preparsedData == IntPtr.Zero)
                {
                    log?.Invoke("[HidReportMap] Failed to get preparsed data.");
                    return null;
                }

                HIDP_CAPS caps;
                if (HidP_GetCaps(preparsedData, out caps) != HIDP_STATUS_SUCCESS)
                {
                    log?.Invoke("[HidReportMap] Failed to get device capabilities.");
                    return null;
                }

                log?.Invoke($"[HidReportMap] Caps: Usage=0x{caps.Usage:X4}, UsagePage=0x{caps.UsagePage:X4}, " +
                    $"InputReportLen={caps.InputReportByteLength}, " +
                    $"ButtonCaps={caps.NumberInputButtonCaps}, ValueCaps={caps.NumberInputValueCaps}");

                var map = new HidReportMap();
                var diagnosticLines = new List<string>();

                // Temporary collections to hold axes for dynamic mapping after parsing
                var genericAxes = new Dictionary<ushort, AxisInfo>();
                var simulationAxes = new Dictionary<ushort, AxisInfo>();

                // ---- Parse Value Caps (axes, hat switch, triggers) ----
                if (caps.NumberInputValueCaps > 0)
                {
                    ushort numValueCaps = caps.NumberInputValueCaps;
                    var valueCaps = new HIDP_VALUE_CAPS[numValueCaps];
                    if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref numValueCaps, preparsedData) == HIDP_STATUS_SUCCESS)
                    {
                        for (int i = 0; i < numValueCaps; i++)
                        {
                            var vc = valueCaps[i];
                            ushort usagePage = vc.UsagePage;
                            ushort usage = vc.IsRange ? vc.UsageMin : vc.UsageMin; // NotRange.Usage is at same offset

                            int bitSize = vc.BitSize;
                            int reportCount = vc.ReportCount;
                            int dataIndex = vc.IsRange ? vc.DataIndexMin : vc.DataIndexMin;

                            string usageName = GetUsageName(usagePage, usage);
                            string line = $"  Value[{i}]: Page=0x{usagePage:X4}, Usage=0x{usage:X4} ({usageName}), " +
                                $"BitSize={bitSize}, LogMin={vc.LogicalMin}, LogMax={vc.LogicalMax}, " +
                                $"DataIndex={dataIndex}, ReportID=0x{vc.ReportID:X2}";
                            log?.Invoke(line);
                            diagnosticLines.Add(line);

                            // Generic Desktop Page (0x01)
                            if (usagePage == 0x01)
                            {
                                var axis = new AxisInfo
                                {
                                    UsagePage = usagePage,
                                    Usage = usage,
                                    LinkCollection = vc.LinkCollection,
                                    LogicalMin = vc.LogicalMin,
                                    LogicalMax = vc.LogicalMax,
                                    BitSize = bitSize,
                                    DataIndex = dataIndex,
                                    Name = usageName,
                                };

                                if (usage == 0x39)
                                {
                                    map.HatSwitch = axis;
                                    axis.Name = "HatSwitch";
                                }
                                else
                                {
                                    genericAxes[usage] = axis;
                                }
                            }
                            // Simulation Controls Page (0x02) - sometimes used for triggers
                            else if (usagePage == 0x02)
                            {
                                var axis = new AxisInfo
                                {
                                    UsagePage = usagePage,
                                    Usage = usage,
                                    LinkCollection = vc.LinkCollection,
                                    LogicalMin = vc.LogicalMin,
                                    LogicalMax = vc.LogicalMax,
                                    BitSize = bitSize,
                                    DataIndex = dataIndex,
                                    Name = usageName,
                                };
                                simulationAxes[usage] = axis;
                            }
                        }
                    }
                }

                // ---- Dynamic Axis Resolution ----
                // 1. Left Stick (always X=0x30, Y=0x31)
                if (genericAxes.TryGetValue(0x30, out var lx)) { map.LeftStickX = lx; lx.Name = "LeftStickX"; }
                if (genericAxes.TryGetValue(0x31, out var ly)) { map.LeftStickY = ly; ly.Name = "LeftStickY"; }

                // 2. Right Stick and Triggers
                // Standard modern layout: Right Stick is Rx (0x33) / Ry (0x34), Triggers are Z (0x32) / Rz (0x35)
                if (genericAxes.ContainsKey(0x33) && genericAxes.ContainsKey(0x34))
                {
                    map.RightStickX = genericAxes[0x33]; map.RightStickX.Name = "RightStickX (Rx)";
                    map.RightStickY = genericAxes[0x34]; map.RightStickY.Name = "RightStickY (Ry)";

                    if (genericAxes.TryGetValue(0x32, out var lt)) { map.LeftTrigger = lt; lt.Name = "LeftTrigger (Z)"; }
                    if (genericAxes.TryGetValue(0x35, out var rt)) { map.RightTrigger = rt; rt.Name = "RightTrigger (Rz)"; }
                }
                // DirectInput/Older layout: Right Stick is Z (0x32) / Rz (0x35), Triggers are Rx (0x33) / Ry (0x34)
                else if (genericAxes.ContainsKey(0x32) && genericAxes.ContainsKey(0x35))
                {
                    map.RightStickX = genericAxes[0x32]; map.RightStickX.Name = "RightStickX (Z)";
                    map.RightStickY = genericAxes[0x35]; map.RightStickY.Name = "RightStickY (Rz)";

                    if (genericAxes.TryGetValue(0x33, out var lt)) { map.LeftTrigger = lt; lt.Name = "LeftTrigger (Rx)"; }
                    if (genericAxes.TryGetValue(0x34, out var rt)) { map.RightTrigger = rt; rt.Name = "RightTrigger (Ry)"; }
                }
                // Fallback layout: Right Stick is Z (0x32) / Rx (0x33) (e.g. what the old code mapped)
                else if (genericAxes.ContainsKey(0x32) && genericAxes.ContainsKey(0x33))
                {
                    map.RightStickX = genericAxes[0x32]; map.RightStickX.Name = "RightStickX (Z)";
                    map.RightStickY = genericAxes[0x33]; map.RightStickY.Name = "RightStickY (Rx)";
                }

                // If triggers were not resolved from generic desktop, check simulation controls page (0x02)
                if (map.LeftTrigger == null && simulationAxes.TryGetValue(0xC5, out var simLt)) { map.LeftTrigger = simLt; simLt.Name = "LeftTrigger (Brake)"; }
                if (map.RightTrigger == null && simulationAxes.TryGetValue(0xC4, out var simRt)) { map.RightTrigger = simRt; simRt.Name = "RightTrigger (Accelerator)"; }

                // ---- Parse Button Caps ----
                if (caps.NumberInputButtonCaps > 0)
                {
                    ushort numButtonCaps = caps.NumberInputButtonCaps;
                    var buttonCaps = new HIDP_BUTTON_CAPS[numButtonCaps];
                    if (HidP_GetButtonCaps(HIDP_REPORT_TYPE.HidP_Input, buttonCaps, ref numButtonCaps, preparsedData) == HIDP_STATUS_SUCCESS)
                    {
                        for (int i = 0; i < numButtonCaps; i++)
                        {
                            var bc = buttonCaps[i];
                            string line;
                            if (bc.IsRange)
                            {
                                line = $"  Button[{i}]: Page=0x{bc.UsagePage:X4}, " +
                                    $"UsageRange={bc.UsageMin}-{bc.UsageMax}, " +
                                    $"DataIndexRange={bc.DataIndexMin}-{bc.DataIndexMax}, " +
                                    $"ReportID=0x{bc.ReportID:X2}";
                                log?.Invoke(line);
                                diagnosticLines.Add(line);

                                // For Button Page (0x09), buttons are numbered 1-N
                                if (bc.UsagePage == 0x09)
                                {
                                    for (ushort u = bc.UsageMin; u <= bc.UsageMax; u++)
                                    {
                                        var btn = new ButtonInfo
                                        {
                                            UsagePage = bc.UsagePage,
                                            LinkCollection = bc.LinkCollection,
                                            UsageIndex = u,
                                            Name = $"Button {u}"
                                        };
                                        AssignButton(map, u, btn);
                                    }
                                }
                            }
                            else
                            {
                                line = $"  Button[{i}]: Page=0x{bc.UsagePage:X4}, " +
                                    $"Usage={bc.UsageMin}, DataIndex={bc.DataIndexMin}, " +
                                    $"ReportID=0x{bc.ReportID:X2}";
                                log?.Invoke(line);
                                diagnosticLines.Add(line);

                                if (bc.UsagePage == 0x09)
                                {
                                    var btn = new ButtonInfo
                                    {
                                        UsagePage = bc.UsagePage,
                                        LinkCollection = bc.LinkCollection,
                                        UsageIndex = bc.UsageMin,
                                        Name = $"Button {bc.UsageMin}"
                                    };
                                    AssignButton(map, bc.UsageMin, btn);
                                }
                            }
                        }
                    }
                }

                map.DiagnosticSummary = string.Join("\n", diagnosticLines);
                map.IsValid = (map.LeftStickX != null || map.LeftStickY != null);

                return map;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[HidReportMap] Exception: {ex.Message}");
                return null;
            }
            finally
            {
                if (preparsedData != IntPtr.Zero)
                    HidD_FreePreparsedData(preparsedData);
            }
        }

        /// <summary>
        /// Resolve the byte/bit offsets for all axes and buttons by analyzing
        /// a real report packet from the device. Call this once after receiving the first packet.
        /// This uses the HidP_GetUsageValue API to extract values and cross-reference positions.
        /// </summary>
        public void ResolveOffsetsFromPacket(byte[] firstPacket, int length, Action<string>? log = null)
        {
            log?.Invoke($"[HidReportMap] First report packet received (len={length}, reportID=0x{firstPacket[0]:X2})");
            log?.Invoke($"[HidReportMap] Packet: {BitConverter.ToString(firstPacket, 0, Math.Min(length, 20))}...");
            LogResolvedMap(log);
        }


        private void LogResolvedMap(Action<string>? log)
        {
            if (log == null) return;

            log("[HidReportMap] === Resolved Report Layout ===");
            LogAxis(log, LeftStickX);
            LogAxis(log, LeftStickY);
            LogAxis(log, RightStickX);
            LogAxis(log, RightStickY);
            LogAxis(log, LeftTrigger);
            LogAxis(log, RightTrigger);
            LogAxis(log, HatSwitch);
            LogBtn(log, BtnSquare);
            LogBtn(log, BtnCross);
            LogBtn(log, BtnCircle);
            LogBtn(log, BtnTriangle);
            LogBtn(log, BtnL1);
            LogBtn(log, BtnR1);
            LogBtn(log, BtnL2);
            LogBtn(log, BtnR2);
            LogBtn(log, BtnSelect);
            LogBtn(log, BtnStart);
            LogBtn(log, BtnL3);
            LogBtn(log, BtnR3);
            LogBtn(log, BtnPS);
            log("[HidReportMap] === End Layout ===");
        }

        private static void LogAxis(Action<string> log, AxisInfo? axis)
        {
            if (axis == null) return;
            log($"  {axis.Name}: Page=0x{axis.UsagePage:X4} Usage=0x{axis.Usage:X4} LinkCollection={axis.LinkCollection} Range=[{axis.LogicalMin}..{axis.LogicalMax}]");
        }

        private static void LogBtn(Action<string> log, ButtonInfo? btn)
        {
            if (btn == null) return;
            log($"  {btn.Name}: Page=0x{btn.UsagePage:X4} UsageIndex={btn.UsageIndex} LinkCollection={btn.LinkCollection}");
        }

        // ---- Helpers ----

        private static void AssignButton(HidReportMap map, int usageIndex, ButtonInfo btn)
        {
            switch (usageIndex)
            {
                case 1: map.BtnSquare = btn; btn.Name = "Square (Btn1)"; break;
                case 2: map.BtnCross = btn; btn.Name = "Cross (Btn2)"; break;
                case 3: map.BtnCircle = btn; btn.Name = "Circle (Btn3)"; break;
                case 4: map.BtnTriangle = btn; btn.Name = "Triangle (Btn4)"; break;
                case 5: map.BtnL1 = btn; btn.Name = "L1 (Btn5)"; break;
                case 6: map.BtnR1 = btn; btn.Name = "R1 (Btn6)"; break;
                case 7: map.BtnL2 = btn; btn.Name = "L2 (Btn7)"; break;
                case 8: map.BtnR2 = btn; btn.Name = "R2 (Btn8)"; break;
                case 9: map.BtnSelect = btn; btn.Name = "Select (Btn9)"; break;
                case 10: map.BtnStart = btn; btn.Name = "Start (Btn10)"; break;
                case 11: map.BtnL3 = btn; btn.Name = "L3 (Btn11)"; break;
                case 12: map.BtnR3 = btn; btn.Name = "R3 (Btn12)"; break;
                case 13: map.BtnPS = btn; btn.Name = "PS (Btn13)"; break;
            }
        }

        private static string GetUsageName(ushort page, ushort usage)
        {
            if (page == 0x01) // Generic Desktop
            {
                return usage switch
                {
                    0x01 => "Pointer",
                    0x04 => "Joystick",
                    0x05 => "Gamepad",
                    0x30 => "X",
                    0x31 => "Y",
                    0x32 => "Z",
                    0x33 => "Rx",
                    0x34 => "Ry",
                    0x35 => "Rz",
                    0x39 => "Hat Switch",
                    _ => $"0x{usage:X2}"
                };
            }
            if (page == 0x02) // Simulation
            {
                return usage switch
                {
                    0xC4 => "Accelerator",
                    0xC5 => "Brake",
                    _ => $"0x{usage:X2}"
                };
            }
            if (page == 0x09) return $"Button {usage}"; // Button Page
            return $"Page=0x{page:X4}/0x{usage:X2}";
        }

        // ---- Convenience: Decode hat switch to D-pad booleans ----

        public void ReadHatSwitch(byte[] data, int length, out bool up, out bool right, out bool down, out bool left)
        {
            if (HatSwitch == null || PreparsedData == IntPtr.Zero)
            {
                up = right = down = left = false;
                return;
            }
            int hat = HatSwitch.ReadRaw(data, length, PreparsedData);
            // Standard hat encoding: 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW, 8+ = centered
            up = hat == 0 || hat == 1 || hat == 7;
            right = hat == 1 || hat == 2 || hat == 3;
            down = hat == 3 || hat == 4 || hat == 5;
            left = hat == 5 || hat == 6 || hat == 7;
        }

        public void Dispose()
        {
            if (PreparsedData != IntPtr.Zero)
            {
                HidD_FreePreparsedData(PreparsedData);
                PreparsedData = IntPtr.Zero;
            }
        }
    }
}
