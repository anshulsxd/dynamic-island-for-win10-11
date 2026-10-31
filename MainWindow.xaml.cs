using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using Windows.Media.Control;
using Forms = System.Windows.Forms;

namespace IslandEye
{
    public partial class MainWindow : Window
    {
        // ---------- Win32 ----------
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        // ---------- Sizes ----------
        private const double CollapsedW = 250, CollapsedH = 36, ExpandedW = 680, ExpandedH = 340;
        private const double NotifW = 430, NotifH = 86;
        // The window itself is only as big as what is on screen (a big transparent window is expensive to redraw)
        private const double SmallWinW = 290, SmallWinH = 60, NotifWinW = 470, NotifWinH = 112, BigWinW = 720, BigWinH = 360;
        private const double BarH = 168;                 // height of the vertical volume / brightness bars
        private const double RingUnits = 27.02;          // battery ring circumference in stroke-thickness units

        // ---------- State ----------
        private bool _expanded, _notch, _lite, _controlsInit, _calcBuilt;
        private int _winMode = -1, _animToken;
        private IntPtr _hwnd;
        private readonly BitmapCache _bitmapCache = new();

        private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
        private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _topTimer = new() { Interval = TimeSpan.FromSeconds(10) };
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(15) };
        private readonly DispatcherTimer _notifTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
        private readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private readonly DispatcherTimer _idleTrimTimer = new() { Interval = TimeSpan.FromMinutes(3) };
        private readonly ObservableCollection<string> _alerts = new();
        private int _clockTicks, _progressTicks;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly AppSettings _settings = AppSettings.Load();

        // Tabs
        private Button? _currentTabBtn;

        // Media
        private GlobalSystemMediaTransportControlsSessionManager? _mgr;
        private GlobalSystemMediaTransportControlsSession? _session;
        private string _lastTrack = "";
        private bool _seeking;

        // Volume / brightness
        private MMDeviceEnumerator? _devEnumInst;
        private MMDeviceEnumerator DevEnum => _devEnumInst ??= new MMDeviceEnumerator();
        private AudioEndpointVolume? _epVol;
        private string _devName = "";
        private bool _draggingVol, _draggingBright, _brightSupported, _brightBusy;
        private int _brightTarget = -1;

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }
        private static readonly Brush VolGradient = Frozen(new LinearGradientBrush(
            Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0x0A, 0x84, 0xFF), 90));
        private static readonly Brush BrightGradient = Frozen(new LinearGradientBrush(
            Color.FromRgb(0xFF, 0xE0, 0x66), Color.FromRgb(0xFF, 0x9F, 0x0A), 90));

        // Radios (Wi-Fi / Bluetooth / airplane)
        private IReadOnlyList<Radio> _radios = Array.Empty<Radio>();
        private bool _radioAccessChecked;
        private RadioAccessStatus _radioAccess = RadioAccessStatus.Allowed;
        private List<RadioKind> _airplaneRestore = new();

        // Calc
        private bool _justEvaluated;

        // Calendar
        private DateTime _calMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

        // Command box
        private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private readonly List<string> _history = new();
        private int _histPos;
        private readonly Encoding _oem = GetOemEncoding();

        // Power menu
        private string? _pendingPower;

        // Popups (notifications + device events)
        private readonly NotificationWatcher _watcher = new();
        private readonly DeviceMonitor _devices = new();
        private readonly Queue<(ToastInfo Info, ImageSource? Icon)> _notifQueue = new();
        private ToastInfo? _currentPopup;
        private bool _notifShowing;

        // Updates
        private ReleaseInfo? _release;
        private bool _installing, _checkFailed;

        private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();
        private static SolidColorBrush Hex(string hex)
        {
            if (!BrushCache.TryGetValue(hex, out var b))
            {
                b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
                b.Freeze();
                BrushCache[hex] = b;
            }
            return b;
        }
        private bool InputFocused => CmdBox.IsKeyboardFocusWithin || CityBox.IsKeyboardFocusWithin;
        public bool NotchMode => _notch;

        public MainWindow()
        {
            InitializeComponent();
            _notch = _settings.NotchMode;
            _lite = _settings.LiteMode ?? DetectLowEnd();
            ApplyShape();
            SetWindowMode(0);

            AlertList.ItemsSource = _alerts;
            AddAlert("Island started" + (_lite ? " (lite mode)" : ""));
            ShowTab("Music");
            UpdateBattery();
            UpdateVolumeUI(0.5, false);
            UpdateBrightUI(0.5);
            WUnitBtn.Content = _settings.Fahrenheit ? "°F" : "°C";
            ApplyUpdateUi();

            // these two only run while the island is expanded (started / stopped in SetExpanded)
            _clockTimer.Tick += (_, _) =>
            {
                UpdateClock();
                if (++_clockTicks % 2 == 0) UpdateBattery();
            };
            _progressTimer.Tick += async (_, _) =>
            {
                if (!_expanded) return;
                _progressTicks++;
                if (MusicPanel.Visibility == Visibility.Visible) UpdateProgress();
                if (ControlsPanel.Visibility == Visibility.Visible)
                {
                    if (!_draggingVol) ReadVolumeUI();
                    if (_progressTicks % 5 == 0)
                    {
                        if (_brightSupported && !_draggingBright && !_brightBusy)
                        {
                            int? b = await Task.Run(ReadBrightness);
                            if (b.HasValue && !_draggingBright) UpdateBrightUI(b.Value / 100.0);
                        }
                        await RefreshRadiosAsync();
                    }
                }
            };

            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (!Island.IsMouseOver && !InputFocused) SetExpanded(false);
            };
            _peekTimer.Tick += (_, _) =>
            {
                _peekTimer.Stop();
                if (!Island.IsMouseOver && !InputFocused) SetExpanded(false);
            };

            _topTimer.Tick += (_, _) => { if (IsVisible && !InputFocused) KeepOnTop(); };
            _topTimer.Start();

            _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
            _weatherTimer.Start();

            _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(true);
            _updateTimer.Start();

            _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); if (!_expanded && !_notifShowing) TrimMemory(); };
            _idleTrimTimer.Tick += (_, _) => { if (!_expanded && !_notifShowing) TrimMemory(); };
            _idleTrimTimer.Start();

            _notifTimer.Tick += (_, _) => { _notifTimer.Stop(); ShowNextNotification(); };
            _watcher.ToastReceived += t => Dispatcher.BeginInvoke(new Action(() => OnToast(t)));
            _watcher.StatusChanged += m => Dispatcher.BeginInvoke(new Action(() => AddAlert(m)));
            _devices.Event += t => Dispatcher.BeginInvoke(new Action(() => OnToast(t)));
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += (_, _) =>
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                _watcher.Dispose();
                _devices.Dispose();
            };

            // heavy things start a little later, so Island appears instantly even on slow PCs
            Loaded += async (_, _) =>
            {
                _ = RefreshWeatherAsync();
                await InitMediaAsync();
                await Task.Delay(2500);
                _watcher.Start();
                try { _devices.Start(_hwnd); }
                catch (Exception ex) { AddAlert("Device monitor problem: " + ex.Message); }
                _ = DelayedUpdateCheckAsync();
                ScheduleTrim();
            };

            SourceInitialized += (_, _) =>
            {
                _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);   // hide from Alt+Tab
            };
        }

        // ================= PERFORMANCE HELPERS =================
        private static bool DetectLowEnd()
        {
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0 && m.ullTotalPhys <= 8_600_000_000UL) return true;
            }
            catch { }
            return Environment.ProcessorCount <= 2;
        }

        public bool LiteMode => _lite;

        public void SetLiteMode(bool on)
        {
            _lite = on;
            _settings.LiteMode = on;
            _settings.Save();
        }

        /// <summary>0 = tiny (collapsed), 1 = notification size, 2 = fully expanded.</summary>
        private void SetWindowMode(int mode)
        {
            if (_winMode == mode) return;
            _winMode = mode;
            (double w, double h) = mode switch
            {
                2 => (BigWinW, BigWinH),
                1 => (NotifWinW, NotifWinH),
                _ => (SmallWinW, SmallWinH)
            };
            Width = w;
            Height = h;
            PositionWindow();
        }

        private void OnDisplayChanged(object? sender, EventArgs e)
            => Dispatcher.BeginInvoke(new Action(PositionWindow));

        private void KeepOnTop()
        {
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        private void ScheduleTrim()
        {
            _trimTimer.Stop();
            _trimTimer.Start();
        }

        /// <summary>Gives memory back to Windows (runs only while the island is idle).</summary>
        public static void TrimMemory()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch { }
        }

        private void FadeTo(UIElement e, double to, int ms, int delay = 0, double? from = null, Action? done = null)
        {
            double cur = e.Opacity;
            e.BeginAnimation(OpacityProperty, null);
            e.Opacity = from ?? cur;
            if (_lite || ms <= 0)
            {
                e.Opacity = to;
                done?.Invoke();
                return;
            }
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay) };
            if (done != null) a.Completed += (_, _) => done();
            e.BeginAnimation(OpacityProperty, a);
        }

        private void PositionWindow()
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        }

        /// <summary>Makes sure the island is visible, centred and on top.</summary>
        public void ResetAndShow()
        {
            Visibility = Visibility.Visible;
            WindowState = WindowState.Normal;
            PositionWindow();
            Topmost = false; Topmost = true;
        }

        // ================= ISLAND <-> NOTCH =================
        private CornerRadius Corner(double r) => _notch ? new CornerRadius(0, 0, r * 0.8, r * 0.8) : new CornerRadius(r);

        private void ApplyShape()
        {
            EarL.Visibility = EarR.Visibility = _notch ? Visibility.Visible : Visibility.Collapsed;
            Island.Margin = new Thickness(0, _notch ? 0 : 6, 0, 0);          // notch is glued to the top edge
            Island.BorderThickness = new Thickness(_notch ? 0 : 1);
            Island.CornerRadius = Corner(_expanded ? 28 : _notifShowing ? 26 : 18);
        }

        public void SetNotchMode(bool on)
        {
            _notch = on;
            _settings.NotchMode = on;
            _settings.Save();
            ApplyShape();
        }

        // ================= CLOCK + BATTERY =================
        private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("h:mm", CultureInfo.InvariantCulture);

        private void UpdateBattery()
        {
            try
            {
                var ps = Forms.SystemInformation.PowerStatus;
                bool noBattery = (ps.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) != 0;
                SleepBtn.Visibility = noBattery ? Visibility.Collapsed : Visibility.Visible;   // Sleep: laptops only
                if (noBattery) { BatteryRing.Visibility = Visibility.Collapsed; return; }
                BatteryRing.Visibility = Visibility.Visible;

                double pct = Math.Clamp(ps.BatteryLifePercent, 0, 1);
                bool charging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
                var green = Hex("#30D158");
                var red = Hex("#FF453A");

                BatteryArc.Stroke = (pct <= 0.2 && !charging) ? red : green;
                BatteryText.Text = ((int)Math.Round(pct * 100)).ToString(CultureInfo.InvariantCulture);
                BatteryText.Foreground = charging ? green : Brushes.White;
                BatteryArc.StrokeDashArray = pct >= 0.995
                    ? new DoubleCollection()
                    : new DoubleCollection { pct * RingUnits, 100 };
            }
            catch { BatteryRing.Visibility = Visibility.Collapsed; SleepBtn.Visibility = Visibility.Collapsed; }
        }

        // ================= EXPAND / COLLAPSE =================
        private void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _collapseTimer.Stop();
            _peekTimer.Stop();
            SetExpanded(true);
        }

        private void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private void Input_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (!Island.IsMouseOver) { _collapseTimer.Stop(); _collapseTimer.Start(); }
        }

        private void Input_MouseDown(object sender, MouseButtonEventArgs e) => Activate();

        private void SetExpanded(bool expand)
        {
            if (_expanded == expand) return;
            _expanded = expand;
            if (expand) CancelNotification();

            if (expand)
            {
                _trimTimer.Stop();
                SetWindowMode(2);                         // make the window big BEFORE the island grows
                ExpandedHeader.Visibility = Visibility.Visible;
                Body.Visibility = Visibility.Visible;
                UpdateClock();
                UpdateBattery();
                Vol(true);
                ReadVolumeUI();
                Body.UpdateLayout();
                UpdateProgress();
                _clockTimer.Start();
                _progressTimer.Start();
            }
            else
            {
                _clockTimer.Stop();
                _progressTimer.Stop();
                ClosePowerMenu();
            }

            AnimateIsland(expand ? ExpandedW : CollapsedW, expand ? ExpandedH : CollapsedH, expand ? 28 : 18,
                expand ? 380 : 260, springy: expand,
                done: expand ? null : () =>
                {
                    if (_expanded || _notifShowing) return;
                    Body.Visibility = Visibility.Collapsed;               // nothing is laid out / drawn while collapsed
                    ExpandedHeader.Visibility = Visibility.Collapsed;
                    SetWindowMode(0);                                     // shrink the window back down
                    ScheduleTrim();
                });

            if (expand)
            {
                if (!_lite) Body.CacheMode = _bitmapCache;                // cheap fade: blend one cached bitmap
                FadeTo(CollapsedHeader, 0, 150);
                FadeTo(ExpandedHeader, 1, 180, 150, from: 0);
                FadeTo(Body, 1, 180, 140, from: 0, done: () => Body.CacheMode = null);
            }
            else
            {
                FadeTo(ExpandedHeader, 0, 120);
                FadeTo(Body, 0, 120);
                FadeTo(CollapsedHeader, 1, 180, 120);
            }
            Body.IsHitTestVisible = expand;
        }

        // Briefly pop open (e.g. when the song changes)
        private void Peek()
        {
            if (_expanded || _notifShowing) return;
            ShowTab("Music");
            SetExpanded(true);
            _peekTimer.Stop();
            _peekTimer.Start();
        }

        // ================= TABS (with bouncing underline) =================
        private void Tab_Click(object sender, RoutedEventArgs e) => ShowTab((string)((Button)sender).Tag);

        private Button TabButton(string name) => name switch
        {
            "Music" => TabMusic,
            "Controls" => TabControls,
            "Calc" => TabCalc,
            "Calendar" => TabCalendar,
            "Weather" => TabWeather,
            "Alerts" => TabAlerts,
            _ => TabVersion
        };

        private void TabStrip_SizeChanged(object sender, SizeChangedEventArgs e) => MoveUnderline(_currentTabBtn, false);

        private void MoveUnderline(Button? btn, bool animate)
        {
            if (btn == null || btn.ActualWidth <= 0 || TabStrip.ActualWidth <= 0) return;
            double x = btn.TranslatePoint(new Point(0, 0), TabStrip).X;
            double w = btn.ActualWidth;

            if (!animate)
            {
                UlT.BeginAnimation(TranslateTransform.XProperty, null);
                TabUnderline.BeginAnimation(WidthProperty, null);
                UlT.X = x;
                TabUnderline.Width = w;
                return;
            }

            // slide with a springy overshoot, then settle
            UlT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 }
            });
            TabUnderline.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }
            });
        }

        private void ShowTab(string name)
        {
            ClosePowerMenu();
            MusicPanel.Visibility = name == "Music" ? Visibility.Visible : Visibility.Collapsed;
            ControlsPanel.Visibility = name == "Controls" ? Visibility.Visible : Visibility.Collapsed;
            CalcPanel.Visibility = name == "Calc" ? Visibility.Visible : Visibility.Collapsed;
            CalPanel.Visibility = name == "Calendar" ? Visibility.Visible : Visibility.Collapsed;
            WeatherPanel.Visibility = name == "Weather" ? Visibility.Visible : Visibility.Collapsed;
            AlertsPanel.Visibility = name == "Alerts" ? Visibility.Visible : Visibility.Collapsed;
            VersionPanel.Visibility = name == "Version" ? Visibility.Visible : Visibility.Collapsed;

            var off = Hex("#8E8E93");
            foreach (var (btn, tag) in new[]
            {
                (TabMusic, "Music"), (TabControls, "Controls"), (TabCalc, "Calc"), (TabCalendar, "Calendar"),
                (TabWeather, "Weather"), (TabAlerts, "Alerts"), (TabVersion, "Version")
            })
                btn.Foreground = name == tag ? Brushes.White : off;

            bool first = _currentTabBtn == null;
            _currentTabBtn = TabButton(name);
            MoveUnderline(_currentTabBtn, animate: !first && _expanded);

            if (name == "Controls")
            {
                Vol(true); ReadVolumeUI();
                _ = RefreshRadiosAsync();
                if (!_controlsInit) { _controlsInit = true; _ = InitBrightnessAsync(); }   // WMI is loaded only if you open this tab
            }
            if (name == "Calc" && !_calcBuilt) { _calcBuilt = true; BuildCalc(); }
            if (name == "Calendar")
            {
                _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                BuildCalendar();
            }
            if (name == "Alerts") ScrollLogToEnd();
            if (name == "Version") ApplyUpdateUi();
        }

        // ================= WEATHER (Open-Meteo, no API key, location chosen by the user) =================
        private static string Inv(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
        private string Unit => _settings.Fahrenheit ? "°F" : "°C";

        private static (string text, string icon) Describe(int code, bool day) => code switch
        {
            0 => day ? ("Sunny", "☀️") : ("Clear night", "🌙"),
            1 => day ? ("Mostly sunny", "🌤️") : ("Mostly clear", "🌙"),
            2 => ("Partly cloudy", day ? "⛅" : "☁️"),
            3 => ("Cloudy", "☁️"),
            45 or 48 => ("Foggy", "🌫️"),
            51 or 53 or 55 => ("Drizzle", "🌦️"),
            56 or 57 => ("Freezing drizzle", "🌧️"),
            61 => ("Light rain", "🌧️"),
            63 => ("Rainy", "🌧️"),
            65 => ("Heavy rain", "🌧️"),
            66 or 67 => ("Freezing rain", "🌧️"),
            71 or 73 or 75 or 77 => ("Snowy", "❄️"),
            80 or 81 or 82 => ("Rain showers", "🌦️"),
            85 or 86 => ("Snow showers", "🌨️"),
            95 => ("Thunderstorm", "⛈️"),
            96 or 99 => ("Thunderstorm, hail", "⛈️"),
            _ => ("Unknown", "🌡️")
        };

        private void ShowNoLocation()
        {
            WCondition.Text = "Set location";
            WTempSmall.Text = "";
            WIconSmall.Text = "📍";
            HeaderIcon.Text = "📍";
            HeaderTemp.Text = "Set location";
            WIconBig.Text = "📍";
            WTempBig.Text = "--";
            WCondBig.Text = "No location set";
            WCityText.Text = "Search your city below";
            WDetail.Text = "";
        }

        private async Task RefreshWeatherAsync()
        {
            if (!_settings.HasLocation) { ShowNoLocation(); return; }
            try
            {
                string unit = _settings.Fahrenheit ? "fahrenheit" : "celsius";
                string wind = _settings.Fahrenheit ? "mph" : "kmh";
                string url = "https://api.open-meteo.com/v1/forecast" +
                    $"?latitude={Inv(_settings.Lat)}&longitude={Inv(_settings.Lon)}" +
                    "&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day" +
                    $"&temperature_unit={unit}&wind_speed_unit={wind}&timezone=auto";

                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var cur = doc.RootElement.GetProperty("current");
                double t = cur.GetProperty("temperature_2m").GetDouble();
                double feels = cur.GetProperty("apparent_temperature").GetDouble();
                double hum = cur.GetProperty("relative_humidity_2m").GetDouble();
                double ws = cur.GetProperty("wind_speed_10m").GetDouble();
                int code = (int)cur.GetProperty("weather_code").GetDouble();
                bool day = (int)cur.GetProperty("is_day").GetDouble() == 1;

                var (text, icon) = Describe(code, day);
                string temp = $"{Math.Round(t).ToString(CultureInfo.InvariantCulture)}{Unit}";

                WCondition.Text = text;
                WTempSmall.Text = temp;
                WIconSmall.Text = icon;
                HeaderIcon.Text = icon;
                HeaderTemp.Text = temp;
                WIconBig.Text = icon;
                WTempBig.Text = temp;
                WCondBig.Text = text;
                WCityText.Text = "📍 " + _settings.City;
                WDetail.Text = $"Feels like {Math.Round(feels).ToString(CultureInfo.InvariantCulture)}°  ·  Humidity {Math.Round(hum).ToString(CultureInfo.InvariantCulture)}%  ·  Wind {Math.Round(ws).ToString(CultureInfo.InvariantCulture)} {(_settings.Fahrenheit ? "mph" : "km/h")}";
            }
            catch
            {
                if (WTempSmall.Text == "")
                {
                    WCondition.Text = "Weather offline";
                    WIconSmall.Text = "⚠️";
                    WCityText.Text = "Check your internet connection";
                }
            }
        }

        private void City_TextChanged(object s, TextChangedEventArgs e)
            => CityHint.Visibility = string.IsNullOrEmpty(CityBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        private async void City_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SearchCityAsync(); }
            else if (e.Key == Key.Escape) Keyboard.ClearFocus();
        }

        private async void Search_Click(object s, RoutedEventArgs e) => await SearchCityAsync();

        private static TextBlock Hint(string text) => new()
        {
            Text = text, Foreground = Hex("#8E8E93"), FontSize = 12, Margin = new Thickness(4, 4, 0, 0)
        };

        private async Task SearchCityAsync()
        {
            string q = CityBox.Text.Trim();
            if (q.Length < 2) return;
            ResultsList.Children.Clear();
            ResultsList.Children.Add(Hint("Searching…"));
            try
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                    Uri.EscapeDataString(q) + "&count=5&language=en&format=json";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                ResultsList.Children.Clear();

                if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.GetArrayLength() == 0)
                {
                    ResultsList.Children.Add(Hint("No city found. Try another spelling."));
                    return;
                }

                foreach (var r in arr.EnumerateArray())
                {
                    string name = r.GetProperty("name").GetString() ?? "";
                    string admin = r.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
                    string country = r.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                    double lat = r.GetProperty("latitude").GetDouble();
                    double lon = r.GetProperty("longitude").GetDouble();
                    string label = string.Join(", ", new[] { name, admin, country }
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());

                    var btn = new Button
                    {
                        Content = label,
                        Style = (Style)FindResource("Pill"),
                        Height = 28,
                        Margin = new Thickness(0, 2, 0, 0),
                        FontSize = 12
                    };
                    btn.Click += async (_, _) => await SetLocationAsync(name, lat, lon);
                    ResultsList.Children.Add(btn);
                }
            }
            catch
            {
                ResultsList.Children.Clear();
                ResultsList.Children.Add(Hint("Couldn't reach the weather service. Check your internet."));
            }
        }

        private async Task SetLocationAsync(string city, double lat, double lon)
        {
            _settings.City = city;
            _settings.Lat = lat;
            _settings.Lon = lon;
            _settings.HasLocation = true;
            _settings.Save();

            CityBox.Clear();
            ResultsList.Children.Clear();
            ResultsList.Children.Add(Hint("Location saved ✓"));
            AddAlert("Weather location set to " + city);
            await RefreshWeatherAsync();
        }

        private async void Unit_Click(object s, RoutedEventArgs e)
        {
            _settings.Fahrenheit = !_settings.Fahrenheit;
            _settings.Save();
            WUnitBtn.Content = _settings.Fahrenheit ? "°F" : "°C";
            await RefreshWeatherAsync();
        }

        // ================= MUSIC (Spotify / any media app) =================
        private async Task InitMediaAsync()
        {
            try
            {
                _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.SessionsChanged += (_, _) => Dispatcher.InvokeAsync(HookSession);
                _mgr.CurrentSessionChanged += (_, _) => Dispatcher.InvokeAsync(HookSession);
                HookSession();
            }
            catch (Exception ex) { AddAlert("Media API unavailable: " + ex.Message); }
        }

        private GlobalSystemMediaTransportControlsSession? PickSession()
        {
            if (_mgr == null) return null;
            foreach (var s in _mgr.GetSessions())
                if (s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                    return s;
            return _mgr.GetCurrentSession();
        }

        private void HookSession()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnMediaChanged;
            }
            _session = PickSession();
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnMediaChanged;
            }
            _ = RefreshMediaAsync();
        }

        private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object args)
            => Dispatcher.InvokeAsync(async () => await RefreshMediaAsync());

        private async Task RefreshMediaAsync()
        {
            if (_session == null)
            {
                SongTitle.Text = "Nothing playing";
                SongArtist.Text = "Open Spotify and hit play";
                Cover.Source = null;
                PlayBtn.Content = "\uE768";
                UpdateProgress();
                return;
            }
            try
            {
                var props = await _session.TryGetMediaPropertiesAsync();
                var info = _session.GetPlaybackInfo();
                bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlayBtn.Content = playing ? "\uE769" : "\uE768";

                SongTitle.Text = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
                SongArtist.Text = string.IsNullOrWhiteSpace(props.Artist) ? "Unknown artist" : props.Artist;

                Cover.Source = null;
                if (props.Thumbnail != null)
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.DecodePixelWidth = 160;                       // tiny decode = far less RAM
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    Cover.Source = bmp;
                }

                string key = props.Title + "|" + props.Artist;
                if (playing && key != _lastTrack && !string.IsNullOrWhiteSpace(props.Title))
                {
                    bool first = _lastTrack == "";
                    _lastTrack = key;
                    AddAlert($"Now playing: {props.Title} — {props.Artist}");
                    if (!first) Peek();
                }
                UpdateProgress();
            }
            catch { /* session may vanish mid-read */ }
        }

        private async void Prev_Click(object s, RoutedEventArgs e) { if (_session != null) await _session.TrySkipPreviousAsync(); }
        private async void Play_Click(object s, RoutedEventArgs e) { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
        private async void Next_Click(object s, RoutedEventArgs e) { if (_session != null) await _session.TrySkipNextAsync(); }

        private static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        private void SetFill(double fraction)
        {
            double w = TrackHit.ActualWidth;
            double fw = Math.Clamp(fraction, 0, 1) * w;
            TrackFill.Width = fw;
            TrackDot.Margin = new Thickness(Math.Clamp(fw - 6, 0, Math.Max(0, w - 12)), 0, 0, 0);
        }

        private void UpdateProgress()
        {
            if (_seeking) return;
            if (_session == null) { ElapsedText.Text = "0:00"; RemainText.Text = "-0:00"; SetFill(0); return; }
            try
            {
                var tl = _session.GetTimelineProperties();
                var info = _session.GetPlaybackInfo();
                var dur = tl.EndTime - tl.StartTime;
                if (dur.TotalSeconds <= 0) { ElapsedText.Text = "0:00"; RemainText.Text = "-0:00"; SetFill(0); return; }

                var pos = tl.Position;
                if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    pos += DateTimeOffset.Now - tl.LastUpdatedTime;
                pos -= tl.StartTime;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (pos > dur) pos = dur;

                ElapsedText.Text = Fmt(pos);
                RemainText.Text = "-" + Fmt(dur - pos);
                SetFill(pos.TotalSeconds / dur.TotalSeconds);
            }
            catch { }
        }

        // ----- timeline: dot appears on hover (bar stays white), click or drag to seek -----
        private void ShowDot(bool show)
            => TrackDot.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(120)));

        private void Track_Enter(object s, System.Windows.Input.MouseEventArgs e) => ShowDot(true);

        private void Track_Leave(object s, System.Windows.Input.MouseEventArgs e)
        {
            if (!_seeking) ShowDot(false);
        }

        private void SeekPreview(System.Windows.Input.MouseEventArgs e)
        {
            double f = Math.Clamp(e.GetPosition(TrackHit).X / Math.Max(1, TrackHit.ActualWidth), 0, 1);
            SetFill(f);
            try
            {
                var tl = _session!.GetTimelineProperties();
                var dur = tl.EndTime - tl.StartTime;
                if (dur <= TimeSpan.Zero) return;
                var pos = TimeSpan.FromTicks((long)(dur.Ticks * f));
                ElapsedText.Text = Fmt(pos);
                RemainText.Text = "-" + Fmt(dur - pos);
            }
            catch { }
        }

        private void Track_Down(object sender, MouseButtonEventArgs e)
        {
            if (_session == null || TrackHit.ActualWidth <= 0) return;
            _seeking = true;
            TrackHit.CaptureMouse();
            ShowDot(true);
            SeekPreview(e);
        }

        private void Track_Move(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_seeking) SeekPreview(e);
        }

        private async void Track_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_seeking) return;
            _seeking = false;
            TrackHit.ReleaseMouseCapture();
            if (!TrackHit.IsMouseOver) ShowDot(false);
            try
            {
                if (_session == null) return;
                var tl = _session.GetTimelineProperties();
                var dur = tl.EndTime - tl.StartTime;
                if (dur <= TimeSpan.Zero) return;
                double f = Math.Clamp(e.GetPosition(TrackHit).X / Math.Max(1, TrackHit.ActualWidth), 0, 1);
                var target = tl.StartTime + TimeSpan.FromTicks((long)(dur.Ticks * f));
                await _session.TryChangePlaybackPositionAsync(target.Ticks);
            }
            catch { }
        }

        // ================= CONTROLS: volume (vertical) =================
        private AudioEndpointVolume? Vol(bool refetch = false)
        {
            try
            {
                if (_epVol == null || refetch)
                {
                    var d = DevEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _devName = d.FriendlyName;
                    _epVol = d.AudioEndpointVolume;
                }
                return _epVol;
            }
            catch { _epVol = null; return null; }
        }

        private void ReadVolumeUI()
        {
            var v = Vol();
            if (v == null) { VolPercent.Text = "N/A"; VolDevice.Text = "No audio device"; return; }
            try { UpdateVolumeUI(v.MasterVolumeLevelScalar, v.Mute); }
            catch { _epVol = null; }
        }

        private void UpdateVolumeUI(double f, bool muted)
        {
            f = Math.Clamp(f, 0, 1);
            VolPercent.Text = muted ? "Muted" : $"{(int)Math.Round(f * 100)}%";
            VolDevice.Text = _devName.Length > 0 ? "🔊 " + _devName : "";
            VolFill.Height = f * BarH;
            VolFill.Background = muted ? Hex("#636366") : VolGradient;
            string glyph = (muted || f <= 0) ? "\uE74F" : "\uE767";
            VolIcon.Text = glyph;
            MuteBtn.Content = glyph;
            MuteBtn.Background = muted ? Hex("#FF9F0A") : Hex("#2B2B2E");
        }

        private void SetVolume(double f)
        {
            f = Math.Clamp(f, 0, 1);
            try
            {
                var v = Vol();
                if (v != null)
                {
                    v.MasterVolumeLevelScalar = (float)f;
                    if (v.Mute && f > 0) v.Mute = false;
                }
            }
            catch { }
            UpdateVolumeUI(f, false);
        }

        private static double FracFromY(double y, double h) => 1.0 - Math.Clamp(y / Math.Max(1, h), 0, 1);

        private void VolTrack_Down(object s, MouseButtonEventArgs e)
        {
            _draggingVol = true;
            VolTrack.CaptureMouse();
            SetVolume(FracFromY(e.GetPosition(VolTrack).Y, VolTrack.ActualHeight));
        }

        private void VolTrack_Move(object s, System.Windows.Input.MouseEventArgs e)
        {
            if (_draggingVol) SetVolume(FracFromY(e.GetPosition(VolTrack).Y, VolTrack.ActualHeight));
        }

        private void VolTrack_Up(object s, MouseButtonEventArgs e)
        {
            _draggingVol = false;
            VolTrack.ReleaseMouseCapture();
        }

        private void VolChip_Click(object s, RoutedEventArgs e)
            => SetVolume(double.Parse((string)((Button)s).Tag, CultureInfo.InvariantCulture) / 100.0);

        private void Mute_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var v = Vol();
                if (v == null) return;
                v.Mute = !v.Mute;
                ReadVolumeUI();
            }
            catch { }
        }

        // ================= CONTROLS: brightness (laptops only) =================
        private static int? ReadBrightness()
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementBaseObject mo in s.Get())
                    return Convert.ToInt32(mo["CurrentBrightness"], CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }

        private static void WriteBrightness(int pct)
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject mo in s.Get())
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)pct });
            }
            catch { }
        }

        private async Task InitBrightnessAsync()
        {
            int? v = await Task.Run(ReadBrightness);
            _brightSupported = v.HasValue;
            BrightPanel.Visibility = _brightSupported ? Visibility.Visible : Visibility.Collapsed;
            if (v.HasValue) UpdateBrightUI(v.Value / 100.0);
        }

        private void UpdateBrightUI(double f)
        {
            f = Math.Clamp(f, 0, 1);
            BrightPercent.Text = $"{(int)Math.Round(f * 100)}%";
            BrightFill.Height = f * BarH;
            BrightFill.Background = BrightGradient;
        }

        private async void SetBrightness(double f)
        {
            int pct = (int)Math.Round(Math.Clamp(f, 0, 1) * 100);
            UpdateBrightUI(pct / 100.0);
            _brightTarget = pct;
            if (_brightBusy) return;
            _brightBusy = true;
            try
            {
                while (_brightTarget >= 0)
                {
                    int v = _brightTarget;
                    _brightTarget = -1;
                    await Task.Run(() => WriteBrightness(v));
                }
            }
            finally { _brightBusy = false; }
        }

        private void BrightTrack_Down(object s, MouseButtonEventArgs e)
        {
            _draggingBright = true;
            BrightTrack.CaptureMouse();
            SetBrightness(FracFromY(e.GetPosition(BrightTrack).Y, BrightTrack.ActualHeight));
        }

        private void BrightTrack_Move(object s, System.Windows.Input.MouseEventArgs e)
        {
            if (_draggingBright) SetBrightness(FracFromY(e.GetPosition(BrightTrack).Y, BrightTrack.ActualHeight));
        }

        private void BrightTrack_Up(object s, MouseButtonEventArgs e)
        {
            _draggingBright = false;
            BrightTrack.ReleaseMouseCapture();
        }

        // ================= CONTROLS: Wi-Fi / Bluetooth / Airplane =================
        private async Task RefreshRadiosAsync()
        {
            try
            {
                if (!_radioAccessChecked)
                {
                    _radioAccess = await Radio.RequestAccessAsync();
                    _radioAccessChecked = true;
                }
                _radios = await Radio.GetRadiosAsync();
            }
            catch { _radios = Array.Empty<Radio>(); }
            UpdateRadioTiles();
        }

        private void SetTile(Button tile, TextBlock status, bool on, string text)
        {
            tile.Background = on ? Hex("#0A84FF") : Hex("#2B2B2E");
            status.Text = text;
            status.Foreground = on ? Hex("#D6E9FF") : Hex("#8E8E93");
        }

        private void UpdateRadioTiles()
        {
            bool blocked = _radioAccessChecked && _radioAccess != RadioAccessStatus.Allowed;
            string Status(Radio? r) => r == null ? "Unavailable" : blocked ? "Blocked" : r.State == RadioState.On ? "On" : "Off";

            var wifi = _radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            var bt = _radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            SetTile(WifiTile, WifiStatus, wifi?.State == RadioState.On, Status(wifi));
            SetTile(BtTile, BtStatus, bt?.State == RadioState.On, Status(bt));

            bool anyRadio = _radios.Any(r => r.Kind is RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband);
            bool airplane = anyRadio && !_radios.Any(r =>
                r.Kind is RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband && r.State == RadioState.On);
            SetTile(AirTile, AirStatus, airplane, !anyRadio ? "Unavailable" : airplane ? "On" : "Off");
        }

        private async void Wifi_Click(object s, RoutedEventArgs e) => await ToggleRadioAsync(RadioKind.WiFi, "ms-settings:network-wifi");
        private async void Bt_Click(object s, RoutedEventArgs e) => await ToggleRadioAsync(RadioKind.Bluetooth, "ms-settings:bluetooth");

        private static void OpenSettings(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        private async Task ToggleRadioAsync(RadioKind kind, string settingsUri)
        {
            var r = _radios.FirstOrDefault(x => x.Kind == kind);
            if (r == null)
            {
                await RefreshRadiosAsync();
                r = _radios.FirstOrDefault(x => x.Kind == kind);
                if (r == null) { AddAlert($"{kind} isn't available on this PC."); return; }
            }
            try
            {
                var res = await r.SetStateAsync(r.State == RadioState.On ? RadioState.Off : RadioState.On);
                if (res != RadioAccessStatus.Allowed) throw new UnauthorizedAccessException(res.ToString());
            }
            catch (Exception ex)
            {
                AddAlert($"Couldn't change {kind}: {ex.Message}. Opening Windows Settings…");
                OpenSettings(settingsUri);
            }
            await Task.Delay(400);
            await RefreshRadiosAsync();
        }

        // "Airplane mode": switches every radio off, and back on (the ones that were on) when toggled again
        private async void Air_Click(object s, RoutedEventArgs e)
        {
            await RefreshRadiosAsync();
            var radios = _radios.Where(r => r.Kind is RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband).ToList();
            if (radios.Count == 0) { AddAlert("No radios found for airplane mode."); return; }

            try
            {
                bool anyOn = radios.Any(r => r.State == RadioState.On);
                if (anyOn)
                {
                    _airplaneRestore = radios.Where(r => r.State == RadioState.On).Select(r => r.Kind).ToList();
                    foreach (var r in radios.Where(r => r.State == RadioState.On)) await r.SetStateAsync(RadioState.Off);
                }
                else
                {
                    var kinds = _airplaneRestore.Count > 0 ? _airplaneRestore : new List<RadioKind> { RadioKind.WiFi, RadioKind.Bluetooth };
                    foreach (var r in radios.Where(r => kinds.Contains(r.Kind))) await r.SetStateAsync(RadioState.On);
                }
            }
            catch (Exception ex)
            {
                AddAlert("Couldn't change airplane mode: " + ex.Message);
                OpenSettings("ms-settings:network-airplanemode");
            }
            await Task.Delay(500);
            await RefreshRadiosAsync();
        }

        // ================= CALCULATOR =================
        private void BuildCalc()
        {
            string[] keys = { "C", "⌫", "(", ")", "7", "8", "9", "/", "4", "5", "6", "*",
                              "1", "2", "3", "-", "0", ".", "=", "+" };
            foreach (var k in keys)
            {
                var b = new Button
                {
                    Content = k,
                    Style = (Style)FindResource("Pill"),
                    Margin = new Thickness(2)
                };
                if (k == "=") b.Background = Hex("#FF9F0A");
                b.Click += (_, _) => CalcPress(k);
                CalcGrid.Children.Add(b);
            }
        }

        // tiny expression evaluator (+ - * / parentheses) - avoids loading the heavy System.Data library
        private static double Evaluate(string s)
        {
            int pos = 0;
            s = s.Replace(" ", "");
            double Expr()
            {
                double v = Term();
                while (pos < s.Length && (s[pos] == '+' || s[pos] == '-'))
                {
                    char op = s[pos++];
                    double r = Term();
                    v = op == '+' ? v + r : v - r;
                }
                return v;
            }
            double Term()
            {
                double v = Factor();
                while (pos < s.Length && (s[pos] == '*' || s[pos] == '/'))
                {
                    char op = s[pos++];
                    double r = Factor();
                    v = op == '*' ? v * r : v / r;
                }
                return v;
            }
            double Factor()
            {
                if (pos >= s.Length) throw new FormatException();
                if (s[pos] == '-') { pos++; return -Factor(); }
                if (s[pos] == '+') { pos++; return Factor(); }
                if (s[pos] == '(')
                {
                    pos++;
                    double v = Expr();
                    if (pos >= s.Length || s[pos] != ')') throw new FormatException();
                    pos++;
                    return v;
                }
                int start = pos;
                while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
                if (start == pos) throw new FormatException();
                return double.Parse(s[start..pos], CultureInfo.InvariantCulture);
            }
            double result = Expr();
            if (pos != s.Length) throw new FormatException();
            return result;
        }

        private void CalcPress(string k)
        {
            string cur = CalcDisplay.Text;
            bool isOp = k is "+" or "-" or "*" or "/";

            switch (k)
            {
                case "C": CalcDisplay.Text = "0"; _justEvaluated = false; return;
                case "⌫":
                    CalcDisplay.Text = cur.Length <= 1 || _justEvaluated ? "0" : cur[..^1];
                    _justEvaluated = false; return;
                case "=":
                    try
                    {
                        var r = Evaluate(cur);
                        CalcDisplay.Text = double.IsNaN(r) || double.IsInfinity(r)
                            ? "Error" : r.ToString("G12", CultureInfo.InvariantCulture);
                    }
                    catch { CalcDisplay.Text = "Error"; }
                    _justEvaluated = true; return;
            }

            if (cur == "Error" || (_justEvaluated && !isOp)) cur = "0";
            _justEvaluated = false;
            CalcDisplay.Text = (cur == "0" && !isOp && k != ".") ? k : cur + k;
        }

        // ================= CALENDAR =================
        private void BuildCalendar()
        {
            var cul = CultureInfo.CurrentCulture;
            var fdow = cul.DateTimeFormat.FirstDayOfWeek;

            CalTitle.Text = _calMonth.ToString("MMMM yyyy", cul);

            CalWeekdays.Children.Clear();
            for (int i = 0; i < 7; i++)
            {
                var d = (DayOfWeek)(((int)fdow + i) % 7);
                CalWeekdays.Children.Add(new TextBlock
                {
                    Text = cul.DateTimeFormat.GetShortestDayName(d),
                    Foreground = Hex("#8E8E93"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            CalDays.Children.Clear();
            int offset = ((int)_calMonth.DayOfWeek - (int)fdow + 7) % 7;
            var start = _calMonth.AddDays(-offset);
            for (int i = 0; i < 42; i++)
            {
                var day = start.AddDays(i);
                bool inMonth = day.Month == _calMonth.Month;
                bool today = day.Date == DateTime.Today;

                var tb = new TextBlock
                {
                    Text = day.Day.ToString(CultureInfo.InvariantCulture),
                    FontSize = 11,
                    FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = today ? Brushes.White : (inMonth ? Hex("#E5E5EA") : Hex("#555558")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                CalDays.Children.Add(new Border
                {
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Background = today ? Hex("#FF453A") : Brushes.Transparent,
                    Child = tb
                });
            }
        }

        private void CalPrev_Click(object s, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); }
        private void CalNext_Click(object s, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); }
        private void CalTitle_Click(object s, MouseButtonEventArgs e)
        {
            _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            BuildCalendar();
        }

        // ================= ALERTS + COMMAND BOX =================
        private static Encoding GetOemEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch { return Encoding.UTF8; }
        }

        private void ScrollLogToEnd()
            => Dispatcher.BeginInvoke(new Action(() => LogScroll.ScrollToEnd()), DispatcherPriority.Background);

        private void AddLine(string text)
        {
            _alerts.Add(text);
            if (_alerts.Count > 120) _alerts.RemoveAt(0);
            ScrollLogToEnd();
        }

        private void AddAlert(string text) => AddLine($"[{DateTime.Now:HH:mm}] {text}");
        private void ClearAlerts_Click(object s, RoutedEventArgs e) => _alerts.Clear();

        private void Cmd_TextChanged(object s, TextChangedEventArgs e)
            => CmdHint.Visibility = string.IsNullOrEmpty(CmdBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        private async void Cmd_KeyDown(object s, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    string c = CmdBox.Text.Trim();
                    CmdBox.Clear();
                    if (c.Length == 0) return;
                    _history.Add(c);
                    _histPos = _history.Count;
                    await RunCommandAsync(c);
                    break;
                case Key.Up:
                    e.Handled = true;
                    if (_histPos > 0) { _histPos--; CmdBox.Text = _history[_histPos]; CmdBox.CaretIndex = CmdBox.Text.Length; }
                    break;
                case Key.Down:
                    e.Handled = true;
                    if (_histPos < _history.Count - 1) { _histPos++; CmdBox.Text = _history[_histPos]; }
                    else { _histPos = _history.Count; CmdBox.Clear(); }
                    CmdBox.CaretIndex = CmdBox.Text.Length;
                    break;
                case Key.Escape:
                    Keyboard.ClearFocus();
                    break;
            }
        }

        private async Task RunCommandAsync(string cmd)
        {
            AddLine($"{_cwd}> {cmd}");
            string lower = cmd.ToLowerInvariant();

            if (lower is "cls" or "clear") { _alerts.Clear(); return; }
            if (lower == "cd") { AddLine(_cwd); return; }
            if (lower.StartsWith("cd "))
            {
                string arg = cmd[3..].Trim();
                if (arg.StartsWith("/d", StringComparison.OrdinalIgnoreCase)) arg = arg[2..].Trim();
                arg = arg.Trim('"');
                try
                {
                    string target = Path.GetFullPath(Path.Combine(_cwd, arg));
                    if (Directory.Exists(target)) _cwd = target;
                    else AddLine("The system cannot find the path specified.");
                }
                catch { AddLine("The directory name is invalid."); }
                return;
            }
            if (cmd.Length == 2 && char.IsLetter(cmd[0]) && cmd[1] == ':')
            {
                if (Directory.Exists(cmd + "\\")) _cwd = cmd.ToUpperInvariant() + "\\";
                else AddLine("The system cannot find the drive specified.");
                return;
            }
            if (lower == "exit") { AddLine("(use the red power button to exit the island)"); return; }

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/d /c " + cmd)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _cwd,
                    StandardOutputEncoding = _oem,
                    StandardErrorEncoding = _oem
                };
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                bool finished = await Task.Run(() => p.WaitForExit(15000));
                if (!finished)
                {
                    try { p.Kill(true); } catch { }
                    AddLine("(command took longer than 15 s and was stopped)");
                }

                await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(1000));
                string output = (outTask.IsCompleted ? outTask.Result : "") +
                                (errTask.IsCompleted ? errTask.Result : "");

                if (output.Length > 0)
                    foreach (var line in output.Replace("\r", "").TrimEnd('\n').Split('\n'))
                        AddLine(line);
            }
            catch (Exception ex) { AddLine("Error: " + ex.Message); }
        }

        // ================= POPUPS: notifications + device events =================
        private void AnimateIsland(double w, double h, double radius, int ms, bool springy = false, Action? done = null)
        {
            int token = ++_animToken;
            Island.CornerRadius = Corner(radius);
            if (_lite) { ms = ms * 2 / 3; springy = false; }

            IEasingFunction ease = springy
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(ms);
            var aw = new DoubleAnimation(w, dur) { EasingFunction = ease };
            var ah = new DoubleAnimation(h, dur) { EasingFunction = ease };
            ah.Completed += (_, _) =>
            {
                if (token != _animToken) return;                  // a newer animation took over
                Island.BeginAnimation(WidthProperty, null);       // release the animation clocks
                Island.BeginAnimation(HeightProperty, null);
                Island.Width = w;
                Island.Height = h;
                done?.Invoke();
            };
            Island.BeginAnimation(WidthProperty, aw);
            Island.BeginAnimation(HeightProperty, ah);
        }

        private void OnToast(ToastInfo t)
        {
            string app = t.App;
            string emoji = t.Emoji;
            ImageSource? icon = null;

            if (!string.IsNullOrEmpty(t.AppId))
            {
                var (name, img) = ShellApps.Resolve(t.AppId);   // the app's real name + icon
                if (!string.IsNullOrWhiteSpace(name)) app = name!;
                icon = img;
                if (icon == null && t.AppId.StartsWith("Windows.SystemToast", StringComparison.OrdinalIgnoreCase))
                {
                    app = "Windows";
                    emoji = "🖥️";
                }
            }
            if (app.Equals("Island", StringComparison.OrdinalIgnoreCase)) return;   // ignore our own tray balloon

            var item = t with { App = app, Emoji = emoji };
            AddLine($"[{DateTime.Now:HH:mm}] {emoji} {app}: {t.Title}" + (t.Body.Length > 0 ? " — " + t.Body : ""));

            if (_expanded) return;                      // you're already looking at the island
            if (_notifQueue.Count < 5) _notifQueue.Enqueue((item, icon));
            if (!_notifShowing) ShowNextNotification();
        }

        public void ShowTestNotification()
            => OnToast(new ToastInfo("Test app", "Test notification", "If you can see this, island popups are working."));

        private void ShowNextNotification()
        {
            if (_expanded) { CancelNotification(); return; }
            if (_notifQueue.Count == 0) { EndNotification(); return; }

            var (info, icon) = _notifQueue.Dequeue();
            _currentPopup = info;
            NotifApp.Text = info.App;
            NotifTitle.Text = info.Title;
            NotifBody.Text = info.Body;
            NotifBody.Visibility = info.Body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            NotifEmoji.Text = info.Emoji;
            NotifIcon.Source = icon;
            NotifIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            NotifEmoji.Visibility = icon != null ? Visibility.Collapsed : Visibility.Visible;

            if (!_notifShowing)
            {
                _notifShowing = true;
                _trimTimer.Stop();
                if (_winMode == 0) SetWindowMode(1);              // medium-sized window just for the popup
                AnimateIsland(NotifW, NotifH, 26, 320);
                FadeTo(CollapsedHeader, 0, 120);
                NotifCard.IsHitTestVisible = true;
                FadeTo(NotifCard, 1, 200, 120, from: 0);
            }
            _notifTimer.Stop();
            _notifTimer.Start();
        }

        private void EndNotification()
        {
            _notifTimer.Stop();
            if (!_notifShowing) return;
            _notifShowing = false;
            NotifCard.IsHitTestVisible = false;
            FadeTo(NotifCard, 0, 120);
            if (!_expanded)
            {
                AnimateIsland(CollapsedW, CollapsedH, 18, 280, done: () =>
                {
                    if (_expanded || _notifShowing) return;
                    SetWindowMode(0);
                    ScheduleTrim();
                });
                FadeTo(CollapsedHeader, 1, 180, 150);
            }
        }

        // user hovered the island: stop the popup, the island expands normally instead
        private void CancelNotification()
        {
            _notifQueue.Clear();
            _notifTimer.Stop();
            if (!_notifShowing) return;
            _notifShowing = false;
            NotifCard.IsHitTestVisible = false;
            FadeTo(NotifCard, 0, 100);
        }

        private void NotifCard_Click(object s, MouseButtonEventArgs e)
        {
            bool isUpdate = _currentPopup?.Kind == "update";
            _notifQueue.Clear();
            EndNotification();
            if (isUpdate) { ShowTab("Version"); SetExpanded(true); }
        }

        // ================= UPDATES (GitHub Releases) =================
        private async Task DelayedUpdateCheckAsync()
        {
            await Task.Delay(15000);
            await CheckForUpdateAsync(true);
        }

        public async void CheckUpdatesFromTray()
        {
            await CheckForUpdateAsync(false);
            ShowTab("Version");
            if (!_expanded && !_notifShowing)
            {
                SetExpanded(true);
                _peekTimer.Stop();
                _peekTimer.Start();
            }
        }

        private async void CheckNow_Click(object s, RoutedEventArgs e) => await CheckForUpdateAsync(false);

        private static string Summarize(string notes)
        {
            string line = notes.Replace("\r", "").Split('\n')
                .Select(l => l.Trim().TrimStart('#', '-', '*', ' '))
                .FirstOrDefault(l => l.Length > 0) ?? "";
            return line.Length > 140 ? line[..140] + "…" : line;
        }

        private void SetInstall(bool enabled, string text)
        {
            InstallBtn.IsEnabled = enabled;
            InstallBtn.Background = enabled ? Hex("#30D158") : Hex("#2B2B2E");
            InstallBtn.Foreground = enabled ? Brushes.White : Hex("#636366");
            InstallBtn.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
            InstallText.Text = text;
        }

        private void ApplyUpdateUi()
        {
            CurVersion.Text = $"Current version {AppInfo.VersionText}";
            if (_installing) return;

            if (!AppInfo.RepoConfigured)
            {
                UpdTitle.Text = "Updates aren't set up yet";
                UpdDetail.Text = "Set GitHubRepo in AppInfo.cs to your GitHub repository, then rebuild.";
                SetInstall(false, "Install update");
                return;
            }
            if (_release != null && _release.Version > AppInfo.Version)
            {
                UpdTitle.Text = $"Update available — v{_release.Version.ToString(3)}";
                UpdDetail.Text = Summarize(_release.Notes);
                SetInstall(true, "Install update");
            }
            else
            {
                UpdTitle.Text = _checkFailed ? "Couldn't check for updates" : "You're up to date";
                UpdDetail.Text = _checkFailed ? "Check your internet connection and try again." : "Island is running the latest version.";
                SetInstall(false, "Up to date");
            }
        }

        public async Task CheckForUpdateAsync(bool announce)
        {
            if (_installing) return;
            if (!AppInfo.RepoConfigured) { ApplyUpdateUi(); return; }

            UpdTitle.Text = "Checking for updates…";
            try { _release = await UpdateChecker.GetLatestAsync(); _checkFailed = false; }
            catch { _checkFailed = true; }
            ApplyUpdateUi();

            if (announce && !_checkFailed && _release != null && _release.Version > AppInfo.Version &&
                _settings.LastAnnouncedUpdate != _release.Tag)
            {
                _settings.LastAnnouncedUpdate = _release.Tag;
                _settings.Save();
                OnToast(new ToastInfo("Island update", "Update available",
                    $"Version {_release.Version.ToString(3)} is ready — tap to install", "", "⬇️", "update"));
            }
        }

        private async void Install_Click(object s, RoutedEventArgs e)
        {
            if (_release == null || _installing) return;
            _installing = true;
            SetInstall(false, "Downloading…");
            UpdProgressHost.Visibility = Visibility.Visible;
            UpdProgress.Width = 0;
            UpdTitle.Text = $"Downloading v{_release.Version.ToString(3)}";
            UpdDetail.Text = "0%";
            try
            {
                var progress = new Progress<double>(f =>
                {
                    UpdProgress.Width = UpdProgressHost.ActualWidth * Math.Clamp(f, 0, 1);
                    UpdDetail.Text = $"{(int)(f * 100)}%";
                });
                await UpdateChecker.InstallAsync(_release, progress);
                UpdTitle.Text = "Restarting…";
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _installing = false;
                UpdProgressHost.Visibility = Visibility.Collapsed;
                ApplyUpdateUi();
                UpdDetail.Text = ex is UnauthorizedAccessException
                    ? "Windows won't let Island replace itself in this folder. Move Island.exe to a normal folder (e.g. C:\\Apps\\Island) and try again."
                    : "Update failed: " + ex.Message;
                AddAlert("Update failed: " + ex.Message);
            }
        }

        // ================= POWER MENU =================
        private void PowerBtn_Click(object s, RoutedEventArgs e)
        {
            if (PowerMenu.Visibility == Visibility.Visible) ClosePowerMenu();
            else
            {
                PowerList.Visibility = Visibility.Visible;
                PowerConfirm.Visibility = Visibility.Collapsed;
                PowerMenu.Visibility = Visibility.Visible;
            }
        }

        private void ClosePowerMenu()
        {
            PowerMenu.Visibility = Visibility.Collapsed;
            PowerList.Visibility = Visibility.Visible;
            PowerConfirm.Visibility = Visibility.Collapsed;
            _pendingPower = null;
        }

        private void PowerOption_Click(object s, RoutedEventArgs e)
        {
            string tag = (string)((Button)s).Tag;
            if (tag == "exit") { Application.Current.Shutdown(); return; }

            _pendingPower = tag;
            ConfirmText.Text = tag switch
            {
                "shutdown" => "Shut down this PC now?",
                "restart" => "Restart this PC now?",
                _ => "Put this PC to sleep?"
            };
            PowerList.Visibility = Visibility.Collapsed;
            PowerConfirm.Visibility = Visibility.Visible;
        }

        private void PowerCancel_Click(object s, RoutedEventArgs e) => ClosePowerMenu();

        private void PowerYes_Click(object s, RoutedEventArgs e)
        {
            string? action = _pendingPower;
            ClosePowerMenu();
            try
            {
                switch (action)
                {
                    case "shutdown":
                        Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                    case "restart":
                        Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                    case "sleep":
                        Forms.Application.SetSuspendState(Forms.PowerState.Suspend, false, false);
                        break;
                }
            }
            catch (Exception ex) { AddAlert("Power action failed: " + ex.Message); }
        }
    }
}
