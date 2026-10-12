using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Audio;
using Plural.Core.Platform;

namespace Plural.Platform.Apple;

/// <summary>
/// Plays a <see cref="JitterBuffer"/> through an output AudioQueue: float PCM, three ~12 ms buffers, callbacks on the queue's
/// own thread (silence on underrun, like the other platforms). Core Audio converts to the device's rate.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed unsafe class AudioQueueSink : IAudioSink
{
    private const int BufferCount = 3;
    private const int FramesPerBuffer = 512;

    private readonly IAudioSource _source;
    private GCHandle _self;
    private IntPtr _queue;
    private volatile bool _stopping;

    public AudioOutputDevice Device { get; }

    /// <param name="deviceUid">Core Audio device UID (macOS); null plays to the default output / current route.</param>
    public AudioQueueSink(AudioOutputDevice device, IAudioSource source, int sampleRate, string? deviceUid)
    {
        Device = device;
        _source = source;
        _self = GCHandle.Alloc(this);
        var format = AudioStreamBasicDescription.Float32(sampleRate, source.Channels);
        IntPtr queue;
        AT.Check(AT.AudioQueueNewOutput(&format, &OnOutput, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, &queue),
            "Opening the audio output");
        _queue = queue;
        try
        {
            if (deviceUid is not null)
            {
                AT.SetCurrentDevice(_queue, deviceUid);
            }
            var bytes = (uint)(FramesPerBuffer * source.Channels * sizeof(float));
            for (var i = 0; i < BufferCount; i++)
            {
                AudioQueueBuffer* buffer;
                AT.Check(AT.AudioQueueAllocateBuffer(_queue, bytes, &buffer), "Allocating audio buffers");
                Fill(buffer);
            }
            AT.Check(AT.AudioQueueStart(_queue, IntPtr.Zero), "Starting the audio output");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Fill(AudioQueueBuffer* buffer)
    {
        var samples = new Span<float>(buffer->AudioData, (int)(buffer->AudioDataBytesCapacity / sizeof(float)));
        _source.Read(samples);
        buffer->AudioDataByteSize = buffer->AudioDataBytesCapacity;
        AT.AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero);
    }

    [UnmanagedCallersOnly]
    private static void OnOutput(IntPtr user, IntPtr queue, AudioQueueBuffer* buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is AudioQueueSink { _stopping: false } sink)
            {
                sink.Fill(buffer);
            }
        }
        catch (Exception)
        {
            // never unwind into Core Audio
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (_queue != IntPtr.Zero)
        {
            AT.AudioQueueStop(_queue, 1);
            AT.AudioQueueDispose(_queue, 1);
            _queue = IntPtr.Zero;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
