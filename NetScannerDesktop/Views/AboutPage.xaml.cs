using System;
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.Views;

/// <summary>
/// Static explainer page. Only the engine version is loaded live.
/// </summary>
public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppVersionRun.Text = GetAppVersion();

        try
        {
            string installed =
                await new NetScannerService().GetVersionAsync(CancellationToken.None);
            EngineVersionRun.Text = installed;
            if (EngineUpdater.CompareVersions(installed, EngineUpdater.PinnedVersion) < 0)
            {
                EngineUpdateNote.Text =
                    $"Bundled engine is {EngineUpdater.PinnedVersion} but installed reports {installed}. " +
                    "Run an update check from Discovery, or re-run Scripts/fetch-ns.ps1 -Force.";
                EngineUpdateNote.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
        }
        catch (Exception)
        {
            EngineVersionRun.Text = "not found";
        }

        // Best-effort latest-release note, shared with Discovery's check. Silent when offline.
        EngineRelease? latest = await EngineUpdater.GetLatestReleaseAsync();
        LatestVersionRun.Text = latest?.Tag ?? "unknown";
    }

    private static string GetAppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
        }

        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return asm?.ToString() ?? "dev";
        }
        catch
        {
            return "dev";
        }
    }
}
