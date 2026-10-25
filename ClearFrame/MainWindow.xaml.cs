using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClearFrame;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private readonly ScreenRecorder _recorder = new();
    private bool _isRecording;
    private string _currentFile = "";

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Hide this window from the recording itself
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

        // Default region = full primary screen (in real pixels)
        XBox.Text = "0";
        YBox.Text = "0";
        WidthBox.Text = GetSystemMetrics(0).ToString();   // SM_CXSCREEN
        HeightBox.Text = GetSystemMetrics(1).ToString();  // SM_CYSCREEN
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecording) StartRecording();
        else StopRecording();
    }

    private void StartRecording()
    {
        if (!int.TryParse(XBox.Text, out int x) ||
            !int.TryParse(YBox.Text, out int y) ||
            !int.TryParse(WidthBox.Text, out int w) ||
            !int.TryParse(HeightBox.Text, out int h) ||
            w < 64 || h < 64 || x < 0 || y < 0)
        {
            StatusText.Text = "Please enter valid numbers (width and height at least 64).";
            return;
        }

        int fps = FpsBox.SelectedIndex == 1 ? 60 : 30;

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        Directory.CreateDirectory(folder);
        _currentFile = Path.Combine(folder, $"ClearFrame_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        _recorder.Start(x, y, w, h, fps, _currentFile);

        _isRecording = true;
        RecordButton.Content = "Stop Recording";
        StatusText.Text = "Recording...";
    }

    private void StopRecording()
    {
        _recorder.Stop();

        _isRecording = false;
        RecordButton.Content = "Start Recording";

        StatusText.Text = _recorder.Error is null
            ? $"Saved: {_currentFile}"
            : $"Error: {_recorder.Error}";
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isRecording) _recorder.Stop();
    }
}
