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
    // 버퍼 12개 × 약 21ms = 약 256ms. 증폭(DSP)은 버퍼에 넣을 때 적용되므로 이 길이가 증폭 변경의 최대 지연이다.
    private const int BufferCount = 12;
    private const int BufferBytes = 1024 * BytesPerFrame;
    /// <summary>최대 음량 배율 (200%).</summary>
    internal const float MaxGain = 2f;

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

    /// <summary>100% 초과분을 담당하는 DSP. 디코딩 스레드에서만 사용.</summary>
    private readonly VolumeBooster _booster = new(SampleRate);
    /// <summary>DSP 증폭 목표 배율 (≥ 1). UI 스레드에서 쓰고 디코딩 스레드에서 읽는다.</summary>
    private volatile float _boost = 1f;
    private float _alGain = 1f;

    /// <summary>테스트용: OpenAL에 넘기기 직전의(증폭이 적용된) PCM.</summary>
    internal Action<ReadOnlyMemory<byte>>? BufferQueued { get; set; }

    public AudioStream(AudioDevice device, Stream pcm, float gain = 1f)
    {
        _device = device;
        _pcm = pcm;
        lock (device.Sync)
        {
            _source = device.Al.GenSource();
            _buffers = device.Al.GenBuffers(BufferCount);
        }
        // 스레드를 시작하기 전에 설정해야 첫 버퍼부터 이 음량이 적용된다
        SetGain(gain);
        _booster.StartAt(_boost);
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

    /// <summary>
    /// 출력 음량 배율 (0 = 무음, 1 = 원본, 최대 <see cref="MaxGain"/>).
    /// 1 이하는 OpenAL 게인으로 즉시 적용하고, 1을 넘는 부분은 OpenAL이 낼 수 없으므로
    /// 버퍼에 넣기 전 PCM에 DSP로 적용한다 (이미 큐에 있는 소리만큼, 최대 약 0.25초 늦게 반영).
    /// </summary>
    public void SetGain(float gain)
    {
        gain = Math.Clamp(gain, 0f, MaxGain);
        _boost = Math.Max(1f, gain);
        _alGain = Math.Min(1f, gain);
        lock (_device.Sync)
            _device.Al.SetSourceProperty(_source, SourceFloat.Gain, _alGain);
    }

    /// <summary>음량이 두 단계(OpenAL 게인 ≤ 1, DSP 증폭 ≥ 1)로 어떻게 나뉘었는지 (테스트·진단용).</summary>
    internal (float AlGain, float Boost) GainStages => (_alGain, _boost);

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

                    // 100% 초과 음량: OpenAL에 넘기기 전에 샘플 자체를 증폭 (리미터 포함)
                    _booster.Process(chunk.AsSpan(0, read), _boost);
                    BufferQueued?.Invoke(chunk.AsMemory(0, read));

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
