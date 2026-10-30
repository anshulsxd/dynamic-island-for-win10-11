using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Forms = System.Windows.Forms;

namespace IslandEye
{
    /// <summary>
    /// Raises an event when something is plugged in or unplugged:
    /// charger, USB devices, drives / SD cards, headphones & audio devices, monitors, Ethernet cable.
    /// </summary>
    public sealed class DeviceMonitor : IMMNotificationClient, IDisposable
    {
        public event Action<ToastInfo>? Event;   // may be raised on any thread

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_VOLUME = 0x2, DBT_DEVTYP_DEVICEINTERFACE = 0x5;
        private static readonly Guid UsbDeviceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        [StructLayout(LayoutKind.Sequential)]
        private struct DevBroadcastDeviceInterface
        {
            public int Size, DeviceType, Reserved;
            public Guid ClassGuid;
            public short Name;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr filter, int flags);
        [DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        private readonly MMDeviceEnumerator _enum = new();
        private readonly Dictionary<string, string> _names = new();
        private readonly Dictionary<string, DateTime> _recent = new();
        private HwndSource? _src;
        private IntPtr _notify;
        private Timer? _timer;
        private DateTime _quietUntil = DateTime.MaxValue;
        private bool? _lastOnline;
        private int _screens;
        private HashSet<string> _eth = new();
        private bool _audioHooked;

        public void Start(IntPtr hwnd)
        {
            _quietUntil = DateTime.UtcNow.AddSeconds(15);   // ignore the burst of events while Windows is starting up

            // USB devices + drives (WM_DEVICECHANGE)
            _src = HwndSource.FromHwnd(hwnd);
            _src?.AddHook(WndProc);
            var filter = new DevBroadcastDeviceInterface { DeviceType = DBT_DEVTYP_DEVICEINTERFACE, ClassGuid = UsbDeviceGuid };
            filter.Size = Marshal.SizeOf<DevBroadcastDeviceInterface>();
            IntPtr mem = Marshal.AllocHGlobal(filter.Size);
            try
            {
                Marshal.StructureToPtr(filter, mem, false);
                _notify = RegisterDeviceNotificationW(hwnd, mem, 0);
            }
            finally { Marshal.FreeHGlobal(mem); }

            // headphones / audio endpoints
            try
            {
                foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
                {
                    try { _names[d.ID] = d.FriendlyName; } catch { }
                }
                _enum.RegisterEndpointNotificationCallback(this);
                _audioHooked = true;
            }
            catch { }

            // monitors
            _screens = Forms.Screen.AllScreens.Length;
            SystemEvents.DisplaySettingsChanged += OnDisplay;
            SystemEvents.PowerModeChanged += OnPowerMode;

            // charger + ethernet (polled)
            try { _eth = UpEthernet(); } catch { }
            _timer = new Timer(_ => Poll(), null, 2000, 1500);
        }

        public void Dispose()
        {
            try { _timer?.Dispose(); } catch { }
            try { SystemEvents.DisplaySettingsChanged -= OnDisplay; SystemEvents.PowerModeChanged -= OnPowerMode; } catch { }
            try { if (_notify != IntPtr.Zero) UnregisterDeviceNotification(_notify); } catch { }
            try { _src?.RemoveHook(WndProc); } catch { }
            try { if (_audioHooked) _enum.UnregisterEndpointNotificationCallback(this); } catch { }
        }

        private void Raise(ToastInfo t)
        {
            if (DateTime.UtcNow < _quietUntil) return;
            string key = t.App + "|" + t.Title + "|" + t.Body;
            lock (_recent)
            {
                var now = DateTime.UtcNow;
                if (_recent.TryGetValue(key, out var last) && (now - last).TotalSeconds < 3) return;   // de-duplicate
                _recent[key] = now;
                if (_recent.Count > 50)
                    foreach (var k in _recent.Where(p => (now - p.Value).TotalSeconds > 30).Select(p => p.Key).ToList())
                        _recent.Remove(k);
            }
            Event?.Invoke(t);
        }

        // ---------- WM_DEVICECHANGE: drives + USB ----------
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE && lParam != IntPtr.Zero)
            {
                int evt = wParam.ToInt32();
                if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
                {
                    try
                    {
                        bool arrival = evt == DBT_DEVICEARRIVAL;
                        int devType = Marshal.ReadInt32(lParam, 4);
                        if (devType == DBT_DEVTYP_VOLUME)
                        {
                            int mask = Marshal.ReadInt32(lParam, 12);
                            int flags = Marshal.ReadInt16(lParam, 16);
                            OnVolume(arrival, mask, flags);
                        }
                        else if (devType == DBT_DEVTYP_DEVICEINTERFACE)
                        {
                            string? path = Marshal.PtrToStringUni(IntPtr.Add(lParam, 28));
                            if (path != null) OnUsb(arrival, path);
                        }
                    }
                    catch { }
                }
            }
            return IntPtr.Zero;
        }

        private static string FormatSize(long bytes)
        {
            double gb = bytes / 1_000_000_000.0;
            return gb >= 1000 ? $"{gb / 1000:0.#} TB" : gb >= 10 ? $"{gb:0} GB" : $"{gb:0.#} GB";
        }

        private void OnVolume(bool arrival, int mask, int flags)
        {
            if ((flags & 0x2) != 0) return;   // network drive
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                string letter = ((char)('A' + i)) + ":";

                if (!arrival)
                {
                    Raise(new ToastInfo("Storage", $"Drive {letter} removed", "Safe to unplug", "", "⏏️"));
                    continue;
                }

                Task.Run(async () =>
                {
                    await Task.Delay(700);
                    try
                    {
                        var di = new DriveInfo(letter + "\\");
                        string label = di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel) ? di.VolumeLabel : "Local Disk";
                        string size = di.IsReady ? FormatSize(di.TotalSize) : "";
                        string kind = di.DriveType switch
                        {
                            DriveType.Removable => "USB drive",
                            DriveType.CDRom => "Disc",
                            _ => "External drive"
                        };
                        Raise(new ToastInfo(kind, $"{label} ({letter})",
                            size.Length > 0 ? $"{size} · ready to use" : "Connected", "",
                            di.DriveType == DriveType.CDRom ? "💿" : "💾"));
                    }
                    catch { Raise(new ToastInfo("Storage", $"Drive {letter} connected", "Ready to use", "", "💾")); }
                });
            }
        }

        private void OnUsb(bool arrival, string path)
        {
            // \\?\USB#VID_0781&PID_5581#SERIAL#{a5dcbf10-...}
            var parts = path.Split('#');
            if (parts.Length < 3 || !parts[0].EndsWith("USB", StringComparison.OrdinalIgnoreCase)) return;
            string vidpid = parts[1], serial = parts[2];

            Task.Run(async () =>
            {
                if (arrival) await Task.Delay(1500);   // let Windows fill in the device name
                string name = UsbName(vidpid, serial);
                string low = name.ToLowerInvariant();
                if (low.Contains("mass storage") || low.Contains("uasp") || low.Contains("attached scsi")) return;   // shown as a drive instead
                Raise(new ToastInfo("USB", name, arrival ? "Connected" : "Disconnected", "", arrival ? "🔌" : "⏏️"));
            });
        }

        private static string UsbName(string vidpid, string serial)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{vidpid}\{serial}");
                string? v = key?.GetValue("FriendlyName") as string;
                if (string.IsNullOrWhiteSpace(v)) v = key?.GetValue("DeviceDesc") as string;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    int i = v.LastIndexOf(';');
                    if (i >= 0) v = v[(i + 1)..];
                    return v.Trim();
                }
            }
            catch { }
            return "USB device";
        }

        // ---------- audio: headphones, speakers, microphones ----------
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            => Task.Run(() => HandleAudioState(deviceId, newState));

        private void HandleAudioState(string deviceId, DeviceState newState)
        {
            try
            {
                if (newState == DeviceState.Active)
                {
                    var dev = _enum.GetDevice(deviceId);
                    string name = dev.FriendlyName;
                    bool mic = dev.DataFlow == DataFlow.Capture;
                    _names[deviceId] = name;
                    Raise(new ToastInfo("Audio", name, mic ? "Microphone connected" : "Audio device connected", "", mic ? "🎤" : "🎧"));
                }
                else if (newState == DeviceState.Unplugged || newState == DeviceState.NotPresent)
                {
                    if (_names.TryGetValue(deviceId, out var name) && name.Length > 0)
                        Raise(new ToastInfo("Audio", name, "Disconnected", "", "🎧"));
                }
            }
            catch { }
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        // ---------- monitors ----------
        private void OnDisplay(object? sender, EventArgs e)
        {
            try
            {
                int n = Forms.Screen.AllScreens.Length;
                if (n > _screens) Raise(new ToastInfo("Display", "External display connected", $"{n} displays active", "", "🖥️"));
                else if (n < _screens) Raise(new ToastInfo("Display", "Display disconnected", $"{n} display{(n == 1 ? "" : "s")} active", "", "🖥️"));
                _screens = n;
            }
            catch { }
        }

        private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) _quietUntil = DateTime.UtcNow.AddSeconds(20);   // devices re-appear after sleep
        }

        // ---------- charger + ethernet ----------
        private void Poll()
        {
            try
            {
                var ps = Forms.SystemInformation.PowerStatus;
                bool hasBattery = (ps.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) == 0;
                if (hasBattery)
                {
                    bool online = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
                    int pct = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0, 1) * 100);
                    if (_lastOnline.HasValue && online != _lastOnline.Value)
                    {
                        if (online)
                            Raise(new ToastInfo("Power", pct >= 100 ? "Charger connected" : "Charging",
                                pct >= 100 ? "Battery full" : $"Charger connected · {pct}%", "", "⚡"));
                        else
                            Raise(new ToastInfo("Power", "Charger disconnected", $"On battery · {pct}%", "", "🔋"));
                    }
                    _lastOnline = online;
                }
            }
            catch { }

            try
            {
                var now = UpEthernet();
                foreach (var n in now.Except(_eth)) Raise(new ToastInfo("Network", "Ethernet connected", n, "", "🌐"));
                foreach (var n in _eth.Except(now)) Raise(new ToastInfo("Network", "Ethernet cable unplugged", n, "", "🌐"));
                _eth = now;
            }
            catch { }
        }

        private static HashSet<string> UpEthernet() => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                        n.OperationalStatus == OperationalStatus.Up && !IsVirtual(n))
            .Select(n => n.Name)
            .ToHashSet();

        private static bool IsVirtual(NetworkInterface n)
        {
            string d = (n.Description + " " + n.Name).ToLowerInvariant();
            return d.Contains("virtual") || d.Contains("vethernet") || d.Contains("vmware") || d.Contains("hyper-v") ||
                   d.Contains("vbox") || d.Contains("loopback") || d.Contains("bluetooth") || d.Contains("pseudo") ||
                   d.Contains("wsl") || d.Contains("tailscale") || d.Contains("vpn") || d.Contains("zerotier") ||
                   d.Contains("tap-") || d.Contains("tap ");
        }
    }
}
