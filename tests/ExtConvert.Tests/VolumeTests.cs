using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ExtConvert.Media;
using ExtConvert.Media.Playback;
using Xunit;

namespace ExtConvert.Tests;

/// <summary>재생 볼륨 0~200% (기본 100%). 100% 초과분은 OpenAL이 아니라 PCM 단계의 DSP가 증폭한다.</summary>
public class VolumeTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private const int Rate = AudioStream.SampleRate;

    /// <summary>440Hz 사인파, 진폭 <paramref name="amplitude"/>(0~1), s16le 스테레오.</summary>
    private static byte[] Sine(double amplitude, double seconds)
    {
        var frames = (int)(Rate * seconds);
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++)
            samples[2 * i] = samples[2 * i + 1] = (short)Math.Round(amplitude * 32767 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static double Peak(ReadOnlySpan<byte> pcm) =>
        MemoryMarshal.Cast<byte, short>(pcm).ToArray().Max(s => Math.Abs((int)s)) / 32768.0;

    private static double Rms(ReadOnlySpan<byte> pcm)
    {
        var s = MemoryMarshal.Cast<byte, short>(pcm).ToArray();
        return Math.Sqrt(s.Average(x => (x / 32768.0) * (x / 32768.0)));
    }

    // ───── 볼륨 → 배율 ─────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.25)] // 100% 이하는 제곱 곡선
    [InlineData(1, 1)]      // 100% = 원본
    [InlineData(1.5, 1.5)]  // 100% 초과는 직선
    [InlineData(2, 2)]      // 200% = 2배 (+6dB)
    [InlineData(3, 2)]      // 상한
    public void GainFor_MapsVolumeToAmplification(double volume, float gain) =>
        Assert.Equal(gain, MediaPlayer.GainFor(volume), 3);

    // ───── DSP (VolumeBooster) ─────

    [Fact]
    public void Booster_At100Percent_LeavesSamplesUntouched()
    {
        var pcm = Sine(0.9, 0.1);
        var original = pcm.ToArray();
        new VolumeBooster(Rate).Process(pcm, 1f);
        Assert.Equal(original, pcm);
    }

    [Fact]
    public void Booster_At200Percent_DoublesQuietAudio()
    {
        var pcm = Sine(0.25, 0.2);
        var booster = new VolumeBooster(Rate);
        booster.StartAt(2f);
        booster.Process(pcm, 2f);

        Assert.Equal(0.5, Peak(pcm), 2); // 리미터에 걸리지 않는 크기는 정확히 2배
    }

    [Fact]
    public void Booster_LoudAudio_IsLimitedInsteadOfClipped()
    {
        var input = Sine(0.8, 0.5);
        var pcm = input.ToArray();
        var booster = new VolumeBooster(Rate);
        booster.StartAt(2f);
        booster.Process(pcm, 2f);

        // 1.6배가 될 소리지만 상한을 넘지 않고(잘림 없음), 그래도 원본보다는 크게 들린다
        Assert.True(Peak(pcm) <= VolumeBooster.Ceiling + 1e-4, $"peak {Peak(pcm)}");
        Assert.True(Rms(pcm) > Rms(input) * 1.15, $"rms {Rms(pcm)} vs {Rms(input)}");
    }

    [Fact]
    public void Booster_ChangeIsRampedWithinBuffer()
    {
        var pcm = Sine(0.25, 0.1);
        var booster = new VolumeBooster(Rate); // 1배에서 시작
        booster.Process(pcm, 2f);

        // 버퍼 앞부분은 거의 원래 크기, 뒷부분은 거의 2배 → 갑자기 바뀌지 않는다
        var quarter = pcm.Length / 4 & ~3;
        Assert.InRange(Peak(pcm.AsSpan(0, quarter)), 0.24, 0.32);
        Assert.InRange(Peak(pcm.AsSpan(pcm.Length - quarter)), 0.44, 0.51);
        Assert.Equal(2f, booster.Boost);
    }

    [Fact]
    public void Booster_LimiterRecoversAfterLoudPart()
    {
        var booster = new VolumeBooster(Rate);
        booster.StartAt(2f);
        booster.Process(Sine(0.9, 0.3), 2f); // 리미터가 크게 작동
        var quiet = Sine(0.25, 1.0);
        booster.Process(quiet, 2f);

        // 큰 소리가 지나가면(약 150ms 릴리스) 다시 온전히 2배
        Assert.Equal(0.5, Peak(quiet.AsSpan(quiet.Length / 2 & ~3)), 2);
    }

    // ───── 실제 오디오 스트림: OpenAL로 넘어가는 PCM이 증폭되는지 ─────

    private static List<byte[]> CaptureQueuedBuffers(byte[] pcm, float gain, out (float AlGain, float Boost) stages)
    {
        var captured = new ConcurrentQueue<byte[]>();
        // 콜백은 생성자에서 넘긴다: 스레드가 곧바로 버퍼를 채우므로 나중에 연결하면 빠른 기기에서 앞부분을 놓친다
        using var stream = new AudioStream(AudioDevice.Shared!, new MemoryStream(pcm), gain,
            chunk => captured.Enqueue(chunk.ToArray()));
        stages = stream.GainStages;

        // 재생하지 않아도 스레드가 큐(12개)를 채운다
        var sw = Stopwatch.StartNew();
        while (captured.Count < 8 && sw.Elapsed < TimeSpan.FromSeconds(3))
            Thread.Sleep(10);
        return captured.ToList();
    }

    [Fact]
    public void AudioStream_Above100Percent_AmplifiesSamplesSentToOpenAl()
    {
        Assert.SkipUnless(AudioDevice.Shared is not null, "오디오 출력 장치가 없습니다.");

        var boosted = CaptureQueuedBuffers(Sine(0.25, 1), 2f, out var stages);
        Assert.Equal((1f, 2f), stages); // OpenAL은 100%, 나머지 2배는 DSP
        Assert.True(boosted.Count >= 8);
        Assert.All(boosted, b => Assert.Equal(0.5, Peak(b), 2)); // 첫 버퍼부터 2배 (탐색 직후에도)

        var normal = CaptureQueuedBuffers(Sine(0.25, 1), 0.5f, out stages);
        Assert.Equal((0.5f, 1f), stages); // 100% 이하는 OpenAL 게인만, 샘플은 원본 그대로
        Assert.All(normal, b => Assert.Equal(0.25, Peak(b), 2));
    }

    // ───── 화면 ─────

    [AvaloniaFact]
    public async Task Slider_GoesTo200PercentAndDefaultsTo100()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var window = new MainWindow();
        window.Show();
        await window.LoadFileAsync(fx.VideoWithAudio);

        var slider = window.FindControl<Slider>("VolumeSlider")!;
        var text = window.FindControl<TextBlock>("VolumeText")!;
        Assert.Equal(200, slider.Maximum);
        Assert.Equal(100, slider.Value);
        Assert.Equal("100%", text.Text);
        var normal = text.Foreground;
        Assert.NotNull(normal); // 글자가 보여야 한다 (테마 기본 색)

        slider.Value = 150;
        Assert.Equal("150%", text.Text);
        Assert.NotNull(text.Foreground);
        Assert.NotSame(normal, text.Foreground); // 증폭 중이면 강조색

        slider.Value = 97; // 100% 근처는 100%로 맞춤
        Assert.Equal("100%", text.Text);
        Assert.Equal(100, slider.Value);
        Assert.Same(normal, text.Foreground); // 다시 기본 색

        slider.Value = 200;
        Assert.Equal("200%", text.Text);
        window.Close();
    }
}
