using Kermalis.VGMusicStudio.UI;
using Kermalis.VGMusicStudio.Core.Audio;
using System;

namespace Kermalis.VGMusicStudio.Core
{
    internal abstract class Mixer : IDisposable
    {
        public readonly bool[] Mutes = new bool[SongInfoControl.SongInfo.MaxTracks];
        protected SDL2AudioWrapper AudioDevice;
        private bool _volChange = true;

        protected void Init(int sampleRate, int channels, int bufferSize)
        {
            AudioDevice = new SDL2AudioWrapper();
            AudioDevice.Init(sampleRate, channels, bufferSize);
            AudioDevice.Play();
        }

        public void SetVolume(float volume)
        {
            _volChange = false;
            if (AudioDevice != null)
            {
                AudioDevice.Volume = volume;
            }
        }

        public void QueueAudio(byte[] audioData, int length)
        {
            if (AudioDevice != null)
            {
                AudioDevice.QueueAudio(audioData, length);
            }
        }

        public void OnVolumeChanged(float volume)
        {
            if (_volChange)
            {
                MainForm.Instance.SetVolumeBarValue(volume);
            }
            _volChange = true;
        }

        public virtual void Dispose()
        {
            AudioDevice?.Stop();
            AudioDevice?.Dispose();
        }
    }
}
