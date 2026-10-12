using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;

namespace Plural.Platform.Apple;

/// <summary>
/// Captures 44.1 kHz mono 16-bit PCM through an input AudioQueue (Core Audio converts from the device format), so every local
/// mic arrives in the same format as a SmartMike. Callbacks run on the queue's own thread.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed unsafe class AudioQueueInput : IAudioInput
{
    private const int BufferCount = 3;
    private const int FramesPerBuffer = 1024;

    private readonly Action<ReadOnlySpan<short>> _onPcm;
    private readonly Action<string> _onError;
    private GCHandle _self;
    private IntPtr _queue;
    private volatile bool _stopping;
    private int _failed;

    public AudioInputDevice Device { get; }
    public int SampleRate => IAudioInputPlatform.PreferredSampleRate;

    /// <param name="deviceUid">Core Audio device UID (macOS); null records from the audio session's current input (iOS).</param>
    public AudioQueueInput(AudioInputDevice device, string? deviceUid, Action<ReadOnlySpan<short>> onPcm, Action<string> onError)
    {
        Device = device;
        _onPcm = onPcm;
        _onError = onError;
        _self = GCHandle.Alloc(this);
        var format = AudioStreamBasicDescription.Pcm16(SampleRate, 1);
        IntPtr queue;
        AT.Check(AT.AudioQueueNewInput(&format, &OnInput, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, &queue),
            "Opening the microphone");
        _queue = queue;
        try
        {
            if (deviceUid is not null)
            {
                AT.SetCurrentDevice(_queue, deviceUid);
            }
            AT.Check(AT.AudioQueueAddPropertyListener(_queue, AT.PropertyIsRunning, &OnRunningChanged, GCHandle.ToIntPtr(_self)),
                "Watching the microphone");
            for (var i = 0; i < BufferCount; i++)
            {
                AudioQueueBuffer* buffer;
                AT.Check(AT.AudioQueueAllocateBuffer(_queue, FramesPerBuffer * sizeof(short), &buffer), "Allocating audio buffers");
                AT.Check(AT.AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero), "Queueing audio buffers");
            }
            AT.Check(AT.AudioQueueStart(_queue, IntPtr.Zero), "Starting the microphone");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnInput(IntPtr user, IntPtr queue, AudioQueueBuffer* buffer, IntPtr startTime, uint descriptions, IntPtr packetDescriptions)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not AudioQueueInput { _stopping: false } input)
            {
                return;
            }
            input._onPcm(new ReadOnlySpan<short>(buffer->AudioData, (int)(buffer->AudioDataByteSize / sizeof(short))));
            AT.AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }
        catch (Exception)
        {
            // never unwind into Core Audio
        }
    }

    /// <summary>The queue stops on its own when the device disappears or another app takes the input over.</summary>
    [UnmanagedCallersOnly]
    private static void OnRunningChanged(IntPtr user, IntPtr queue, uint property)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not AudioQueueInput { _stopping: false } input)
            {
                return;
            }
            uint running = 0, size = sizeof(uint);
            if (AT.AudioQueueGetProperty(queue, AT.PropertyIsRunning, &running, &size) == 0 && running == 0 &&
                Interlocked.Exchange(ref input._failed, 1) == 0)
            {
                input._onError("The microphone stopped.");
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
