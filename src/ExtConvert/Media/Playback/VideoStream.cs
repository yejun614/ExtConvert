using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ExtConvert.Media.Playback;

internal readonly record struct VideoFrame(TimeSpan Pts, byte[] Data);

/// <summary>
/// ffmpeg가 출력하는 고정 프레임레이트 BGRA 원시 프레임을 백그라운드에서 읽어 작은 큐에 쌓는다.
/// 큐가 가득 차면 읽기를 멈추므로 ffmpeg도 파이프에서 대기한다 (자연스러운 흐름 제어).
/// </summary>
internal sealed class VideoStream : IDisposable
{
    private const int QueueCapacity = 6;

    private readonly Stream _raw;
    private readonly int _frameBytes;
    private readonly double _fps;
    private readonly TimeSpan _start;
    private readonly Channel<VideoFrame> _frames =
        Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;

    public VideoStream(Stream raw, int width, int height, double fps, TimeSpan start)
    {
        _raw = raw;
        _frameBytes = width * height * 4;
        _fps = fps;
        _start = start;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>입력이 끝났고 큐의 프레임도 모두 꺼냈는지.</summary>
    public bool IsCompleted => _frames.Reader.Completion.IsCompleted;

    public TimeSpan FrameDuration => TimeSpan.FromSeconds(1 / _fps);

    public bool TryPeek(out VideoFrame frame) => _frames.Reader.TryPeek(out frame);
    public bool TryRead(out VideoFrame frame) => _frames.Reader.TryRead(out frame);

    /// <summary>다 쓴 프레임 버퍼를 재사용하도록 돌려준다.</summary>
    public void Return(byte[] buffer) => _pool.Add(buffer);

    private async Task ReadLoopAsync()
    {
        try
        {
            for (long index = 0; !_cts.IsCancellationRequested; index++)
            {
                var buffer = _pool.TryTake(out var b) ? b : new byte[_frameBytes];
                var read = await _raw.ReadAtLeastAsync(buffer, _frameBytes, throwOnEndOfStream: false, _cts.Token);
                if (read < _frameBytes)
                    break;

                var pts = _start + TimeSpan.FromSeconds(index / _fps);
                await _frames.Writer.WriteAsync(new VideoFrame(pts, buffer), _cts.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 탐색/종료로 중단됨
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _reader.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
