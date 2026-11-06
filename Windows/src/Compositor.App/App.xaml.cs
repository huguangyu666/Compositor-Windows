using Microsoft.UI.Xaml;

namespace Compositor.App;

public partial class App : Application
{
    // A stuck disk gets this much of a shutdown or a crash, then the process is allowed to leave.
    private static readonly TimeSpan drainWindow = TimeSpan.FromSeconds(2);

    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        // Start the consumer before anything can fail: its first disk access happens on its own
        // thread, so this cannot delay the window.
        AppLogging.Start();
        // Unpackaged XAML and ResourceLoader need the same language before either loads resources.
        string uiLanguage = UiLanguage.Resolve(System.Globalization.CultureInfo.CurrentUICulture.Name);
#if DEBUG
        uiLanguage = UiLanguage.Resolve(Environment.GetEnvironmentVariable("COMPOSITOR_PROBE_UI_LANGUAGE") ?? uiLanguage);
#endif
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = uiLanguage;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(uiLanguage);
        InitializeComponent();
        // The Mac pins dark for the whole app (NSApp.appearance = darkAqua); setting it at
        // the application level (before any window exists) carries flyouts and dialogs too,
        // where a RootGrid-level RequestedTheme stops short.
        RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += (sender, args) =>
        {
            Log($"Unhandled: {args.Message}\n{args.Exception}", LogLevel.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log($"Fatal: {args.ExceptionObject}", LogLevel.Error);
            // The runtime is leaving, so wait for the records already accepted, bounded: a volume
            // that never answers must not extend a crash.
            AppLogging.Shutdown(drainWindow);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLogging.Shutdown(drainWindow);
    }

    internal static void Log(string message) => AppLogging.Log(message);

    internal static void Log(string message, LogLevel level) => AppLogging.Log(message, level);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // `Compositor.App.exe <path-to-.comp>` opens straight into the project.
            string? projectPath = Environment.GetCommandLineArgs().Skip(1)
                .FirstOrDefault(argument => !argument.StartsWith('-') && Directory.Exists(argument));
            MainAppWindow = projectPath is null ? new MainWindow() : new MainWindow(projectPath);
            MainAppWindow.Activate();
#if DEBUG
            if (Environment.GetCommandLineArgs().Contains("--layer-drag-perf") && MainAppWindow is MainWindow layerDragWindow)
                layerDragWindow.DispatcherQueue.TryEnqueue(async () => await layerDragWindow.RunLayerDragPerfProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--timeline-probe") && MainAppWindow is MainWindow timelineWindow)
                timelineWindow.DispatcherQueue.TryEnqueue(async () => await timelineWindow.RunTimelineProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--localization-probe") && MainAppWindow is MainWindow localizationWindow)
                localizationWindow.DispatcherQueue.TryEnqueue(async () => await localizationWindow.RunLocalizationProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--ai-image-probe") && MainAppWindow is MainWindow aiImageWindow)
                aiImageWindow.DispatcherQueue.TryEnqueue(async () => await aiImageWindow.RunAiImageProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--draft-exit-probe") && MainAppWindow is MainWindow draftExitWindow)
                draftExitWindow.DispatcherQueue.TryEnqueue(async () => await draftExitWindow.RunDraftExitProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--project-save-probe") && MainAppWindow is MainWindow projectSaveWindow)
                projectSaveWindow.DispatcherQueue.TryEnqueue(async () => await projectSaveWindow.RunProjectSaveProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--project-open-probe") && MainAppWindow is MainWindow projectOpenWindow)
                projectOpenWindow.DispatcherQueue.TryEnqueue(async () => await projectOpenWindow.RunProjectOpenProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--direct-outline-probe") && MainAppWindow is MainWindow directOutlineWindow)
                directOutlineWindow.DispatcherQueue.TryEnqueue(async () => await directOutlineWindow.RunDirectOutlineProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--transform-probe") && MainAppWindow is MainWindow transformWindow)
                transformWindow.DispatcherQueue.TryEnqueue(async () => await transformWindow.RunTransformProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--navigation-probe") && MainAppWindow is MainWindow navigationWindow)
                navigationWindow.DispatcherQueue.TryEnqueue(async () => await navigationWindow.RunNavigationProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--workspace-probe") && MainAppWindow is MainWindow workspaceWindow)
                workspaceWindow.DispatcherQueue.TryEnqueue(async () => await workspaceWindow.RunWorkspaceProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--panel-perf") && MainAppWindow is MainWindow probeWindow)
                probeWindow.DispatcherQueue.TryEnqueue(async () => await probeWindow.RunPanelPerformanceProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--effects-perf") && MainAppWindow is MainWindow effectsWindow)
                effectsWindow.DispatcherQueue.TryEnqueue(async () => await effectsWindow.RunEffectsPerformanceProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--logging-probe") && MainAppWindow is MainWindow loggingWindow)
                loggingWindow.DispatcherQueue.TryEnqueue(async () => await loggingWindow.RunLoggingProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--lifetime-probe") && MainAppWindow is MainWindow lifetimeWindow)
                lifetimeWindow.DispatcherQueue.TryEnqueue(async () => await lifetimeWindow.RunLifetimeProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--stress-matrix") && MainAppWindow is MainWindow stressWindow)
                stressWindow.DispatcherQueue.TryEnqueue(async () => await stressWindow.RunStressMatrixAsync());
            if (Environment.GetCommandLineArgs().Contains("--retention-probe") && MainAppWindow is MainWindow retentionWindow)
                retentionWindow.DispatcherQueue.TryEnqueue(async () => await retentionWindow.RunRetentionProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--paint-perf") && MainAppWindow is MainWindow paintWindow)
                paintWindow.DispatcherQueue.TryEnqueue(async () => await paintWindow.RunPaintPerfProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--watcher-probe") && MainAppWindow is MainWindow watcherWindow)
                watcherWindow.DispatcherQueue.TryEnqueue(async () => await watcherWindow.RunWatcherProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--mcp-interop-probe") && MainAppWindow is MainWindow interopWindow)
                interopWindow.DispatcherQueue.TryEnqueue(async () => await interopWindow.RunMcpInteropProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--recovery-probe") && MainAppWindow is MainWindow recoveryWindow)
                recoveryWindow.DispatcherQueue.TryEnqueue(async () => await recoveryWindow.RunRecoveryProbeAsync());
#endif
        }
        catch (Exception ex)
        {
            Log($"Launch failed: {ex}", LogLevel.Error);
            throw;
        }
    }
}
