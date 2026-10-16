using System.Diagnostics;
using Silk.NET.OpenAL;

namespace ExtConvert.Media.Playback;

/// <summary>앱 전체에서 공유하는 OpenAL 출력 장치. 장치가 없거나 초기화에 실패하면 null.</summary>
internal sealed unsafe class AudioDevice
{
    private static readonly Lazy<AudioDevice?> SharedDevice = new(TryCreate);
    public static AudioDevice? Shared => SharedDevice.Value;

    public AL Al { get; }
    /// <summary>AL 호출은 디코딩 스레드와 UI 스레드에서 모두 일어나므로 이 객체로 직렬화한다.</summary>
    public object Sync { get; } = new();

    private AudioDevice(AL al) => Al = al;

    private static AudioDevice? TryCreate()
    {
        try
        {
            var alc = ALContext.GetApi(soft: true);
            var al = AL.GetApi(soft: true);
            var device = alc.OpenDevice(null!); // null = 시스템 기본 출력 장치
            if (device == null)
                return null;
            var context = alc.CreateContext(device, null);
            if (context == null || !alc.MakeContextCurrent(context))
                return null;
            // 장치와 컨텍스트는 앱이 끝날 때까지 유지한다
            return new AudioDevice(al);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenAL 초기화 실패: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// ffmpeg가 출력하는 PCM(s16le, 48kHz, 스테레오)을 OpenAL 스트리밍 버퍼 큐로 재생한다.
/// 실제로 재생된 샘플 수가 재생 위치의 기준 시계가 된다.
/// </summary>
internal sealed class AudioStream : IDisposable
{
    public const int SampleRate = 48000;
    private const int BytesPerFrame = 4; // 16bit × 2ch
    private const int BufferCount = 6;
    private const int BufferBytes = 4096 * BytesPerFrame; // 약 85ms

    private readonly AudioDevice _device;
    private readonly Stream _pcm;
    private readonly uint _source;
    private readonly uint[] _buffers;
    private readonly Dictionary<uint, int> _bufferSizes = [];
    private readonly Thread _thread;

    private volatile bool _stop;
    private volatile bool _wantPlaying;
    private volatile bool _eof;
    private volatile bool _primed;
    private long _unqueuedBytes; // 재생이 끝나 큐에서 뺀 버퍼의 총 바이트
    private long _queuedBytes;   // 현재 큐에 있는 버퍼의 총 바이트
    private int _queuedCount;
    private long _lastPlayedFrames;

    public AudioStream(AudioDevice device, Stream pcm)
    {
        _device = device;
        _pcm = pcm;
        lock (device.Sync)
        {
            _source = device.Al.GenSource();
            _buffers = device.Al.GenBuffers(BufferCount);
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioStream" };
        _thread.Start();
    }

    /// <summary>재생을 시작할 만큼 버퍼가 찼는지 (또는 스트림이 이미 끝났는지).</summary>
    public bool IsPrimed => _primed || _eof;

    /// <summary>입력이 끝났고 큐에 넣은 소리도 모두 재생했는지.</summary>
    public bool IsDrained
    {
        get
        {
            if (!_eof) return false;
            lock (_device.Sync)
                return _queuedCount == 0 || State() == SourceState.Stopped;
        }
    }

    /// <summary>지금까지 스피커로 나간 시간.</summary>
    public TimeSpan Played
    {
        get
        {
            long frames;
            lock (_device.Sync)
            {
                // SAMPLE_OFFSET은 큐에 남아 있는 첫 버퍼 기준. 큐를 다 재생하고 멈춘 경우엔 0으로 돌아가므로 따로 처리.
                frames = State() == SourceState.Stopped && _eof
                    ? (_unqueuedBytes + _queuedBytes) / BytesPerFrame
                    : _unqueuedBytes / BytesPerFrame + GetInt(GetSourceInteger.SampleOffset);
            }
            // 버퍼 교체 순간의 미세한 역행을 막는다
            frames = Math.Max(frames, Interlocked.Read(ref _lastPlayedFrames));
            Interlocked.Exchange(ref _lastPlayedFrames, frames);
            return TimeSpan.FromSeconds((double)frames / SampleRate);
        }
    }

    public void Play()
    {
        _wantPlaying = true;
        lock (_device.Sync)
            if (_queuedCount > 0)
                _device.Al.SourcePlay(_source);
    }

    public void Pause()
    {
        _wantPlaying = false;
        lock (_device.Sync)
            _device.Al.SourcePause(_source);
    }

    /// <summary>출력 음량 (0 = 무음, 1 = 원본).</summary>
    public void SetGain(float gain)
    {
        lock (_device.Sync)
            _device.Al.SetSourceProperty(_source, SourceFloat.Gain, Math.Clamp(gain, 0f, 1f));
    }

    private void Run()
    {
        var chunk = new byte[BufferBytes];
        var free = new Queue<uint>(_buffers);

        try
        {
            while (!_stop)
            {
                // 재생이 끝난 버퍼 회수
                lock (_device.Sync)
                {
                    var processed = GetInt(GetSourceInteger.BuffersProcessed);
                    for (var i = 0; i < processed; i++)
                    {
                        uint buf = 0;
                        unsafe { _device.Al.SourceUnqueueBuffers(_source, 1, &buf); }
                        var size = _bufferSizes[buf];
                        _unqueuedBytes += size;
                        _queuedBytes -= size;
                        _queuedCount--;
                        free.Enqueue(buf);
                    }
                }

                // 빈 버퍼 채우기 (파이프 읽기는 블로킹이므로 락 밖에서)
                while (!_eof && free.Count > 0 && !_stop)
                {
                    var read = _pcm.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
                    read -= read % BytesPerFrame;
                    if (read == 0)
                    {
                        _eof = true;
                        break;
                    }

                    var buf = free.Dequeue();
                    lock (_device.Sync)
                    {
                        unsafe
                        {
                            fixed (byte* p = chunk)
                                _device.Al.BufferData(buf, BufferFormat.Stereo16, p, read, SampleRate);
                            _device.Al.SourceQueueBuffers(_source, 1, &buf);
                        }
                        _bufferSizes[buf] = read;
                        _queuedBytes += read;
                        _queuedCount++;
                    }
                }
                if (free.Count == 0)
                    _primed = true;

                // 시작 또는 버퍼 부족(underrun)으로 멈춘 경우 다시 재생
                lock (_device.Sync)
                {
                    if (_wantPlaying && _queuedCount > 0 && State() != SourceState.Playing)
                        _device.Al.SourcePlay(_source);
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 탐색/종료로 ffmpeg가 종료되어 파이프가 닫힘
            _eof = true;
        }
    }

    private SourceState State() => (SourceState)GetInt(GetSourceInteger.SourceState);

    private int GetInt(GetSourceInteger param)
    {
        _device.Al.GetSourceProperty(_source, param, out int value);
        return value;
    }

    /// <summary>호출 전에 PCM을 공급하는 ffmpeg 프로세스를 먼저 종료해야 스레드가 블로킹에서 풀린다.</summary>
    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));

        lock (_device.Sync)
        {
            var al = _device.Al;
            al.SourceStop(_source);
            al.SetSourceProperty(_source, SourceInteger.Buffer, 0); // 큐 비우기
            al.DeleteSource(_source);
            al.DeleteBuffers(_buffers);
        }
    }
}
