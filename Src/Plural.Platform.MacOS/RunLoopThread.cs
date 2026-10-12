using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Plural.Platform.Apple;

namespace Plural.Platform.MacOS;

/// <summary>
/// A background thread running a CFRunLoop. IOBluetooth delivers delegate callbacks on the run loop of the thread that started
/// the operation, so every IOBluetooth call is made here (work is posted through a run-loop source, each item inside an
/// autorelease pool). Works the same in the app and in the headless CLI, without depending on the main thread.
/// </summary>
internal sealed unsafe class RunLoopThread
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private IntPtr _runLoop;
    private IntPtr _source;

    public RunLoopThread(string name)
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = name };
        _thread.Start();
        ready.Wait();
    }

    public bool IsCurrent => Thread.CurrentThread == _thread;

    private void Run(ManualResetEventSlim ready)
    {
        _runLoop = CF.CFRetain(CF.CFRunLoopGetCurrent());
        var context = new CFRunLoopSourceContext
        {
            Info = GCHandle.ToIntPtr(GCHandle.Alloc(this)),
            Perform = &Perform,
        };
        _source = CF.CFRunLoopSourceCreate(IntPtr.Zero, 0, &context);
        CF.CFRunLoopAddSource(_runLoop, _source, CF.RunLoopDefaultMode);
        ready.Set();
        CF.CFRunLoopRun();
    }

    [UnmanagedCallersOnly]
    private static void Perform(IntPtr info)
    {
        var self = (RunLoopThread)GCHandle.FromIntPtr(info).Target!;
        while (self._work.TryDequeue(out var action))
        {
            var pool = ObjC.objc_autoreleasePoolPush();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{self._thread.Name}: {ex}");
            }
            finally
            {
                ObjC.objc_autoreleasePoolPop(pool);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on the run-loop thread (exceptions are logged, not thrown).</summary>
    public void Post(Action action)
    {
        _work.Enqueue(action);
        CF.CFRunLoopSourceSignal(_source);
        CF.CFRunLoopWakeUp(_runLoop);
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
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

    public T Invoke<T>(Func<T> func)
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
}
