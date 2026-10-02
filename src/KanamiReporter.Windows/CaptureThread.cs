using System.Collections.Concurrent;

namespace KanamiReporter.Windows;

/// <summary>
/// Windows 图形捕获的捕获对象（GraphicsCaptureItem、帧池、采集会话）带有线程归属：
/// 在创建它的线程之外释放会抛 RPC_E_WRONG_THREAD（0x8001010E，
/// “应用程序调用一个已为另一线程整理的接口”）。所以创建和释放都必须落在同一个线程上。
///
/// 这里用一条专用 MTA 线程承载这对操作。不用界面线程的原因有两个：
/// 回退到显示器时启动采集发生在帧回调一侧的线程池线程上，而退出流程里界面调度器
/// 可能已经不再接受投递——两种情况都会让“同一个线程”这个约束失效。
/// </summary>
internal sealed class CaptureThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public CaptureThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "KanamiCapture"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>在采集线程上执行 action 并等它结束；action 抛出的异常原样抛给调用方。</summary>
    public void Invoke(Action action)
    {
        // 已经在采集线程上时必须直接执行：投递给自己会死等在自己身上。
        if (Thread.CurrentThread == _thread)
        {
            action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Action(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        try
        {
            _queue.Add(work);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(CaptureThread));
        }

        completion.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // 采集线程可能正卡在某个系统调用里（实测有机器会卡在采集对象的创建/释放上），
        // 不能无限等它：它是后台线程，进程退出不依赖它，等不到就让它随进程一起结束。
        // 在采集线程自己身上调用 Dispose 时更不能 Join 自己。
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            action();
        }
    }
}
