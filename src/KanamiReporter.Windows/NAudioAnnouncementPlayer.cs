using KanamiReporter.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KanamiReporter.Windows;

public sealed class NAudioAnnouncementPlayer : IAnnouncementPlayer
{
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly object _gate = new();
    private PlaybackSession? _current;
    private bool _disposed;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultDeviceId(enumerator);
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.Ordinal)))
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task PlayAsync(string filePath, string? deviceId, float volume, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到语音文件。", filePath);
        }

        await _playGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var reader = new AudioFileReader(filePath) { Volume = Math.Clamp(volume, 0f, 1f) };
            IWavePlayer output;
            try
            {
                output = CreateOutput(deviceId);
            }
            catch
            {
                reader.Dispose();
                throw;
            }

            var session = new PlaybackSession(output, reader);
            PlaybackSession? previous;
            lock (_gate)
            {
                previous = _current;
                _current = session;
            }

            previous?.Stop();
            try
            {
                session.Start();
            }
            catch
            {
                session.Stop();
                lock (_gate)
                {
                    if (ReferenceEquals(_current, session))
                    {
                        _current = null;
                    }
                }

                throw;
            }

            using var registration = cancellationToken.Register(session.Stop);
            await session.Completion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _playGate.Release();
        }
    }

    public Task StopAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        PlaybackSession? current;
        lock (_gate)
        {
            current = _current;
        }

        current?.Stop();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        await _playGate.WaitAsync();
        _playGate.Release();
        _playGate.Dispose();
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            return null;
        }
    }

    private static IWavePlayer CreateOutput(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 100);
        }
        catch
        {
            // WASAPI 不可用或设备被移除时回退到系统默认输出。
        }

        return new WaveOutEvent { DesiredLatency = 100 };
    }

    private sealed class PlaybackSession
    {
        private readonly IWavePlayer _output;
        private readonly AudioFileReader _reader;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public PlaybackSession(IWavePlayer output, AudioFileReader reader)
        {
            _output = output;
            _reader = reader;
        }

        public Task Completion => _completion.Task;

        public void Start()
        {
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_reader);
            _output.Play();
        }

        public void Stop() => Complete(null, stopOutput: true);

        private void OnPlaybackStopped(object? sender, StoppedEventArgs args) =>
            Complete(args.Exception, stopOutput: false);

        private void Complete(Exception? error, bool stopOutput)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            try
            {
                _output.PlaybackStopped -= OnPlaybackStopped;
                if (stopOutput)
                {
                    _output.Stop();
                }
            }
            catch
            {
                // 设备断开时停止可能失败，继续释放资源。
            }

            try
            {
                _output.Dispose();
            }
            catch
            {
                // 与播放错误无关，忽略释放异常。
            }

            _reader.Dispose();
            if (error is null)
            {
                _completion.TrySetResult();
            }
            else
            {
                _completion.TrySetException(error);
            }
        }
    }
}


