using System;
using System.Runtime.InteropServices;

namespace Kermalis.VGMusicStudio.Core.Audio
{
    /// <summary>
    /// SDL2 audio wrapper for cross-platform audio playback.
    /// Replaces NAudio dependency with SDL2-CS bindings.
    /// </summary>
    internal class SDL2AudioWrapper : IDisposable
    {
        // SDL2 Audio Format constants
        private const ushort AUDIO_F32 = 0x8120;
        private const ushort AUDIO_S16 = 0x8010;

        // SDL2 P/Invoke declarations
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_InitSubSystem(uint flags);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_OpenAudioDevice(
            string device,
            int iscapture,
            ref SDL_AudioSpec desired,
            out SDL_AudioSpec obtained,
            int allowed_changes);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_CloseAudioDevice(uint dev);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_PauseAudioDevice(uint dev, int pause_on);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SDL_GetQueuedAudioSize(uint dev);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_QueueAudio(uint dev, IntPtr data, uint len);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_ClearQueuedAudio(uint dev);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        private static extern float SDL_GetAudioDeviceSpec(uint dev, int iscapture, ref SDL_AudioSpec spec);

        [StructLayout(LayoutKind.Sequential)]
        private struct SDL_AudioSpec
        {
            public int freq;
            public ushort format;
            public byte channels;
            public byte silence;
            public ushort samples;
            public ushort padding;
            public uint size;
            public IntPtr callback;
            public IntPtr userdata;
        }

        private const uint SDL_INIT_AUDIO = 0x00000010;
        private uint _audioDeviceId;
        private bool _isInitialized = false;
        private int _sampleRate;
        private int _channels;
        private int _bufferSize;
        private float _volume = 1.0f;

        public int SampleRate => _sampleRate;
        public int Channels => _channels;
        public int BufferSize => _bufferSize;
        public float Volume
        {
            get => _volume;
            set => _volume = Math.Max(0.0f, Math.Min(1.0f, value));
        }

        /// <summary>
        /// Initialize SDL2 audio device with specified parameters.
        /// </summary>
        public void Init(int sampleRate, int channels, int bufferSize)
        {
            if (_isInitialized)
                throw new InvalidOperationException("Audio device already initialized");

            // Initialize SDL audio subsystem
            if (SDL_InitSubSystem(SDL_INIT_AUDIO) < 0)
                throw new Exception("Failed to initialize SDL audio subsystem");

            _sampleRate = sampleRate;
            _channels = channels;
            _bufferSize = bufferSize;

            // Configure desired audio format
            SDL_AudioSpec desired = new SDL_AudioSpec
            {
                freq = sampleRate,
                format = AUDIO_F32, // Float32 for better precision
                channels = (byte)channels,
                samples = (ushort)bufferSize,
                callback = IntPtr.Zero // No callback - we use queueing
            };

            // Open audio device
            _audioDeviceId = (uint)SDL_OpenAudioDevice(null, 0, ref desired, out SDL_AudioSpec obtained, 0);
            if (_audioDeviceId == 0)
                throw new Exception("Failed to open SDL audio device");

            _sampleRate = obtained.freq;
            _channels = obtained.channels;
            _bufferSize = obtained.samples;
            _isInitialized = true;
        }

        /// <summary>
        /// Queue audio samples for playback.
        /// </summary>
        public void QueueAudio(byte[] audioData, int length)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Audio device not initialized");

            // Apply volume scaling if needed
            if (_volume < 1.0f)
            {
                ScaleAudioVolume(audioData, length);
            }

            GCHandle handle = GCHandle.Alloc(audioData, GCHandleType.Pinned);
            try
            {
                int result = SDL_QueueAudio(_audioDeviceId, handle.AddrOfPinnedObject(), (uint)length);
                if (result < 0)
                    throw new Exception("Failed to queue audio data");
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// Start playback.
        /// </summary>
        public void Play()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Audio device not initialized");

            SDL_PauseAudioDevice(_audioDeviceId, 0);
        }

        /// <summary>
        /// Pause playback.
        /// </summary>
        public void Pause()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Audio device not initialized");

            SDL_PauseAudioDevice(_audioDeviceId, 1);
        }

        /// <summary>
        /// Stop playback and clear queue.
        /// </summary>
        public void Stop()
        {
            if (!_isInitialized)
                return;

            SDL_PauseAudioDevice(_audioDeviceId, 1);
            SDL_ClearQueuedAudio(_audioDeviceId);
        }

        /// <summary>
        /// Get the amount of queued audio data (in bytes).
        /// </summary>
        public uint GetQueuedAudioSize()
        {
            if (!_isInitialized)
                return 0;

            return SDL_GetQueuedAudioSize(_audioDeviceId);
        }

        /// <summary>
        /// Apply volume scaling to audio buffer (Float32 format).
        /// </summary>
        private void ScaleAudioVolume(byte[] audioData, int length)
        {
            // Convert byte array to float array, scale, and convert back
            int floatCount = length / sizeof(float);
            
            for (int i = 0; i < floatCount; i++)
            {
                // Read float from byte array
                float sample = BitConverter.ToSingle(audioData, i * sizeof(float));
                
                // Scale by volume
                sample *= _volume;
                
                // Clamp to valid range
                if (sample < -1.0f) sample = -1.0f;
                else if (sample > 1.0f) sample = 1.0f;
                
                // Write back to byte array
                byte[] floatBytes = BitConverter.GetBytes(sample);
                Array.Copy(floatBytes, 0, audioData, i * sizeof(float), sizeof(float));
            }
        }

        /// <summary>
        /// Release resources.
        /// </summary>
        public void Dispose()
        {
            if (_isInitialized)
            {
                Stop();
                SDL_CloseAudioDevice(_audioDeviceId);
                SDL_QuitSubSystem(SDL_INIT_AUDIO);
                _isInitialized = false;
            }
        }
    }
}
