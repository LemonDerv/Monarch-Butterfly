using System;
using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace PSControllerUI
{
    public enum Xbox360Buttons : ushort
    {
        DpadUp = 0x0001,
        DpadDown = 0x0002,
        DpadLeft = 0x0004,
        DpadRight = 0x0008,
        Start = 0x0010,
        Back = 0x0020,
        LeftThumbClick = 0x0040,
        RightThumbClick = 0x0080,
        LeftShoulder = 0x0100,
        RightShoulder = 0x0200,
        Guide = 0x0400, // Xbox Logo Button
        A = 0x1000,
        B = 0x2000,
        X = 0x4000,
        Y = 0x8000
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XUSB_REPORT
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    public class ViGEmClientWrapper : IDisposable
    {
        private ViGEmClient? _client;
        private IXbox360Controller? _target;

        public bool IsSupported { get; private set; } = true;

        public ViGEmClientWrapper()
        {
            // The NuGet package Nefarius.ViGEm.Client automatically extracts and loads the necessary DLLs.
            // We just check if the driver itself is installed/connectable.
            try
            {
                using (var temp = new ViGEmClient())
                {
                    IsSupported = true;
                }
            }
            catch
            {
                IsSupported = false;
            }
        }

        public bool Connect()
        {
            if (!IsSupported) return false;

            try
            {
                if (_client == null)
                {
                    _client = new ViGEmClient();
                }
                return true;
            }
            catch
            {
                IsSupported = false;
                return false;
            }
        }

        public bool PlugInTarget()
        {
            if (_client == null) return false;

            try
            {
                if (_target == null)
                {
                    _target = _client.CreateXbox360Controller();
                    _target.Connect();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool Update(XUSB_REPORT report)
        {
            if (_target == null) return false;

            try
            {
                // Set Button States
                ushort mask = report.wButtons;
                _target.SetButtonState(Xbox360Button.Up, (mask & 0x0001) != 0);
                _target.SetButtonState(Xbox360Button.Down, (mask & 0x0002) != 0);
                _target.SetButtonState(Xbox360Button.Left, (mask & 0x0004) != 0);
                _target.SetButtonState(Xbox360Button.Right, (mask & 0x0008) != 0);
                _target.SetButtonState(Xbox360Button.Start, (mask & 0x0010) != 0);
                _target.SetButtonState(Xbox360Button.Back, (mask & 0x0020) != 0);
                _target.SetButtonState(Xbox360Button.LeftThumb, (mask & 0x0040) != 0);
                _target.SetButtonState(Xbox360Button.RightThumb, (mask & 0x0080) != 0);
                _target.SetButtonState(Xbox360Button.LeftShoulder, (mask & 0x0100) != 0);
                _target.SetButtonState(Xbox360Button.RightShoulder, (mask & 0x0200) != 0);
                _target.SetButtonState(Xbox360Button.Guide, (mask & 0x0400) != 0);
                _target.SetButtonState(Xbox360Button.A, (mask & 0x1000) != 0);
                _target.SetButtonState(Xbox360Button.B, (mask & 0x2000) != 0);
                _target.SetButtonState(Xbox360Button.X, (mask & 0x4000) != 0);
                _target.SetButtonState(Xbox360Button.Y, (mask & 0x8000) != 0);

                // Set Triggers (Sliders)
                _target.SetSliderValue(Xbox360Slider.LeftTrigger, report.bLeftTrigger);
                _target.SetSliderValue(Xbox360Slider.RightTrigger, report.bRightTrigger);

                // Set Analog Sticks (Axes)
                _target.SetAxisValue(Xbox360Axis.LeftThumbX, report.sThumbLX);
                _target.SetAxisValue(Xbox360Axis.LeftThumbY, report.sThumbLY);
                _target.SetAxisValue(Xbox360Axis.RightThumbX, report.sThumbRX);
                _target.SetAxisValue(Xbox360Axis.RightThumbY, report.sThumbRY);

                _target.SubmitReport();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void UnplugTarget()
        {
            if (_target != null)
            {
                try
                {
                    _target.Disconnect();
                }
                catch { }
                _target = null;
            }
        }

        public void Disconnect()
        {
            UnplugTarget();
            if (_client != null)
            {
                try
                {
                    _client.Dispose();
                }
                catch { }
                _client = null;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
