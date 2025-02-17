﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

using Microsoft.Win32;

using WinMan.Windows.DllImports;
using static WinMan.Windows.DllImports.Constants;
using static WinMan.Windows.DllImports.NativeMethods;

namespace WinMan.Windows
{
    public class Win32DisplayManager : IDisplayManager
    {
        // TODO: Implements hotplug detection
        public event EventHandler<DisplayChangedEventArgs>? Added;
        public event EventHandler<DisplayChangedEventArgs>? Removed;
#pragma warning disable CS0067
        public event EventHandler<DisplayRectangleChangedEventArgs>? VirtualDisplayBoundsChanged;
#pragma warning restore CS0067
        public event EventHandler<PrimaryDisplayChangedEventArgs>? PrimaryDisplayChanged;

        private static readonly bool IsPerMonitorDPISupported = Environment.OSVersion.Version.Major > 6
            || (Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor >= 3);

        public Rectangle VirtualDisplayBounds
        {
            get
            {
                int x = GetSystemMetrics(GetSystemMetrics_nIndexFlags.SM_XVIRTUALSCREEN);
                int y = GetSystemMetrics(GetSystemMetrics_nIndexFlags.SM_YVIRTUALSCREEN);
                int width = GetSystemMetrics(GetSystemMetrics_nIndexFlags.SM_CXVIRTUALSCREEN);
                int height = GetSystemMetrics(GetSystemMetrics_nIndexFlags.SM_CYVIRTUALSCREEN);

                return new Rectangle(x, y, x + width, y + height);
            }
        }

        public IDisplay PrimaryDisplay { get; private set; }

        public IReadOnlyList<IDisplay> Displays
        {
            get
            {
                lock (m_displays)
                {
                    return m_displays.ToList();
                }
            }
        }

        public IWorkspace Workspace => m_workspace;

        // Appears to be the device name used when the user is not logged in or
        // for RDP connections. I could not find any documentation, and it appears
        // that this device always returns a resoltion of 1024x768.
        // I suspect it is also used when the graphics driver crashes.
        private static readonly string VirtualDeviceName = "WinDisc";

        private const int VirtualDeviceDefaultRefreshRate = 30;

        private readonly Win32Workspace m_workspace;

        private readonly HashSet<Win32Display> m_displays;

        public Win32DisplayManager(Win32Workspace workspace)
        {
            m_workspace = workspace;
            m_displays = new HashSet<Win32Display>(GetMonitors().Select(x => new Win32Display(this, x)));
            PrimaryDisplay = m_displays.First(x => x.Bounds.TopLeft == new Point(0, 0));
        }

        private List<IntPtr> GetMonitors()
        {
            List<IntPtr> monitors = new List<IntPtr>();
            unsafe
            {
                if (!EnumDisplayMonitors(new(), (RECT*)null, delegate (HMONITOR hMonitor, HDC hdcMonitor, RECT* lprcMonitor, LPARAM dwData)
                {
                    if (IsVisibleMonitor(hMonitor))
                    {
                        monitors.Add(hMonitor);
                    }
                    return true;
                }, new LPARAM()))
                {
                    throw new Win32Exception().WithMessage("Could not enumerate the display monitors attached to the system!");
                }
            }
            return monitors;
        }

        internal void OnDisplayChange()
        {
            var addedDisplays = new List<Win32Display>();
            var removedDisplays = new List<Win32Display>();

            IDisplay oldPrimaryDisplay;
            IDisplay newPrimaryDisplay;
            lock (m_displays)
            {
                oldPrimaryDisplay = PrimaryDisplay;
                var newMonitors = GetMonitors();
                var handles = m_displays.Select(x => x.Handle);

                var added = newMonitors.Except(handles).ToList();
                var removed = handles.Except(newMonitors).ToList();

                foreach (var hMonitor in removed)
                {
                    var disp = m_displays.First(x => x.Handle == hMonitor);
                    removedDisplays.Add(disp);
                }
                m_displays.RemoveWhere(x => removed.Contains(x.Handle));

                foreach (var hMonitor in added)
                {
                    var disp = new Win32Display(this, hMonitor);
                    m_displays.Add(disp);
                    addedDisplays.Add(disp);
                }

                newPrimaryDisplay = m_displays.First(x => x.Bounds.TopLeft == new Point(0, 0));
                if (!oldPrimaryDisplay.Equals(newPrimaryDisplay))
                {
                    PrimaryDisplay = newPrimaryDisplay;
                }
            }

            try
            {
                // Added events
                try
                {
                    foreach (var added in addedDisplays)
                    {
                        Added?.Invoke(added, new DisplayChangedEventArgs(added));
                    }
                }
                finally
                {
                    // Removed events
                    foreach (var removed in removedDisplays)
                    {
                        try
                        {
                            removed.OnRemoved();
                        }
                        finally
                        {
                            Removed?.Invoke(removed, new DisplayChangedEventArgs(removed));
                        }
                    }
                }
            }
            finally
            {
                if (!oldPrimaryDisplay.Equals(newPrimaryDisplay))
                {
                    PrimaryDisplayChanged?.Invoke(this, new PrimaryDisplayChangedEventArgs(newPrimaryDisplay, oldPrimaryDisplay));
                }
            }
        }

        internal void OnSettingChange()
        {
            List<Win32Display> displays;
            lock (m_displays)
            {
                displays = m_displays.ToList();
            }

            foreach (var d in displays)
            {
                d.OnSettingChange();
            }
        }

        private bool IsVisibleMonitor(IntPtr hMonitor)
        {
            return (GetMonitorInfo(hMonitor).dwFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) == 0;
        }

        internal (string deviceName, Rectangle workArea, Rectangle bounds, double dpiScale, int refreshRate) GetMonitorSettings(IntPtr hMonitor)
        {
            try
            {
                var (mi, device, refreshRate) = GetMonitorInfoAndSettings(hMonitor);
                var dpiScale = GetDpiScale(hMonitor);
                return (
                    deviceName: device,
                    workArea: new Rectangle(mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom),
                    bounds: new Rectangle(mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom),
                    dpiScale,
                    refreshRate);
            }
            catch (Win32Exception e) when (e.IsInvalidMonitorHandleException() || !IsMonitorValid(hMonitor))
            {
                throw new InvalidDisplayReferenceException(hMonitor);
            }
        }

        private int GetRefreshRate(IntPtr hMonitor)
        {
            var (_, _, refreshRate) = GetMonitorInfoAndSettings(hMonitor);
            return refreshRate;
        }

        private Rectangle GetWorkArea(IntPtr hMonitor)
        {
            var rect = GetMonitorInfo(hMonitor).rcWork;
            return new Rectangle(rect.left, rect.top, rect.right, rect.bottom);
        }

        private Rectangle GetBounds(IntPtr hMonitor)
        {
            var rect = GetMonitorInfo(hMonitor).rcMonitor;
            return new Rectangle(rect.left, rect.top, rect.right, rect.bottom);
        }

        private double GetDpiScale(IntPtr hMonitor)
        {
            if (IsPerMonitorDPISupported)
            {
                try
                {
                    NT_6_3.GetDpiForMonitor(new(hMonitor), MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
                    return dpiX / 96.0;
                }
                catch (Win32Exception e) when (e.IsInvalidMonitorHandleException() || !IsMonitorValid(hMonitor))
                {
                    throw new InvalidDisplayReferenceException(hMonitor);
                }
            }
            else
            {
                return 1.0;
            }
        }

        private MONITORINFO GetMonitorInfo(IntPtr hMonitor)
        {
            MONITORINFO mi = default;
            mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();

            if (!NativeMethods.GetMonitorInfo(new(hMonitor), ref mi))
            {
                try
                {
                    throw new Win32Exception().WithMessage($"Could not read the monitor information for HMONITOR={hMonitor:X8}!");
                }
                catch (Win32Exception e) when (e.IsInvalidMonitorHandleException() || !IsMonitorValid(hMonitor))
                {
                    throw new InvalidDisplayReferenceException(hMonitor, e);
                }
            }

            return mi;
        }

        private (MONITORINFO mi, string device) GetMonitorInfoEx(IntPtr hMonitor)
        {
            unsafe
            {
                MONITORINFOEXW miEx = default;
                MONITORINFO* pmi = (MONITORINFO*)&miEx;
                (*pmi).cbSize = (uint)sizeof(MONITORINFOEXW);
                if (!NativeMethods.GetMonitorInfo(new(hMonitor), pmi))
                {
                    try
                    {
                        throw new Win32Exception().WithMessage($"Could not read the monitor information for HMONITOR={hMonitor:X8}!");
                    }
                    catch (Win32Exception e) when (e.IsInvalidMonitorHandleException() || !IsMonitorValid(hMonitor))
                    {
                        throw new InvalidDisplayReferenceException(hMonitor, e);
                    }
                }

                char* pszDevice = (char*)&miEx.szDevice;
                return (*pmi, new string(pszDevice));
            }
        }

        private (MONITORINFO mi, string device, int refreshRate) GetMonitorInfoAndSettings(IntPtr hMonitor)
        {
            var (mi, device) = GetMonitorInfoEx(hMonitor);
            DEVMODEW devMode = default;
            if (!EnumDisplaySettings(device, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref devMode))
            {
                if (!IsMonitorValid(hMonitor))
                {
                    throw new InvalidDisplayReferenceException(hMonitor);
                }
                if (device == VirtualDeviceName)
                {
                    return (mi, device, GetVirtualMonitorRefreshRate());
                }
                else
                {
                    throw new Win32Exception($"Could not read the settings for monitor \"{device}\".");
                }
            }

            return (mi, device, (int)GetVirtualMonitorRefreshRate());
        }

        private bool IsMonitorValid(IntPtr hMonitor)
        {
            return GetMonitors().Contains(hMonitor);
        }

        private int GetVirtualMonitorRefreshRate()
        {
            try
            {
                using var rdpConfig = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations");
                var frameIntervalString = rdpConfig!.GetValue("DWMFRAMEINTERVAL", null)?.ToString();
                if (frameIntervalString == null)
                    return VirtualDeviceDefaultRefreshRate;
                int frameInterval = int.Parse(frameIntervalString);
                return (int)(1000.0 / frameInterval);
            }
            catch
            {
                return VirtualDeviceDefaultRefreshRate;
            }
        }
    }
}
