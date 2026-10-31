using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace IslandEye
{
    public partial class App : Application
    {
        private Mutex? _mutex;
        private bool _ownsMutex;
        private Forms.NotifyIcon? _tray;
        private MainWindow? _window;

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandEye", "log.txt");

        private static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now}] {text}{Environment.NewLine}");
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                Log(args.Exception.ToString());
                Forms.MessageBox.Show("Island error:\n\n" + args.Exception.Message +
                    "\n\nDetails saved to:\n" + LogPath, "Island");
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject.ToString() ?? "unknown");

            try
            {
                // finishing a self-update: wait for the previous copy to exit first
                for (int i = 0; i < e.Args.Length - 1; i++)
                {
                    if (e.Args[i] == "--wait-pid" && int.TryParse(e.Args[i + 1], out int pid))
                    {
                        try { Process.GetProcessById(pid).WaitForExit(15000); } catch { }
                    }
                }
                try
                {
                    string old = (Environment.ProcessPath ?? "") + ".old";
                    if (File.Exists(old)) File.Delete(old);
                }
                catch { }

                // Single instance
                try
                {
                    _mutex = new Mutex(true, "Island_SingleInstance", out bool isNew);
                    _ownsMutex = isNew;
                    if (!isNew)
                    {
                        Forms.MessageBox.Show(
                            "Island is already running.\n\nLook for its icon in the system tray (bottom-right, maybe under the ^ arrow). " +
                            "If you can't see the island, right-click the tray icon > 'Show / Reset island'.\n\n" +
                            "Or close it in Task Manager (Island) and start again.", "Island");
                        Shutdown();
                        return;
                    }
                }
                catch (AbandonedMutexException) { _ownsMutex = true; }

                _window = new MainWindow();
                _window.Show();
                _window.ResetAndShow();

                BuildTray();
                _tray!.ShowBalloonTip(3000, "Island is running",
                    "Look at the top-center of your screen. Right-click this icon for options.", Forms.ToolTipIcon.Info);
                Log("Started OK");
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                Forms.MessageBox.Show("Island failed to start:\n\n" + ex.Message +
                    "\n\nDetails saved to:\n" + LogPath, "Island");
                Shutdown();
            }
        }

        private void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();

            var startup = new Forms.ToolStripMenuItem("Start with Windows")
            {
                Checked = StartupManager.IsEnabled(),
                CheckOnClick = true
            };
            startup.CheckedChanged += (_, _) => StartupManager.Set(startup.Checked);

            var notch = new Forms.ToolStripMenuItem("Notch mode")
            {
                Checked = _window!.NotchMode,
                CheckOnClick = true
            };
            notch.CheckedChanged += (_, _) => _window?.SetNotchMode(notch.Checked);

            var show = new Forms.ToolStripMenuItem("Show / Reset island");
            show.Click += (_, _) => _window?.ResetAndShow();

            var hide = new Forms.ToolStripMenuItem("Hide island");
            hide.Click += (_, _) => { if (_window != null) _window.Visibility = Visibility.Hidden; };

            var test = new Forms.ToolStripMenuItem("Send test notification");
            test.Click += (_, _) => _window?.ShowTestNotification();

            var update = new Forms.ToolStripMenuItem("Check for updates");
            update.Click += (_, _) => _window?.CheckUpdatesFromTray();

            var exit = new Forms.ToolStripMenuItem("Exit");
            exit.Click += (_, _) => Shutdown();

            menu.Items.Add(startup);
            menu.Items.Add(notch);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(show);
            menu.Items.Add(hide);
            menu.Items.Add(test);
            menu.Items.Add(update);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);

            System.Drawing.Icon icon = System.Drawing.SystemIcons.Application;
            try
            {
                var own = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                if (own != null) icon = own;
            }
            catch { }

            _tray = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = "Island",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => _window?.ResetAndShow();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            try { if (_ownsMutex) _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
