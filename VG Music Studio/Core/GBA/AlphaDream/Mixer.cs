using System;
using System.IO;

namespace Kermalis.VGMusicStudio.Core.GBA.AlphaDream
{
    internal class Mixer : Core.Mixer
    {
        public readonly float SampleRateReciprocal;
        private readonly float _samplesReciprocal;
        public readonly int SamplesPerBuffer;
        private bool _isFading;
        private long _fadeMicroFramesLeft;
        private float _fadePos;
        private float _fadeStepPerMicroframe;

        public readonly Config Config;
        private readonly float[] _audio;
        private readonly float[][] _trackBuffers = new float[Player.NumTracks][];
        private readonly int _sampleRate;
        private readonly int _channels = 2;
        private BinaryWriter _waveWriter;
        private int _audioDataLength = 0; // Track for WAV file header

        public Mixer(Config config)
        {
            Config = config;
            const int sampleRate = 13379; // TODO: Actual value unknown
            _sampleRate = sampleRate;
            SamplesPerBuffer = 224; // TODO
            SampleRateReciprocal = 1f / sampleRate;
            _samplesReciprocal = 1f / SamplesPerBuffer;

            int amt = SamplesPerBuffer * _channels;
            _audio = new float[amt];
            for (int i = 0; i < Player.NumTracks; i++)
            {
                _trackBuffers[i] = new float[amt];
            }
            
            Init(sampleRate, _channels, SamplesPerBuffer);
        }

        public override void Dispose()
        {
            base.Dispose();
            CloseWaveWriter();
        }

        public void BeginFadeIn()
        {
            _fadePos = 0f;
            _fadeMicroFramesLeft = (long)(GlobalConfig.Instance.PlaylistFadeOutMilliseconds / 1000.0 * Utils.AGB_FPS);
            _fadeStepPerMicroframe = 1f / _fadeMicroFramesLeft;
            _isFading = true;
        }

        public void BeginFadeOut()
        {
            _fadePos = 1f;
            _fadeMicroFramesLeft = (long)(GlobalConfig.Instance.PlaylistFadeOutMilliseconds / 1000.0 * Utils.AGB_FPS);
            _fadeStepPerMicroframe = -1f / _fadeMicroFramesLeft;
            _isFading = true;
        }

        public bool IsFading()
        {
            return _isFading;
        }

        public bool IsFadeDone()
        {
            return _isFading && _fadeMicroFramesLeft == 0;
        }

        public void ResetFade()
        {
            _isFading = false;
            _fadeMicroFramesLeft = 0;
        }

        public void CreateWaveWriter(string fileName)
        {
            FileStream fileStream = File.Create(fileName);
            _waveWriter = new BinaryWriter(fileStream);
            _audioDataLength = 0;

            // Write WAV header (will update size later)
            _waveWriter.Write(new char[] { 'R', 'I', 'F', 'F' });
            _waveWriter.Write(0); // File size - 8 (placeholder)
            _waveWriter.Write(new char[] { 'W', 'A', 'V', 'E' });

            // Write fmt chunk
            _waveWriter.Write(new char[] { 'f', 'm', 't', ' ' });
            _waveWriter.Write(16); // Chunk size
            _waveWriter.Write((ushort)1); // Audio format (PCM)
            _waveWriter.Write((ushort)_channels); // Channels
            _waveWriter.Write(_sampleRate); // Sample rate
            _waveWriter.Write(_sampleRate * _channels * 2); // Byte rate
            _waveWriter.Write((ushort)(_channels * 2)); // Block align
            _waveWriter.Write((ushort)16); // Bits per sample

            // Write data chunk
            _waveWriter.Write(new char[] { 'd', 'a', 't', 'a' });
            _waveWriter.Write(0); // Data size (placeholder)
        }

        public void CloseWaveWriter()
        {
            if (_waveWriter != null)
            {
                long pos = _waveWriter.BaseStream.Position;
                
                // Update data chunk size
                _waveWriter.BaseStream.Seek(40, SeekOrigin.Begin);
                _waveWriter.Write(_audioDataLength);

                // Update file size
                _waveWriter.BaseStream.Seek(4, SeekOrigin.Begin);
                _waveWriter.Write((int)(pos - 8));

                _waveWriter.Close();
                _waveWriter = null;
            }
        }

        public void Process(Track[] tracks, bool output, bool recording)
        {
            Array.Clear(_audio, 0, _audio.Length);
            float masterStep;
            float masterLevel;

            if (_isFading && _fadeMicroFramesLeft == 0)
            {
                masterStep = 0;
                masterLevel = 0;
            }
            else
            {
                float fromMaster = 1f;
                float toMaster = 1f;
                if (_fadeMicroFramesLeft > 0)
                {
                    const float scale = 10f / 6f;
                    fromMaster *= (_fadePos < 0f) ? 0f : (float)Math.Pow(_fadePos, scale);
                    _fadePos += _fadeStepPerMicroframe;
                    toMaster *= (_fadePos < 0f) ? 0f : (float)Math.Pow(_fadePos, scale);
                    _fadeMicroFramesLeft--;
                }
                masterStep = (toMaster - fromMaster) * _samplesReciprocal;
                masterLevel = fromMaster;
            }

            for (int i = 0; i < Player.NumTracks; i++)
            {
                Track track = tracks[i];
                if (track.Enabled && track.NoteDuration != 0 && !track.Channel.Stopped && !Mutes[i])
                {
                    float level = masterLevel;
                    float[] buf = _trackBuffers[i];
                    Array.Clear(buf, 0, buf.Length);
                    track.Channel.Process(buf);
                    for (int j = 0; j < SamplesPerBuffer; j++)
                    {
                        _audio[j * 2] += buf[j * 2] * level;
                        _audio[(j * 2) + 1] += buf[(j * 2) + 1] * level;
                        level += masterStep;
                    }
                }
            }

            if (output)
            {
                // Convert float samples to byte array (PCM 16-bit)
                byte[] audioBytes = ConvertFloatToBytes(_audio);
                QueueAudio(audioBytes, audioBytes.Length);
            }

            if (recording && _waveWriter != null)
            {
                byte[] audioBytes = ConvertFloatToBytes(_audio);
                _waveWriter.Write(audioBytes);
                _audioDataLength += audioBytes.Length;
            }
        }

        private byte[] ConvertFloatToBytes(float[] floatData)
        {
            byte[] byteData = new byte[floatData.Length * 2];
            for (int i = 0; i < floatData.Length; i++)
            {
                // Clamp and convert to 16-bit PCM
                float sample = Math.Max(-1.0f, Math.Min(1.0f, floatData[i]));
                short sampleInt = (short)(sample * 32767);
                
                // Little-endian
                byteData[i * 2] = (byte)(sampleInt & 0xFF);
                byteData[i * 2 + 1] = (byte)((sampleInt >> 8) & 0xFF);
            }
            return byteData;
        }
    }
}
