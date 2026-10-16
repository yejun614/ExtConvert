using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ExtConvert.Media.Playback;

/// <summary>
/// FFmpeg로 디코딩하고 OpenAL로 소리를 내는 간단한 재생기.
/// 모든 공개 멤버는 UI 스레드에서 호출해야 한다. 화면 갱신마다 <see cref="Tick"/>을 호출하면
/// 현재 시각에 맞는 프레임을 <see cref="Frame"/>에 그린다.
/// </summary>
public sealed class MediaPlayer : IDisposable
{
    private const int MaxVideoWidth = 1280;
    private const int MaxVideoHeight = 720;
    private const double MaxFps = 60;

    private readonly string _path;
    private readonly MediaInfo _info;
    private readonly int _width;
    private readonly int _height;
    private readonly double _fps;

    private Session? _session;
    private TimeSpan _position;
    private bool _playing;
    private bool _muted;
    private double _volume = 1;

    public MediaPlayer(string path, MediaInfo info)
    {
        _path = path;
        _info = info;

        if (info.HasVideo)
        {
            (_width, _height) = FitSize(info.DisplaySize, MaxVideoWidth, MaxVideoHeight);
            _fps = info.FrameRate is > 0 and var f ? Math.Min(f, MaxFps) : 30;
            Frame = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
    }

    /// <summary>현재 프레임이 그려지는 비트맵. 비디오가 없으면 null.</summary>
    public WriteableBitmap? Frame { get; }

    /// <summary>오디오 트랙이 있지만 출력 장치를 쓸 수 없는 경우 false.</summary>
    public bool IsAudioOutputAvailable => !_info.HasAudio || AudioDevice.Shared is not null;

    /// <summary>재생할 수 있는 트랙(비디오 또는 소리를 낼 수 있는 오디오)이 있는지.</summary>
    public bool CanPlay => _info.HasVideo || (_info.HasAudio && AudioDevice.Shared is not null);

    public TimeSpan? Duration => _info.Duration;
    public bool IsPlaying => _playing;
    public bool IsEnded { get; private set; }

    /// <summary>탐색 직후 첫 프레임을 기다리는 중이거나 재생 중이면 true (화면 갱신 루프가 필요함).</summary>
    public bool NeedsTick => _playing || _session is { HasShownFrame: false, Video: not null };

    public TimeSpan Position
    {
        get
        {
            var pos = _session?.Clock ?? _position;
            return Duration is { } d && pos > d ? d : pos;
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyGain();
        }
    }

    /// <summary>볼륨 0~1. 음소거와는 별개로 유지된다.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            ApplyGain();
        }
    }

    // 사람 귀는 음량을 로그적으로 느끼므로 제곱 곡선을 써야 슬라이더 중간이 "절반 정도"로 들린다
    private float EffectiveGain => _muted ? 0f : (float)(_volume * _volume);

    private void ApplyGain() => _session?.Audio?.SetGain(EffectiveGain);

    public void Play()
    {
        if (!CanPlay || _playing)
            return;
        if (IsEnded)
            Seek(TimeSpan.Zero);

        _session ??= StartSession(_position);
        _playing = true;
        // 실제 시계 시작은 Tick에서 버퍼가 준비된 뒤에 한다 (시작 직후 프레임 누락 방지)
    }

    public void Pause()
    {
        if (!_playing)
            return;
        _playing = false;
        _session?.Pause();
    }

    public void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    /// <summary>디코더를 새 위치에서 다시 시작한다. 재생 중이면 그 위치부터 계속 재생한다.</summary>
    public void Seek(TimeSpan position)
    {
        if (Duration is { } d)
            position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, d.Ticks));
        else if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        _session?.Dispose();
        _position = position;
        IsEnded = false;
        _session = StartSession(position);
    }

    /// <summary>
    /// 화면 갱신마다 UI 스레드에서 호출한다. 새 프레임을 <see cref="Frame"/>에 그렸으면 true.
    /// </summary>
    public bool Tick()
    {
        if (_session is not { } s)
            return false;

        if (_playing && !s.Started && s.IsReady)
            s.Start();

        var changed = false;
        if (s.Video is { } video)
        {
            var clock = s.Clock;
            // 시각이 지난 프레임은 건너뛰고 가장 최근 것을 표시. 탐색 직후엔 첫 프레임을 즉시 표시.
            while (video.TryPeek(out var next) && (next.Pts <= clock || !s.HasShownFrame))
            {
                video.TryRead(out next);
                if (s.Current is { } old)
                    video.Return(old.Data);
                s.Current = next;
                changed = true;
                if (!s.HasShownFrame)
                {
                    s.HasShownFrame = true;
                    break;
                }
            }
            if (changed)
                CopyToFrame(s.Current!.Value.Data);
        }

        if (_playing && s.Started
            && (s.Video is null || s.Video.IsCompleted)
            && (s.Audio is null || s.Audio.IsDrained))
        {
            // 끝까지 재생함
            _position = Duration ?? s.Clock;
            _playing = false;
            IsEnded = true;
            s.Pause();
        }

        return changed;
    }

    private Session StartSession(TimeSpan start)
    {
        var ss = start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        Process? videoProc = null, audioProc = null;
        VideoStream? video = null;
        AudioStream? audio = null;

        if (_info.HasVideo)
        {
            videoProc = FFmpeg.StartStreaming(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-ss", ss, "-i", _path,
                "-map", "0:v:0", "-an", "-sn",
                "-vf", $"fps={_fps.ToString(CultureInfo.InvariantCulture)},scale={_width}:{_height}",
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
            ]);
            video = new VideoStream(videoProc.StandardOutput.BaseStream, _width, _height, _fps, start);
        }

        if (_info.HasAudio && AudioDevice.Shared is { } device)
        {
            audioProc = FFmpeg.StartStreaming(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-ss", ss, "-i", _path,
                "-map", "0:a:0", "-vn", "-sn",
                "-f", "s16le", "-acodec", "pcm_s16le", "-ac", "2", "-ar", AudioStream.SampleRate.ToString(),
                "pipe:1",
            ]);
            audio = new AudioStream(device, audioProc.StandardOutput.BaseStream);
            audio.SetGain(EffectiveGain);
        }

        return new Session(start, videoProc, audioProc, video, audio);
    }

    private unsafe void CopyToFrame(byte[] data)
    {
        using var fb = Frame!.Lock();
        var rowBytes = _width * 4;
        fixed (byte* src = data)
        {
            if (fb.RowBytes == rowBytes)
            {
                Buffer.MemoryCopy(src, (void*)fb.Address, (long)fb.RowBytes * _height, data.Length);
            }
            else
            {
                for (var y = 0; y < _height; y++)
                    Buffer.MemoryCopy(src + y * rowBytes, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, rowBytes);
            }
        }
    }

    /// <summary>비율을 유지하며 최대 크기 안에 맞추고, 인코더 호환을 위해 짝수로 만든다.</summary>
    internal static (int Width, int Height) FitSize((int Width, int Height) source, int maxWidth, int maxHeight)
    {
        var (w, h) = source;
        if (w <= 0 || h <= 0)
            return (2, 2);
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / w, (double)maxHeight / h));
        return (Math.Max(2, (int)Math.Round(w * scale / 2) * 2), Math.Max(2, (int)Math.Round(h * scale / 2) * 2));
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        Frame?.Dispose();
    }

    /// <summary>특정 위치에서 시작한 디코더 한 벌과 재생 시계.</summary>
    private sealed class Session(TimeSpan start, Process? videoProc, Process? audioProc,
        VideoStream? video, AudioStream? audio) : IDisposable
    {
        private readonly Stopwatch _wall = new();
        private TimeSpan? _audioEndClock;
        private TimeSpan _wallAtAudioEnd;

        public VideoStream? Video { get; } = video;
        public AudioStream? Audio { get; } = audio;
        public VideoFrame? Current { get; set; }
        public bool HasShownFrame { get; set; }
        public bool Started { get; private set; }

        /// <summary>첫 프레임과 오디오 버퍼가 준비됐는지 (각각 트랙이 없으면 준비된 것으로 본다).</summary>
        public bool IsReady =>
            (Video is null || Video.TryPeek(out _) || Video.IsCompleted || HasShownFrame)
            && (Audio is null || Audio.IsPrimed);

        /// <summary>
        /// 현재 재생 시각. 오디오가 있으면 실제로 재생된 샘플 수를 기준으로 하고(립싱크),
        /// 오디오가 없거나 먼저 끝나면 벽시계로 이어간다.
        /// </summary>
        public TimeSpan Clock
        {
            get
            {
                if (Audio is null)
                    return start + _wall.Elapsed;
                if (_audioEndClock is { } end)
                    return end + (_wall.Elapsed - _wallAtAudioEnd);

                var clock = start + Audio.Played;
                if (Started && Audio.IsDrained)
                {
                    _audioEndClock = clock;
                    _wallAtAudioEnd = _wall.Elapsed;
                }
                return clock;
            }
        }

        public void Start()
        {
            Started = true;
            _wall.Start();
            Audio?.Play();
        }

        public void Pause()
        {
            _wall.Stop();
            Audio?.Pause();
            Started = false; // 다시 재생할 때 Tick에서 Start
        }

        public void Dispose()
        {
            // 프로세스를 먼저 죽여야 파이프를 읽는 스레드가 풀린다
            Kill(videoProc);
            Kill(audioProc);
            Video?.Dispose();
            Audio?.Dispose();
            videoProc?.Dispose();
            audioProc?.Dispose();
        }

        private static void Kill(Process? p)
        {
            try { p?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
