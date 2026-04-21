using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;

namespace WinFormsApp1
{
    public partial class Form1 : Form
    {
        private class CalibrationState
        {
            public List<float> Samples { get; } = new List<float>();
            public bool IsCalibrating { get; set; } = false;
            public DateTime StartTime { get; set; }
            public int DurationMs { get; set; } = 3000;
            public float Baseline { get; private set; } = 0f;

            public void Complete()
            {
                Baseline = Samples.Average();
                IsCalibrating = false;
            }

            public void Reset()
            {
                Samples.Clear();
                IsCalibrating = false;
                Baseline = 0f;
            }
        }

        private class ClipState
        {
            public WaveFileWriter Writer { get; set; } = null;
            public bool IsCapturing { get; set; } = false;
            public DateTime LastNoiseTime { get; set; }
            public int SilenceTimeoutMs { get; set; } = 2000;
    
            public DateTime? NoiseStart { get; set; } = null; // when did current noise begin
            public int MinimumNoiseDurationMs { get; set; } = 500; // must last 500ms to count
        }

        private const string DefaultFilePath = "C:\\Users\\myles\\Music\\sleep recordings";
        
        private WaveInEvent _waveIn;
        
        private CalibrationState _calibration = new CalibrationState();
        private ClipState _clip = new ClipState();
        
        private Button _startRecordingButton;
        private Button _stopRecordingButton;
        private Button _viewRecordingsButton;

        public Form1()
        {
            InitializeComponent();
            InitialiseFormLayout();
        }

        private void InitialiseFormLayout()
        {
            _startRecordingButton = new Button();
            _startRecordingButton.Text = "Start Recording";
            _startRecordingButton.Location = new Point(50, 50);
            _startRecordingButton.Size = new Size(500, 40);
            _startRecordingButton.Click += StartRecordingButton_Click;
            Controls.Add(_startRecordingButton);

            _stopRecordingButton = new Button();
            _stopRecordingButton.Text = "Stop Recording";
            _stopRecordingButton.Location = new Point(600, 50);
            _stopRecordingButton.Size = new Size(500, 40);
            _stopRecordingButton.Click += StopRecordingButton_Click;
            Controls.Add(_stopRecordingButton);

            _viewRecordingsButton = new Button();
            _viewRecordingsButton.Text = "View Recordings";
            _viewRecordingsButton.Location = new Point(1150, 50);
            _viewRecordingsButton.Size = new Size(500, 40);
            _viewRecordingsButton.Click += ViewRecordingsButton_Click;
            Controls.Add(_viewRecordingsButton);
        }

        private void StartRecordingButton_Click(object sender, EventArgs e)
        {
            _startRecordingButton.Text = "Calibrating...";
            StartMonitoring();
        }

        private void StopRecordingButton_Click(object sender, EventArgs e)
        {
            if (_clip.IsCapturing)
                StopClip();

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            _calibration.Reset();
            _startRecordingButton.Text = "Start Recording";
            Console.WriteLine("Recording stopped.");
        }

        private static void ViewRecordingsButton_Click(object sender, EventArgs e)
        {
            Process.Start("explorer.exe", DefaultFilePath);
        }

        private void StartMonitoring()
        {
            _calibration.Reset();
            _calibration.IsCalibrating = true;
            _calibration.StartTime = DateTime.Now;

            _waveIn = new WaveInEvent();
            _waveIn.WaveFormat = new WaveFormat(44100, 1);
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            var volume = GetVolume(e.Buffer, e.BytesRecorded);

            if (_calibration.IsCalibrating)
            {
                Calibrate(volume);
                return;
            }

            double threshold = 0;
            if (_calibration.Baseline < 0.0400)
            {
                 threshold = 0.0400 * 1.5f;
            }
            else
            {
                threshold = _calibration.Baseline * 1.5f;
            }

            if (volume > threshold)
            {
                CheckThreshold(e, volume);
            }
            else if (_clip.IsCapturing)
            {
                _clip.Writer?.Write(e.Buffer, 0, e.BytesRecorded);

                if ((DateTime.Now - _clip.LastNoiseTime).TotalMilliseconds >= _clip.SilenceTimeoutMs)
                    StopClip();
            }
            else
            {
                _clip.NoiseStart = null; // reset if volume dropped below threshold
            }
        }

        private void CheckThreshold(WaveInEventArgs e, float volume)
        {
            if (_clip.NoiseStart == null)
            {
                _clip.NoiseStart = DateTime.Now;
            }

            var noiseLongEnough = (DateTime.Now - _clip.NoiseStart.Value).TotalMilliseconds >= _clip.MinimumNoiseDurationMs;

            if (!noiseLongEnough)
                return;

            Console.WriteLine($"Noise detected! Volume: {volume:F4} Baseline: {_calibration.Baseline:F4}");
            _clip.LastNoiseTime = DateTime.Now;

            if (!_clip.IsCapturing)
            {
                _clip.IsCapturing = true;
                StartClip();
            }

            _clip.Writer.Write(e.Buffer, 0, e.BytesRecorded);
        }

        private void Calibrate(float volume)
        {
            _calibration.Samples.Add(volume);
            if (!((DateTime.Now - _calibration.StartTime).TotalMilliseconds >= _calibration.DurationMs)) return;
            _calibration.Complete();
            _startRecordingButton.Invoke((MethodInvoker)(() => _startRecordingButton.Text = "Recording..."));
            Console.WriteLine($"Calibration done. Baseline: {_calibration.Baseline:F4}");
        }

        private void StartClip()
        {
            if (!Directory.Exists(DefaultFilePath))
                Directory.CreateDirectory(DefaultFilePath);

            var fileName = $"clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav";
            var filePath = Path.Combine(DefaultFilePath, fileName);

            _clip.Writer = new WaveFileWriter(filePath, _waveIn.WaveFormat);
            Console.WriteLine($"Clip started: {fileName}");
        }

        private void StopClip()
        {
            _clip.IsCapturing = false;
            _clip.Writer?.Dispose();
            _clip.Writer = null;
            Console.WriteLine("Clip saved.");
        }

        private static float GetVolume(byte[] buffer, int bytesRecorded)
        {
            var max = 0f;
            for (var i = 0; i < bytesRecorded; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i);
                var abs = Math.Abs(sample / 32768f);
                if (abs > max) max = abs;
            }
            return max;
        }
    }
}