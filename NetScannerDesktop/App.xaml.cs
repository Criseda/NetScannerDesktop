using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace NetScannerDesktop
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Crash details land here (%TEMP%\NetScannerDesktop.crash.log): always
        /// writable, packaged or not, and easy for users to find and attach.
        /// </summary>
        public static string CrashLogPath { get; } =
            Path.Combine(Path.GetTempPath(), "NetScannerDesktop.crash.log");

        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// Last-resort diagnostics. Does not mark the exception handled — the
        /// app still terminates as before.
        /// </summary>
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
            WriteCrashLog("Unhandled", e.Exception);

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                m_window = new MainWindow();
                m_window.Activate();
            }
            catch (Exception ex)
            {
                WriteCrashLog("Launch failed", ex);
                throw;
            }
        }

        private static void WriteCrashLog(string label, Exception? exception)
        {
            try
            {
                File.AppendAllText(CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {label}: {exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never throw: it would replace the real exception.
            }
        }

        private Window? m_window;

        public MainWindow? MainAppWindow => m_window as MainWindow;
    }
}
