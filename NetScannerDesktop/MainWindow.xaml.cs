using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace NetScannerDesktop
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();
        }

        private async void ScanPortsButton_Click(object sender, RoutedEventArgs e)
        {
            // Disable button and show progress ring
            ScanPortsButton.IsEnabled = false;
            PortsProgressRing.IsActive = true;
            PortsProgressRing.Visibility = Visibility.Visible;

            try
            {
                string ipAddress = IpAddressTextBox.Text;
                string portRange = PortRangeTextBox.Text;
                string arguments = $"-p {ipAddress} {portRange}";
                string output = await RunCliToolAsync(arguments);
            }
            finally
            {
                // Re-enable button and hide progress ring
                ScanPortsButton.IsEnabled = true;
                PortsProgressRing.IsActive = false;
                PortsProgressRing.Visibility = Visibility.Collapsed;
            }
        }

        private async void ScanSubnetButton_Click(object sender, RoutedEventArgs e)
        {
            // Disable button and show progress ring
            ScanSubnetButton.IsEnabled = false;
            SubnetProgressRing.IsActive = true;
            SubnetProgressRing.Visibility = Visibility.Visible;

            try
            {
                string subnet = SubnetTextBox.Text;
                string arguments = $"-s {subnet}";
                string output = await RunCliToolAsync(arguments);
            }
            finally
            {
                // Re-enable button and hide progress ring
                ScanSubnetButton.IsEnabled = true;
                SubnetProgressRing.IsActive = false;
                SubnetProgressRing.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<string> RunCliToolAsync(string arguments)
        {
            // Clear any previous output
            DispatcherQueue.TryEnqueue(() => OutputTextBlock.Text = string.Empty);

            var exePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Tools", "ns.exe"));
            Console.WriteLine($"Resolved path: {exePath}");
            var processStartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = processStartInfo })
            {
                var outputBuilder = new System.Text.StringBuilder();
                var errorBuilder = new System.Text.StringBuilder();

                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        outputBuilder.AppendLine(args.Data);
                        // Update UI immediately with each new line
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            OutputTextBlock.Text += args.Data + Environment.NewLine;
                        });
                    }
                };

                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        errorBuilder.AppendLine(args.Data);
                        // Update UI immediately with each new error line
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            OutputTextBlock.Text += "ERROR: " + args.Data + Environment.NewLine;
                        });
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                // Return the complete output as before (useful if you need to process it elsewhere)
                string output = outputBuilder.ToString();
                if (errorBuilder.Length > 0)
                {
                    output += Environment.NewLine + errorBuilder.ToString();
                }

                return output;
            }
        }
    }
}
