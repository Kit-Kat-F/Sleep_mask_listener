using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;
using NWaves.Transforms;
using NWaves.Windows;

namespace WinFormsApp1
{
    public partial class Sleep_recorder : Form
    {
        private class CalibrationState
        {
            public bool IsCalibrating { get; set; } = false;
            public DateTime StartTime { get; set; }
            public int DurationMs { get; set; } = 3000;
            public float[] NoiseProfile { get; private set; } = null;
            public List<float[]> FftFrames { get; } = new List<float[]>();
            public float VolumeBaseline { get; private set; } = 0f;
            public float[] FftBuffer { get; } = new float[1024];
            public int FftBufferIndex { get; set; } = 0;

            private List<float> _volumeSamples = new List<float>();

            public bool IsCalibrated => NoiseProfile != null;

            public void AddVolumeSample(float volume) => _volumeSamples.Add(volume);

            public void Complete()
            {
                int binCount = FftFrames[0].Length;
                NoiseProfile = new float[binCount];
                foreach (var frame in FftFrames)
                    for (int i = 0; i < binCount; i++)
                        NoiseProfile[i] += frame[i];
                for (int i = 0; i < binCount; i++)
                    NoiseProfile[i] /= FftFrames.Count;

                VolumeBaseline = _volumeSamples.Average();
                IsCalibrating = false;
            }

            public void Reset()
            {
                IsCalibrating = false;
                NoiseProfile = null;
                FftFrames.Clear();
                VolumeBaseline = 0f;
                FftBufferIndex = 0;
                _volumeSamples.Clear();
            }
        }

        private class ClipState
        {
            public WaveFileWriter Writer { get; set; } = null;
            public bool IsCapturing { get; set; } = false;
            public DateTime LastNoiseTime { get; set; }
            public int SilenceTimeoutMs { get; set; } = 2000;
            public DateTime? NoiseStart { get; set; } = null;
            public int MinimumNoiseDurationMs { get; set; } = 500;
        }

        private const string DefaultFilePath = "";
        private const int FftSize = 1024;

        private WaveInEvent _waveIn;
        private CalibrationState _calibration = new CalibrationState();
        private ClipState _clip = new ClipState();

        private Button _startRecordingButton;
        private Button _stopRecordingButton;
        private Button _viewRecordingsButton;

        public Sleep_recorder()
        {
            InitializeComponent();
            InitialiseFormLayout();
        }
        
        private void StyleButton(Button button)
        {
            BackgroundImage = Image.FromFile("sleepykitten.png");
            BackgroundImageLayout = ImageLayout.Zoom; // or Tile, Center, Zoom
            
            
            button.BackgroundImage = Image.FromFile(".\\empty_large_normal.png");
            button.BackgroundImageLayout = ImageLayout.Stretch;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.BackColor = Color.Transparent;
            button.Text = "";
            button.Cursor = Cursors.Hand;
        }

        private void InitialiseFormLayout()
        {
            _startRecordingButton = new Button();
            StyleButton(_startRecordingButton);
            _startRecordingButton.Text = "Start Recording";
            _startRecordingButton.Location = new Point(50, 400);
            _startRecordingButton.Size = new Size(250, 40);
            _startRecordingButton.Click += StartRecordingButton_Click;
            Controls.Add(_startRecordingButton);

            _stopRecordingButton = new Button();
            StyleButton(_stopRecordingButton);
            _stopRecordingButton.Text = "Stop Recording";
            _stopRecordingButton.Location = new Point(350, 400);
            _stopRecordingButton.Size = new Size(250, 40);
            _stopRecordingButton.Click += StopRecordingButton_Click;
            Controls.Add(_stopRecordingButton);

            _viewRecordingsButton = new Button();
            StyleButton(_viewRecordingsButton);
            _viewRecordingsButton.Text = "View Recordings";
            _viewRecordingsButton.Location = new Point(650, 400);
            _viewRecordingsButton.Size = new Size(250, 40);
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

        private float[] ProcessAudio(float[] samples)
        {
            if (!_calibration.IsCalibrated)
                return samples;

            var sampleCount = samples.Length;
            var outputSamples = new float[sampleCount];
            var windowSum = new float[sampleCount];
            var hopSize = FftSize / 2;

            var window = Window.OfType(WindowType.Hann, FftSize);

            for (int i = 0; i + FftSize <= sampleCount; i += hopSize)
            {
                var real = new float[FftSize];
                var imag = new float[FftSize];
                var output = new float[FftSize];

                for (int j = 0; j < FftSize; j++)
                    real[j] = samples[i + j] * window[j];

                var fft = new RealFft(FftSize);
                fft.Direct(real, real, imag);

                var subtractionStrength = 0.7f;
                var spectralFloor = 0.1f;

                for (var j = 0; j < FftSize / 2; j++)
                {
                    var magnitude = (float)Math.Sqrt(real[j] * real[j] + imag[j] * imag[j]);
                    var noiseMagnitude = _calibration.NoiseProfile[j] * subtractionStrength;
                    var cleanedMagnitude = Math.Max(magnitude * spectralFloor, magnitude - noiseMagnitude);
                    var scale = magnitude > 0 ? cleanedMagnitude / magnitude : 0;
                    real[j] *= scale;
                    imag[j] *= scale;
                }

                fft.Inverse(real, imag, output);

                for (var j = 0; j < FftSize && i + j < sampleCount; j++)
                {
                    outputSamples[i + j] += (output[j] / FftSize) * window[j];
                    windowSum[i + j] += window[j] * window[j];
                }
            }

            for (var i = 0; i < sampleCount; i++)
            {
                if (windowSum[i] > 0.0001f)
                    outputSamples[i] /= windowSum[i];
                else
                    outputSamples[i] = samples[i];
            }

            return outputSamples;
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (_calibration.IsCalibrating)
            {
                Calibrate(e.Buffer, e.BytesRecorded);
                return;
            }

            // use RAW samples for volume/threshold detection
            float[] rawSamples = BufferToSamples(e.Buffer, e.BytesRecorded);
            var volume = GetVolumeFromSamples(rawSamples);
            var threshold = Math.Max(0.04f, _calibration.VolumeBaseline * 1.5f);

            // use CLEANED samples for writing to file
            float[] cleanedSamples = ProcessAudio(rawSamples);
            byte[] cleanedBytes = SamplesToBytes(cleanedSamples);

            if (volume > threshold)
                CheckThreshold(cleanedBytes, volume);
            else if (_clip.IsCapturing)
            {
                _clip.Writer?.Write(cleanedBytes, 0, cleanedBytes.Length);
                if ((DateTime.Now - _clip.LastNoiseTime).TotalMilliseconds >= _clip.SilenceTimeoutMs)
                    StopClip();
            }
            else
            {
                _clip.NoiseStart = null;
            }
        }

        private void CheckThreshold(byte[] cleanedBytes, float volume)
        {
            if (_clip.NoiseStart == null)
                _clip.NoiseStart = DateTime.Now;

            var noiseLongEnough = (DateTime.Now - _clip.NoiseStart.Value).TotalMilliseconds >= _clip.MinimumNoiseDurationMs;

            if (!noiseLongEnough)
                return;

            Console.WriteLine($"Noise detected! Volume: {volume:F4} Baseline: {_calibration.VolumeBaseline:F4}");
            _clip.LastNoiseTime = DateTime.Now;

            if (!_clip.IsCapturing)
            {
                _clip.IsCapturing = true;
                StartClip();
            }

            _clip.Writer.Write(cleanedBytes, 0, cleanedBytes.Length);
        }

        private void Calibrate(byte[] buffer, int bytesRecorded)
        {
            var samples = BufferToSamples(buffer, bytesRecorded);
            _calibration.AddVolumeSample(GetVolumeFromSamples(samples));

            for (var i = 0; i < samples.Length; i++)
            {
                _calibration.FftBuffer[_calibration.FftBufferIndex++] = samples[i];
                if (_calibration.FftBufferIndex >= FftSize)
                {
                    _calibration.FftBufferIndex = 0;
                    _calibration.FftFrames.Add(ComputeFftMagnitudes(_calibration.FftBuffer));
                }
            }

            if ((DateTime.Now - _calibration.StartTime).TotalMilliseconds >= _calibration.DurationMs)
            {
                _calibration.Complete();
                _startRecordingButton.Invoke((MethodInvoker)(() => _startRecordingButton.Text = "Recording..."));
                Console.WriteLine($"Calibration done. Baseline volume: {_calibration.VolumeBaseline:F4}");
            }
        }

        private float[] ComputeFftMagnitudes(float[] samples)
        {
            var window = Window.OfType(WindowType.Hann, FftSize);
            var real = samples.ToArray();
            for (var i = 0; i < FftSize; i++)
                real[i] *= window[i];

            var imag = new float[FftSize];
            var fft = new RealFft(FftSize);
            fft.Direct(real, real, imag);

            var magnitudes = new float[FftSize / 2];
            for (var i = 0; i < FftSize / 2; i++)
                magnitudes[i] = (float)Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

            return magnitudes;
        }

        private float[] BufferToSamples(byte[] buffer, int bytesRecorded)
        {
            int sampleCount = bytesRecorded / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            return samples;
        }

        private float GetVolumeFromSamples(float[] samples)
        {
            var max = 0f;
            foreach (var s in samples)
            {
                var abs = Math.Abs(s);
                if (abs > max) max = abs;
            }
            return max;
        }

        private byte[] SamplesToBytes(float[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, samples[i] * 32768f));
                BitConverter.GetBytes(s).CopyTo(bytes, i * 2);
            }
            return bytes;
        }
    }
}