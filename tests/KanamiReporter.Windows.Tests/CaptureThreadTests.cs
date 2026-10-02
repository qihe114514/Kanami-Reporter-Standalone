namespace KanamiReporter.Windows.Tests;

/// <summary>
/// Windows 图形捕获的对象必须"谁创建谁释放"：在别的线程上释放会抛
/// RPC_E_WRONG_THREAD（0x8001010E，应用程序调用一个已为另一线程整理的接口）。
/// 这些用例锁住 CaptureThread 提供的那条保证，避免有人把它换回 Task.Run。
/// </summary>
public sealed class CaptureThreadTests
{
    [Fact]
    public void InvokeRunsEveryActionOnTheSameDedicatedThread()
    {
        using var captureThread = new CaptureThread();
        var callerThread = Environment.CurrentManagedThreadId;
        var firstThread = 0;
        var secondThread = 0;

        captureThread.Invoke(() => firstThread = Environment.CurrentManagedThreadId);
        captureThread.Invoke(() => secondThread = Environment.CurrentManagedThreadId);

        Assert.NotEqual(callerThread, firstThread);
        Assert.Equal(firstThread, secondThread);
    }

    [Fact]
    public void InvokePropagatesExceptionsToTheCaller()
    {
        using var captureThread = new CaptureThread();

        var exception = Assert.Throws<InvalidOperationException>(
            () => captureThread.Invoke(() => throw new InvalidOperationException("采集失败")));

        Assert.Equal("采集失败", exception.Message);
    }

    [Fact]
    public void NestedInvokeRunsInlineInsteadOfDeadlocking()
    {
        using var captureThread = new CaptureThread();
        var nestedThread = 0;

        captureThread.Invoke(() => captureThread.Invoke(() => nestedThread = Environment.CurrentManagedThreadId));

        var outerThread = 0;
        captureThread.Invoke(() => outerThread = Environment.CurrentManagedThreadId);
        Assert.Equal(outerThread, nestedThread);
    }

    [Fact]
    public void DisposeWaitsForInvokedWorkToFinish()
    {
        var captureThread = new CaptureThread();
        var completed = 0;

        captureThread.Invoke(() => Interlocked.Increment(ref completed));
        captureThread.Dispose();

        Assert.Equal(1, completed);
    }

    [Fact]
    public void InvokeAfterDisposeThrows()
    {
        var captureThread = new CaptureThread();
        captureThread.Dispose();

        Assert.Throws<ObjectDisposedException>(() => captureThread.Invoke(() => { }));
    }
}
