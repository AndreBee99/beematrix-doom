using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Drawing.Size;

namespace beematrix_doom
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _streamTimer = new DispatcherTimer();
        private bool _isStreaming = false;

        public MainWindow()
        {
            InitializeComponent();

            // Set up terminal logging and state changed events
            BleManager.Instance.LogMessage += LogToTerminal;
            BleManager.Instance.ConnectionStateChanged += OnConnectionStateChanged;

            _streamTimer.Interval = TimeSpan.FromMilliseconds(50); // 20 FPS
            _streamTimer.Tick += OnStreamTimerTick;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            WvDoomPlayer.NavigationCompleted += WvDoomPlayer_NavigationCompleted;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Auto-load config from BeeMatrixDashboard (if it exists)
            LoadTargetMacFromConfig();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _streamTimer.Stop();
            BleManager.Instance.Disconnect();
        }

        private void LoadTargetMacFromConfig()
        {
            try
            {
                string configFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BeeMatrixDashboard"
                );
                string configPath = Path.Combine(configFolder, "config.json");

                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("TargetMac", out var prop))
                        {
                            string mac = prop.GetString() ?? "";
                            if (!string.IsNullOrEmpty(mac))
                            {
                                TxtMacAddress.Text = mac;
                                LogToTerminal($"Loaded saved MAC address from config: {mac}");
                                return;
                            }
                        }
                    }
                }
                TxtMacAddress.Text = "A0:3E:49:90:79:83"; // Default user MAC
            }
            catch (Exception ex)
            {
                LogToTerminal($"Failed to load config: {ex.Message}");
            }
        }

        // Connection Handlers
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string mac = TxtMacAddress.Text.Trim();
            if (string.IsNullOrEmpty(mac) || mac == "00:00:00:00:00:00")
            {
                MessageBox.Show("Please enter a valid Bluetooth LE MAC address first.", "Invalid MAC", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnConnect.IsEnabled = false;
            LogToTerminal($"Connecting to {mac}...");

            bool success = await BleManager.Instance.ConnectByAddressAsync(mac);
            if (success)
            {
                // Put the device into DIY mode
                await BleManager.Instance.SendModeCommandAsync(1);
            }
            else
            {
                MessageBox.Show("Could not connect to the BeeMatrix display. Check BLE range and ensure it is turned on.", "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnConnect.IsEnabled = true;
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            StopStreaming();
            BleManager.Instance.Disconnect();
        }

        private void OnConnectionStateChanged()
        {
            Dispatcher.Invoke(() =>
            {
                bool connected = BleManager.Instance.IsConnected;
                ElStatusIndicator.Fill = new SolidColorBrush(connected ? Color.FromRgb(30, 215, 96) : Color.FromRgb(255, 0, 79));
                BtnConnect.IsEnabled = !connected;
                BtnDisconnect.IsEnabled = connected;
                BtnStream.IsEnabled = connected;

                if (!connected)
                {
                    StopStreaming();
                }
            });
        }

        // Stream Loop
        private void BtnStream_Click(object sender, RoutedEventArgs e)
        {
            if (!_isStreaming)
            {
                StartStreaming();
            }
            else
            {
                StopStreaming();
            }
        }

        private void StartStreaming()
        {
            _isStreaming = true;
            BtnStream.Content = "STOP STREAM";
            BtnStream.Background = new SolidColorBrush(Color.FromRgb(255, 46, 147)); // Hot pink
            _streamTimer.Start();
            LogToTerminal("Started display streaming loop at 20 FPS.");
        }

        private void StopStreaming()
        {
            _isStreaming = false;
            _streamTimer.Stop();
            BtnStream.Content = "START STREAM";
            BtnStream.Background = new SolidColorBrush(Color.FromRgb(21, 21, 34)); // Default grey
            LogToTerminal("Stopped display streaming loop.");
        }

        private void OnStreamTimerTick(object? sender, EventArgs e)
        {
            try
            {
                if (!BleManager.Instance.IsConnected)
                {
                    StopStreaming();
                    return;
                }

                // Check size of the WebView2 element
                double width = WvDoomPlayer.ActualWidth;
                double height = WvDoomPlayer.ActualHeight;
                if (width <= 0 || height <= 0) return;

                // Adjust coordinates and size for Windows DPI screen scaling
                double dpiX = 1.0;
                double dpiY = 1.0;
                var source = PresentationSource.FromVisual(WvDoomPlayer);
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }

                Point screenPos = WvDoomPlayer.PointToScreen(new Point(0, 0));
                int physicalX = (int)(screenPos.X * dpiX);
                int physicalY = (int)(screenPos.Y * dpiY);
                int physicalW = (int)(width * dpiX);
                int physicalH = (int)(height * dpiY);

                if (physicalW <= 0 || physicalH <= 0) return;

                // Capture window graphics using GDI+
                using (var bmp = new Bitmap(physicalW, physicalH))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(physicalX, physicalY, 0, 0, new Size(physicalW, physicalH));
                    }

                    // Resize to 32x32 standard LED panel size
                    using (var resized = new Bitmap(32, 32))
                    {
                        using (var rg = Graphics.FromImage(resized))
                        {
                            rg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bicubic;
                            rg.DrawImage(bmp, 0, 0, 32, 32);
                        }

                        // Compress to PNG bytes
                        using (var ms = new MemoryStream())
                        {
                            resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            byte[] pngData = ms.ToArray();

                            // Push payload in a background Task to ensure UI loop responsiveness
                            Task.Run(async () =>
                            {
                                await BleManager.Instance.SendImagePayloadAsync(pngData);
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Stream capture error: {ex.Message}");
            }
        }

        private async void WvDoomPlayer_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                try
                {
                    string script = @"
                        var style = document.createElement('style');
                        style.innerHTML = 'canvas#DoomGame { width: 100vw !important; height: 100vh !important; object-fit: fill !important; display: block !important; opacity: 1.0 !important; } html, body { overflow: hidden !important; background-color: black !important; }';
                        document.head.appendChild(style);
                        var canvas = document.getElementById('DoomGame');
                        if (canvas) {
                            canvas.focus();
                        }
                    ";
                    await WvDoomPlayer.ExecuteScriptAsync(script);
                    LogToTerminal("Injected fullscreen CSS scaling and focused DOOM canvas.");
                }
                catch (Exception ex)
                {
                    LogToTerminal($"Failed to inject scaling script: {ex.Message}");
                }
            }
        }

        // Terminal Log
        private void LogToTerminal(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLogTerminal.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                TxtLogTerminal.ScrollToEnd();
            });
        }
    }
}