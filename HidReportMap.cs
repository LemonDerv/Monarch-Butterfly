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
    public class HidReportMap
    {
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
            public int ByteOffset;  // Byte index in the report
            public int BitOffset;   // Bit offset within the byte (usually 0 for full-byte values)
            public int BitSize;     // Number of bits (8 for a standard byte-sized axis)
            public int LogicalMin;
            public int LogicalMax;
            public string Name = "";

            /// <summary>
            /// Read the raw value from the report buffer.
            /// </summary>
            public int ReadRaw(byte[] data, int length)
            {
                if (ByteOffset >= length) return LogicalMin;

                if (BitSize == 8 && BitOffset == 0)
                {
                    return data[ByteOffset];
                }
                else if (BitSize == 4 && BitOffset == 0)
                {
                    return data[ByteOffset] & 0x0F;
                }
                else if (BitSize == 4 && BitOffset == 4)
                {
                    return (data[ByteOffset] >> 4) & 0x0F;
                }
                else
                {
                    // Generic bit extraction
                    int totalBitOffset = ByteOffset * 8 + BitOffset;
                    int value = 0;
                    for (int i = 0; i < BitSize; i++)
                    {
                        int byteIdx = (totalBitOffset + i) / 8;
                        int bitIdx = (totalBitOffset + i) % 8;
                        if (byteIdx < length && (data[byteIdx] & (1 << bitIdx)) != 0)
                        {
                            value |= (1 << i);
                        }
                    }
                    return value;
                }
            }

            /// <summary>
            /// Read as normalized float -1.0 to 1.0 (for sticks) or 0.0 to 1.0 (for triggers).
            /// </summary>
            public double ReadNormalized(byte[] data, int length)
            {
                int raw = ReadRaw(data, length);
                double range = LogicalMax - LogicalMin;
                if (range <= 0) return 0.0;
                return ((raw - LogicalMin) / range) * 2.0 - 1.0;
            }

            /// <summary>
            /// Read as byte 0-255 scaled from logical range.
            /// </summary>
            public byte ReadByte(byte[] data, int length)
            {
                int raw = ReadRaw(data, length);
                double range = LogicalMax - LogicalMin;
                if (range <= 0) return 0;
                return (byte)Math.Clamp((int)(((raw - LogicalMin) / range) * 255.0), 0, 255);
            }
        }

        public class ButtonInfo
        {
            public int ByteOffset;
            public int BitOffset;
            public int UsageIndex; // HID button usage number (1-based)
            public string Name = "";

            public bool IsPressed(byte[] data, int length)
            {
                if (ByteOffset >= length) return false;
                return (data[ByteOffset] & (1 << BitOffset)) != 0;
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

                            // Calculate byte/bit offset from DataIndex
                            // The data index tells us the position in the report
                            int bitSize = vc.BitSize;
                            int reportCount = vc.ReportCount;

                            // For value caps, we use the data index to find position.
                            // But we can also compute from the report layout directly.
                            // We'll use HidP_GetUsageValue at runtime for simplicity, but for a
                            // static map we need the byte offset. Use DataIndex-based calculation.
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
                                    BitSize = bitSize,
                                    LogicalMin = vc.LogicalMin,
                                    LogicalMax = vc.LogicalMax,
                                    Name = usageName,
                                };

                                // Compute byte offset. For single-report-ID devices, the report ID byte
                                // shifts all offsets by 1. DataIndex is sequential, so we compute from bit position.
                                // For HID reports WITH a report ID, byte 0 is the report ID, and data starts at byte 1.
                                // We'll resolve exact positions at first-packet time using a simpler method.

                                switch (usage)
                                {
                                    case 0x30: map.LeftStickX = axis; axis.Name = "LeftStickX"; break;   // X
                                    case 0x31: map.LeftStickY = axis; axis.Name = "LeftStickY"; break;   // Y
                                    case 0x32: map.RightStickX = axis; axis.Name = "RightStickX"; break; // Z
                                    case 0x33: map.RightStickY = axis; axis.Name = "RightStickY"; break; // Rz
                                    case 0x34: map.RightStickX ??= axis; axis.Name = "RightStickX (Rx)"; break; // Rx
                                    case 0x35: map.RightStickY ??= axis; axis.Name = "RightStickY (Ry)"; break; // Ry
                                    case 0x39: map.HatSwitch = axis; axis.Name = "HatSwitch"; break;      // Hat Switch
                                }
                            }
                            // Simulation Controls Page (0x02) - sometimes used for triggers
                            else if (usagePage == 0x02)
                            {
                                var axis = new AxisInfo
                                {
                                    BitSize = bitSize,
                                    LogicalMin = vc.LogicalMin,
                                    LogicalMax = vc.LogicalMax,
                                    Name = usageName,
                                };
                                switch (usage)
                                {
                                    case 0xC4: map.RightTrigger = axis; axis.Name = "RightTrigger (Accelerator)"; break;
                                    case 0xC5: map.LeftTrigger = axis; axis.Name = "LeftTrigger (Brake)"; break;
                                }
                            }
                        }
                    }
                }

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
                                        var btn = new ButtonInfo { UsageIndex = u, Name = $"Button {u}" };
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
                                    var btn = new ButtonInfo { UsageIndex = bc.UsageMin, Name = $"Button {bc.UsageMin}" };
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
            // For generic HID gamepads, the byte layout after the report ID is typically:
            // [ReportID] [Axes...] [HatSwitch+Buttons packed] [More buttons...]
            //
            // The HidP_ API gives us DataIndex values which correspond to sequential bit positions
            // in the report. But the exact byte offset depends on the report descriptor's field order.
            //
            // The most robust method: scan the first idle packet for bytes that are at "center" values
            // (0x80 for 8-bit axes, 0x7F sometimes) to identify axis positions.

            bool hasReportId = (length > 0 && firstPacket[0] != 0x00);
            int dataStart = hasReportId ? 1 : 0;

            log?.Invoke($"[HidReportMap] Resolving offsets from first packet (len={length}, reportID=0x{firstPacket[0]:X2})");
            log?.Invoke($"[HidReportMap] Packet: {BitConverter.ToString(firstPacket, 0, Math.Min(length, 20))}...");

            // Strategy: Find centered axes (value ~0x80 = 128) among the first several bytes
            var centeredBytes = new List<int>();
            var nonCenteredBytes = new List<int>();

            for (int i = dataStart; i < Math.Min(length, dataStart + 8); i++)
            {
                if (firstPacket[i] >= 0x78 && firstPacket[i] <= 0x88) // Near center (120-136)
                    centeredBytes.Add(i);
                else
                    nonCenteredBytes.Add(i);
            }

            log?.Invoke($"[HidReportMap] Centered bytes (near 0x80): [{string.Join(", ", centeredBytes)}]");
            log?.Invoke($"[HidReportMap] Non-centered bytes: [{string.Join(", ", nonCenteredBytes)}]");

            // Assign axes to centered byte positions (typically 4 centered bytes for 2 sticks)
            if (centeredBytes.Count >= 4)
            {
                if (LeftStickX != null) LeftStickX.ByteOffset = centeredBytes[0];
                if (LeftStickY != null) LeftStickY.ByteOffset = centeredBytes[1];
                if (RightStickX != null) RightStickX.ByteOffset = centeredBytes[2];
                if (RightStickY != null) RightStickY.ByteOffset = centeredBytes[3];
            }
            else if (centeredBytes.Count >= 2)
            {
                // Only 2 centered bytes — likely just left stick (or sticks with 0-based range)
                if (LeftStickX != null) LeftStickX.ByteOffset = centeredBytes[0];
                if (LeftStickY != null) LeftStickY.ByteOffset = centeredBytes[1];

                // For right stick, look for next bytes after the centered ones
                int nextByte = centeredBytes[centeredBytes.Count - 1] + 1;
                if (RightStickX != null) RightStickX.ByteOffset = nextByte;
                if (RightStickY != null) RightStickY.ByteOffset = nextByte + 1;
            }

            // Hat switch: find the byte after axes that contains a typical hat-centered value (0x08 or 0x0F)
            // or has its low nibble as 8/15
            int axisEnd = (centeredBytes.Count > 0) ? centeredBytes[centeredBytes.Count - 1] + 1 : dataStart;
            for (int i = axisEnd; i < Math.Min(length, axisEnd + 3); i++)
            {
                byte lowNibble = (byte)(firstPacket[i] & 0x0F);
                if (lowNibble == 0x08 || lowNibble == 0x0F)
                {
                    if (HatSwitch != null)
                    {
                        HatSwitch.ByteOffset = i;
                        HatSwitch.BitOffset = 0;
                        if (HatSwitch.BitSize == 0) HatSwitch.BitSize = 4;
                    }

                    // Buttons packed in the same byte's upper nibble or subsequent bytes
                    AssignButtonOffsets(i, firstPacket, length, log);
                    break;
                }
            }

            // Log final resolved mapping
            LogResolvedMap(log);
        }

        private void AssignButtonOffsets(int hatByte, byte[] packet, int length, Action<string>? log)
        {
            // Generic HID gamepad button layout relative to hat switch byte:
            // Hat byte upper nibble + next bytes contain packed button bits.
            //
            // Standard layout for most clones:
            //   hatByte[7:4] = face buttons (square, cross, circle, triangle)
            //   hatByte+1    = shoulder/system buttons (L1, R1, L2, R2, select, start, L3, R3)
            //   hatByte+2    = PS button (bit 0)

            int btnByteBase = hatByte; // Face buttons in upper nibble of hat byte
            int btnByte1 = hatByte + 1; // Shoulder/system
            int btnByte2 = hatByte + 2; // PS/Home

            // Standard button-page assignment:
            // Button 1 = bit4 of hatByte (or varies)
            // We use the standard HID button usage 1-13 ordering:
            // Btn1=Square, Btn2=Cross, Btn3=Circle, Btn4=Triangle,
            // Btn5=L1, Btn6=R1, Btn7=L2, Btn8=R2,
            // Btn9=Select, Btn10=Start, Btn11=L3, Btn12=R3, Btn13=PS

            if (BtnSquare != null) { BtnSquare.ByteOffset = btnByteBase; BtnSquare.BitOffset = 4; }
            if (BtnCross != null) { BtnCross.ByteOffset = btnByteBase; BtnCross.BitOffset = 5; }
            if (BtnCircle != null) { BtnCircle.ByteOffset = btnByteBase; BtnCircle.BitOffset = 6; }
            if (BtnTriangle != null) { BtnTriangle.ByteOffset = btnByteBase; BtnTriangle.BitOffset = 7; }

            if (btnByte1 < length)
            {
                if (BtnL1 != null) { BtnL1.ByteOffset = btnByte1; BtnL1.BitOffset = 0; }
                if (BtnR1 != null) { BtnR1.ByteOffset = btnByte1; BtnR1.BitOffset = 1; }
                if (BtnL2 != null) { BtnL2.ByteOffset = btnByte1; BtnL2.BitOffset = 2; }
                if (BtnR2 != null) { BtnR2.ByteOffset = btnByte1; BtnR2.BitOffset = 3; }
                if (BtnSelect != null) { BtnSelect.ByteOffset = btnByte1; BtnSelect.BitOffset = 4; }
                if (BtnStart != null) { BtnStart.ByteOffset = btnByte1; BtnStart.BitOffset = 5; }
                if (BtnL3 != null) { BtnL3.ByteOffset = btnByte1; BtnL3.BitOffset = 6; }
                if (BtnR3 != null) { BtnR3.ByteOffset = btnByte1; BtnR3.BitOffset = 7; }
            }

            if (btnByte2 < length && BtnPS != null)
            {
                BtnPS.ByteOffset = btnByte2;
                BtnPS.BitOffset = 0;
            }
        }

        private void LogResolvedMap(Action<string>? log)
        {
            if (log == null) return;

            log("[HidReportMap] === Resolved Report Layout ===");
            LogAxis(log, LeftStickX);
            LogAxis(log, LeftStickY);
            LogAxis(log, RightStickX);
            LogAxis(log, RightStickY);
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
            log($"  {axis.Name}: byte[{axis.ByteOffset}] bits={axis.BitSize} range=[{axis.LogicalMin}..{axis.LogicalMax}]");
        }

        private static void LogBtn(Action<string> log, ButtonInfo? btn)
        {
            if (btn == null) return;
            log($"  {btn.Name}: byte[{btn.ByteOffset}] bit={btn.BitOffset}");
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
            if (HatSwitch == null)
            {
                up = right = down = left = false;
                return;
            }
            int hat = HatSwitch.ReadRaw(data, length);
            // Standard hat encoding: 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW, 8+ = centered
            up = hat == 0 || hat == 1 || hat == 7;
            right = hat == 1 || hat == 2 || hat == 3;
            down = hat == 3 || hat == 4 || hat == 5;
            left = hat == 5 || hat == 6 || hat == 7;
        }
    }
}
