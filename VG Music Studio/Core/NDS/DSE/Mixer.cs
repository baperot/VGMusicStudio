using System;
using System.IO;

namespace Kermalis.VGMusicStudio.Core.NDS.DSE
{
    internal class Mixer : Core.Mixer
    {
        private const int _numChannels = 0x20;
        private readonly float _samplesReciprocal;
        private readonly int _samplesPerBuffer;
        private bool _isFading;
        private long _fadeMicroFramesLeft;
        private float _fadePos;
        private float _fadeStepPerMicroframe;

        private readonly Channel[] _channels;
        private const int sampleRate = 65456;
        private const int channels = 2;
        private BinaryWriter _waveWriter;
        private int _audioDataLength = 0;

        public Mixer()
        {
            // The sampling frequency of the mixer is 1.04876 MHz with an amplitude resolution of 24 bits, but the sampling frequency after mixing with PWM modulation is 32.768 kHz with an amplitude resolution of 10 bits.
            // - gbatek
            // I'm not using either of those because the samples per buffer leads to an overflow eventually
            _samplesPerBuffer = 341; // TODO
            _samplesReciprocal = 1f / _samplesPerBuffer;

            _channels = new Channel[_numChannels];
            for (byte i = 0; i < _numChannels; i++)
            {
                _channels[i] = new Channel(i);
            }

            Init(sampleRate, channels, _samplesPerBuffer);
        }

        public override void Dispose()
        {
            base.Dispose();
            CloseWaveWriter();
        }

        public Channel AllocateChannel()
        {
            int GetScore(Channel c)
            {
                // Free channels should be used before releasing channels
                return c.Owner == null ? -2 : Utils.IsStateRemovable(c.State) ? -1 : 0;
            }
            Channel nChan = null;
            for (int i = 0; i < _numChannels; i++)
            {
                Channel c = _channels[i];
                if (nChan != null)
                {
                    int nScore = GetScore(nChan);
                    int cScore = GetScore(c);
                    if (cScore <= nScore && (cScore < nScore || c.Volume <= nChan.Volume))
                    {
                        nChan = c;
                    }
                }
                else
                {
                    nChan = c;
                }
            }
            return nChan != null && 0 >= GetScore(nChan) ? nChan : null;
        }

        public void ChannelTick()
        {
            for (int i = 0; i < _numChannels; i++)
            {
                Channel chan = _channels[i];
                if (chan.Owner != null)
                {
                    chan.Volume = (byte)chan.StepEnvelope();
                    if (chan.NoteLength == 0 && !Utils.IsStateRemovable(chan.State))
                    {
                        chan.SetEnvelopePhase7_2074ED8();
                    }
                    int vol = SDAT.Utils.SustainTable[chan.NoteVelocity] + SDAT.Utils.SustainTable[chan.Volume] + SDAT.Utils.SustainTable[chan.Owner.Volume] + SDAT.Utils.SustainTable[chan.Owner.Expression];
                    //int pitch = ((chan.Key - chan.BaseKey) << 6) + chan.SweepMain() + chan.Owner.GetPitch(); // "<< 6" is "* 0x40"
                    int pitch = (chan.Key - chan.RootKey) << 6; // "<< 6" is "* 0x40"
                    if (Utils.IsStateRemovable(chan.State) && vol <= -92544)
                    {
                        chan.Stop();
                    }
                    else
                    {
                        chan.Volume = SDAT.Utils.GetChannelVolume(vol);
                        chan.Panpot = chan.Owner.Panpot;
                        chan.Timer = SDAT.Utils.GetChannelTimer(chan.BaseTimer, pitch);
                    }
                }
            }
        }

        public void BeginFadeIn()
        {
            _fadePos = 0f;
            _fadeMicroFramesLeft = (long)(GlobalConfig.Instance.PlaylistFadeOutMilliseconds / 1000.0 * 192);
            _fadeStepPerMicroframe = 1f / _fadeMicroFramesLeft;
            _isFading = true;
        }

        public void BeginFadeOut()
        {
            _fadePos = 1f;
            _fadeMicroFramesLeft = (long)(GlobalConfig.Instance.PlaylistFadeOutMilliseconds / 1000.0 * 192);
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

            // Write WAV header
            _waveWriter.Write(new char[] { 'R', 'I', 'F', 'F' });
            _waveWriter.Write(0); // File size - 8 (placeholder)
            _waveWriter.Write(new char[] { 'W', 'A', 'V', 'E' });

            // Write fmt chunk
            _waveWriter.Write(new char[] { 'f', 'm', 't', ' ' });
            _waveWriter.Write(16); // Chunk size
            _waveWriter.Write((ushort)1); // Audio format (PCM)
            _waveWriter.Write((ushort)channels); // Channels
            _waveWriter.Write(sampleRate); // Sample rate
            _waveWriter.Write(sampleRate * channels * 2); // Byte rate
            _waveWriter.Write((ushort)(channels * 2)); // Block align
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

        public void Process(bool output, bool recording)
        {
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
            byte[] b = new byte[4];
            for (int i = 0; i < _samplesPerBuffer; i++)
            {
                int left = 0, right = 0;
                for (int j = 0; j < _numChannels; j++)
                {
                    Channel chan = _channels[j];
                    if (chan.Owner != null)
                    {
                        bool muted = Mutes[chan.Owner.Index];
                        chan.Process(out short channelLeft, out short channelRight);
                        if (!muted)
                        {
                            left += channelLeft;
                            right += channelRight;
                        }
                    }
                }
                float f = left * masterLevel;
                if (f < short.MinValue) f = short.MinValue;
                else if (f > short.MaxValue) f = short.MaxValue;
                left = (int)f;
                b[0] = (byte)left;
                b[1] = (byte)(left >> 8);
                
                f = right * masterLevel;
                if (f < short.MinValue) f = short.MinValue;
                else if (f > short.MaxValue) f = short.MaxValue;
                right = (int)f;
                b[2] = (byte)right;
                b[3] = (byte)(right >> 8);
                masterLevel += masterStep;
                
                if (output)
                {
                    QueueAudio(b, 4);
                }
                if (recording)
                {
                    _waveWriter.Write(b, 0, 4);
                    _audioDataLength += 4;
                }
            }
        }
    }
}
