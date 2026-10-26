using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Plural.Platform.Apple;

namespace Plural.Platform.MacOS;

/// <summary>
/// Runs work on the main thread's CFRunLoop. IOBluetooth is backed by CoreBluetooth on the main dispatch queue and delivers its
/// delegate callbacks (RFCOMM open/data, SDP) only while the main thread services it, so every IOBluetooth call and callback goes
/// through here: work is posted through a run-loop source in the common modes, each item inside an autorelease pool. The app's
/// main thread already runs the loop; a headless host runs it with <see cref="Run{T}"/>.
/// </summary>
public sealed unsafe partial class MainRunLoop
{
    private static readonly Lazy<MainRunLoop> Shared = new(() => new MainRunLoop());

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly IntPtr _runLoop;
    private readonly IntPtr _source;

    private MainRunLoop()
    {
        _runLoop = CF.CFRetain(CF.CFRunLoopGetMain());
        var context = new CFRunLoopSourceContext
        {
            Info = GCHandle.ToIntPtr(GCHandle.Alloc(this)),
            Perform = &Perform,
        };
        _source = CF.CFRunLoopSourceCreate(IntPtr.Zero, 0, &context);
        CF.CFRunLoopAddSource(_runLoop, _source, CF.RunLoopCommonModes);
    }

    internal static MainRunLoop Instance => Shared.Value;

    internal static bool IsCurrent => pthread_main_np() != 0;

    /// <summary>
    /// For a process without a UI loop (the CLI): call on the main thread; runs <paramref name="body"/> on the thread pool while
    /// the main thread runs its run loop (which also drains the main dispatch queue), and returns its result.
    /// </summary>
    public static T Run<T>(Func<Task<T>> body)
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException("MainRunLoop.Run must be called on the main thread.");
        }
        var task = Task.Run(body);
        while (!task.IsCompleted)
        {
            CF.CFRunLoopRunInMode(CF.RunLoopDefaultMode, 0.25, 0);
        }
        return task.GetAwaiter().GetResult();
    }

    [UnmanagedCallersOnly]
    private static void Perform(IntPtr info)
    {
        var self = (MainRunLoop)GCHandle.FromIntPtr(info).Target!;
        while (self._work.TryDequeue(out var action))
        {
            var pool = ObjC.objc_autoreleasePoolPush();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainRunLoop: {ex}");
            }
            finally
            {
                ObjC.objc_autoreleasePoolPop(pool);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on the main run loop (exceptions are logged, not thrown).</summary>
    internal void Post(Action action)
    {
        _work.Enqueue(action);
        CF.CFRunLoopSourceSignal(_source);
        CF.CFRunLoopWakeUp(_runLoop);
    }

    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    internal T Invoke<T>(Func<T> func)
    {
        if (IsCurrent)
        {
            var pool = ObjC.objc_autoreleasePoolPush();
            try
            {
                return func();
            }
            finally
            {
                ObjC.objc_autoreleasePoolPop(pool);
            }
        }
        return InvokeAsync(func).GetAwaiter().GetResult();
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial int pthread_main_np();
}