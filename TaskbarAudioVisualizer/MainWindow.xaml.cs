using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using System.Windows.Shapes;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Forms = System.Windows.Forms;

namespace TaskbarAudioVisualizer;

public partial class MainWindow : Window
{
    private const int BarCount = 40;
    private const int FftSize = 2048;
    private const int AnalysisIntervalMs = 50; // 20 spectrum updates per second
    private const int RenderIntervalMs = 33;   // maximum approximately 30 FPS
    private const double SilenceThreshold = 0.0025;
    private const double StopThreshold = 0.008;
    private static readonly Color BarColor = Color.FromRgb(153, 153, 153);

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _levels = new double[BarCount];
    private readonly double[] _targets = new double[BarCount];
    private readonly float[] _samples = new float[FftSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imag = new double[FftSize];
    private readonly object _audioLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _renderTimer;

    private WasapiLoopbackCapture? _capture;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _batteryOverrideItem;
    private int _writeIndex;
    private long _lastAnalysisMs;
    private long _lastAudibleMs;
    private bool _rendering;
    private bool _hidden;
    private bool _userHidden;
    private bool _manualOverrideOnBattery;
    private bool _powerSourceAllowsRun;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        CreateBars();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(RenderIntervalMs)
        };
        _renderTimer.Tick += RenderFrame;

        Loaded += (_, _) =>
        {
            PositionOverTaskbar();
            MakeClickThrough();
            CreateTrayIcon();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            EvaluatePowerPolicy();
        };
        SizeChanged += (_, _) => LayoutBars();
        Closing += (_, _) => Cleanup();
    }

    private void CreateBars()
    {
        var brush = new SolidColorBrush(BarColor);
        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 4,
                Height = 2,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = brush,
                Opacity = 0.92,
                IsHitTestVisible = false
            };
            _bars[i] = bar;
            BarsCanvas.Children.Add(bar);
        }
        LayoutBars();
    }

    private void LayoutBars()
    {
        if (_bars[0] is null || ActualWidth <= 0) return;

        const double margin = 30;
        double slot = Math.Max(1, ActualWidth - margin * 2) / BarCount;
        double width = Math.Clamp(slot * 0.42, 2, 7);

        for (int i = 0; i < BarCount; i++)
        {
            _bars[i].Width = width;
            Canvas.SetLeft(_bars[i], margin + i * slot + (slot - width) / 2);
        }
    }

    private void PositionOverTaskbar()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out NativeRect rect))
        {
            var work = SystemParameters.WorkArea;
            Left = 0;
            Top = work.Bottom;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = Math.Max(36, SystemParameters.PrimaryScreenHeight - work.Bottom);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        Left = rect.Left / dpi.DpiScaleX;
        Top = rect.Top / dpi.DpiScaleY;
        Width = (rect.Right - rect.Left) / dpi.DpiScaleX;
        Height = (rect.Bottom - rect.Top) / dpi.DpiScaleY;
        LayoutBars();
    }

    private void MakeClickThrough()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle,
            style | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Hide visualizer", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(HideVisualizer)));
        menu.Items.Add("Show visualizer", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowVisualizer)));
        _batteryOverrideItem = new Forms.ToolStripMenuItem("Allow on battery (manual override)")
        {
            CheckOnClick = true,
            Checked = _manualOverrideOnBattery
        };
        _batteryOverrideItem.CheckedChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _manualOverrideOnBattery = _batteryOverrideItem.Checked;
            EvaluatePowerPolicy();
        }));
        menu.Items.Add(_batteryOverrideItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(Close)));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Taskbar Audio Visualizer",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_hidden) ShowVisualizer();
            else HideVisualizer();
        }));
    }

    private void HideVisualizer()
    {
        if (_closing) return;
        _userHidden = true;
        SuspendVisualizer();
    }

    private void ShowVisualizer()
    {
        if (_closing) return;
        _userHidden = false;
        if (!IsOnAcPower()) _manualOverrideOnBattery = true;
        if (_batteryOverrideItem is not null)
            _batteryOverrideItem.Checked = _manualOverrideOnBattery;
        EvaluatePowerPolicy();
    }

    private static bool IsOnAcPower() =>
        Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Online;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.StatusChange or PowerModes.Resume)) return;
        Dispatcher.BeginInvoke(new Action(EvaluatePowerPolicy), DispatcherPriority.Background);
    }

    private void EvaluatePowerPolicy()
    {
        if (_closing) return;

        bool onAc = IsOnAcPower();
        _powerSourceAllowsRun = onAc || _manualOverrideOnBattery;

        // AC power automatically enables the visualizer. On battery it stays off
        // unless the user explicitly enables the tray-menu override.
        if (_powerSourceAllowsRun && !_userHidden)
        {
            if (_hidden)
            {
                _hidden = false;
                PositionOverTaskbar();
                Show();
            }
            StartAudioCapture();
        }
        else
        {
            SuspendVisualizer();
        }

        if (_batteryOverrideItem is not null &&
            _batteryOverrideItem.Checked != _manualOverrideOnBattery)
            _batteryOverrideItem.Checked = _manualOverrideOnBattery;
    }

    private void SuspendVisualizer()
    {
        _hidden = true;
        _renderTimer.Stop();
        _rendering = false;
        StopAudioCapture();
        if (IsVisible) Hide();
    }

    private void StartAudioCapture()
    {
        if (_closing || _capture is not null) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnAudioData;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            _capture?.Dispose();
            _capture = null;
            Forms.MessageBox.Show(
                $"Could not capture system audio. Check your Windows playback device.\n\n{ex.Message}",
                "Taskbar Audio Visualizer",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
        }
    }

    private void StopAudioCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;

        capture.DataAvailable -= OnAudioData;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && !_closing && !_hidden)
            Debug.WriteLine($"Audio capture stopped: {e.Exception.Message}");
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format is null || e.BytesRecorded <= 0) return;

        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = bytesPerSample * format.Channels;
        if (bytesPerSample <= 0 || frameSize <= 0) return;

        double sumSquares = 0;
        int frames = 0;
        bool analyze;
        long now = _clock.ElapsedMilliseconds;

        lock (_audioLock)
        {
            for (int offset = 0; offset + frameSize <= e.BytesRecorded; offset += frameSize)
            {
                float mono = 0;
                for (int channel = 0; channel < format.Channels; channel++)
                    mono += ReadSample(e.Buffer, offset + channel * bytesPerSample, format);

                mono = Math.Clamp(mono / format.Channels, -1f, 1f);
                _samples[_writeIndex] = mono;
                _writeIndex = (_writeIndex + 1) % FftSize;
                sumSquares += mono * mono;
                frames++;
            }

            double rms = frames == 0 ? 0 : Math.Sqrt(sumSquares / frames);
            if (rms >= SilenceThreshold) _lastAudibleMs = now;

            analyze = rms >= SilenceThreshold && now - _lastAnalysisMs >= AnalysisIntervalMs;
            if (analyze)
            {
                AnalyzeSpectrum(format.SampleRate);
                _lastAnalysisMs = now;
            }
        }

        if (analyze && !_hidden && !_closing)
            Dispatcher.BeginInvoke(new Action(EnsureRendering), DispatcherPriority.Background);
    }

    private static float ReadSample(byte[] data, int offset, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32))
            return BitConverter.ToSingle(data, offset);

        return format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(data, offset) / 32768f,
            24 => ReadInt24(data, offset) / 8388608f,
            32 => BitConverter.ToInt32(data, offset) / 2147483648f,
            _ => 0f
        };
    }

    private static int ReadInt24(byte[] data, int offset)
    {
        int value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
        return value;
    }

    private void AnalyzeSpectrum(int sampleRate)
    {
        for (int i = 0; i < FftSize; i++)
        {
            float sample = _samples[(_writeIndex + i) % FftSize];
            double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
            _real[i] = sample * hann;
            _imag[i] = 0;
        }

        Fft(_real, _imag);
        double binHz = (double)sampleRate / FftSize;
        const double minHz = 35;
        double maxHz = Math.Min(16000, sampleRate / 2.0);

        for (int band = 0; band < BarCount; band++)
        {
            double startHz = minHz * Math.Pow(maxHz / minHz, (double)band / BarCount);
            double endHz = minHz * Math.Pow(maxHz / minHz, (double)(band + 1) / BarCount);
            int firstBin = Math.Clamp((int)Math.Floor(startHz / binHz), 1, FftSize / 2 - 1);
            int lastBin = Math.Clamp((int)Math.Ceiling(endHz / binHz), firstBin + 1, FftSize / 2);

            double energy = 0;
            int count = 0;
            for (int bin = firstBin; bin < lastBin; bin++)
            {
                double re = _real[bin];
                double im = _imag[bin];
                energy += re * re + im * im;
                count++;
            }

            double magnitude = count == 0 ? 0 : Math.Sqrt(energy / count);
            _targets[band] = Math.Clamp(Math.Log10(1 + magnitude * 240) / 1.75, 0, 1);
        }
    }

    private static void Fft(double[] real, double[] imag)
    {
        int n = real.Length;
        int j = 0;

        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double stepReal = Math.Cos(angle);
            double stepImag = Math.Sin(angle);
            int half = length >> 1;

            for (int start = 0; start < n; start += length)
            {
                double wr = 1, wi = 0;
                for (int k = 0; k < half; k++)
                {
                    int even = start + k;
                    int odd = even + half;
                    double oddReal = real[odd] * wr - imag[odd] * wi;
                    double oddImag = real[odd] * wi + imag[odd] * wr;

                    real[odd] = real[even] - oddReal;
                    imag[odd] = imag[even] - oddImag;
                    real[even] += oddReal;
                    imag[even] += oddImag;

                    double nextWr = wr * stepReal - wi * stepImag;
                    wi = wr * stepImag + wi * stepReal;
                    wr = nextWr;
                }
            }
        }
    }

    private void EnsureRendering()
    {
        if (_closing || _hidden || _rendering) return;
        _rendering = true;
        _renderTimer.Start();
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (_closing || _hidden)
        {
            _renderTimer.Stop();
            _rendering = false;
            return;
        }

        bool moving = false;
        double maxHeight = Math.Max(8, ActualHeight - 8);
        long now = _clock.ElapsedMilliseconds;
        bool audible = now - _lastAudibleMs < 180;

        lock (_audioLock)
        {
            for (int i = 0; i < BarCount; i++)
            {
                double target = audible ? _targets[i] : 0;
                double smoothing = target > _levels[i] ? 0.42 : 0.16;
                _levels[i] += (target - _levels[i]) * smoothing;
                if (_levels[i] > StopThreshold) moving = true;

                double height = Math.Max(1.5, _levels[i] * maxHeight);
                _bars[i].Height = height;
                Canvas.SetTop(_bars[i], (ActualHeight - height) / 2);
                _bars[i].Opacity = 0.28 + 0.72 * _levels[i];
            }
        }

        if (!moving && !audible)
        {
            _renderTimer.Stop();
            _rendering = false;
        }
    }

    private void Cleanup()
    {
        _closing = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _renderTimer.Stop();
        StopAudioCapture();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
