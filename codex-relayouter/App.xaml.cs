using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using codex_bridge.Backend;
using codex_bridge.State;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace codex_bridge
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private static readonly object UnhandledExceptionLogGate = new();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private Window? _window;
        public static BackendServerManager BackendServer { get; } = new();
        public static AppSessionState SessionState { get; } = new();
        public static ConnectionService ConnectionService { get; } = new();
        public static SessionPreferences SessionPreferences { get; } = new();
        public static ChatSessionStore ChatStore { get; } = new();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            AttachGlobalExceptionHandlers();
            SessionState.CurrentSessionChanged += (_, _) => ApplySessionToConnectionDefaults();
        }

        private void AttachGlobalExceptionHandlers()
        {
            UnhandledException += (_, e) =>
            {
                TryAppendUnhandledExceptionLog("WinUI.UnhandledException", e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                TryAppendUnhandledExceptionLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                TryAppendUnhandledExceptionLog("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }

        private static void TryAppendUnhandledExceptionLog(string source, Exception? exception)
        {
            if (exception is null)
            {
                return;
            }

            try
            {
                var path = GetUnhandledExceptionLogPath();
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var entry = $"===== {DateTimeOffset.Now:O} {source} ====={Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";

                lock (UnhandledExceptionLogGate)
                {
                    File.AppendAllText(path, entry, Utf8NoBom);
                }
            }
            catch
            {
            }
        }

        private static string GetUnhandledExceptionLogPath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
            }

            return System.IO.Path.Combine(localAppData, "codex-relayouter", "logs", "winui-unhandled.log");
        }

        private static void ApplySessionToConnectionDefaults()
        {
            var sessionId = SessionState.CurrentSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            var cwd = SessionState.CurrentSessionCwd;
            if (string.IsNullOrWhiteSpace(cwd))
            {
                return;
            }

            ConnectionService.WorkingDirectory = cwd;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _ = BackendServer.EnsureStartedAsync();
            _window = new MainWindow();
            MainWindow = _window;
            _window.Closed += async (_, _) => await BackendServer.StopAsync();
            _window.Activate();
        }

        public static Window? MainWindow { get; private set; }
    }
}
