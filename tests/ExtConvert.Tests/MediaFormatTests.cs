using ExtConvert.Conversion;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

/// <summary>등록된 모든 동영상/오디오 형식으로 실제 변환해 보고 결과를 ffprobe로 확인한다.</summary>
public class MediaFormatTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static void RequireFFmpeg() =>
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");

    private async Task<MediaInfo> ConvertAsync(string input, IMediaExporter exporter, MediaExportOptions options,
        string? name = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await FFmpeg.ProbeAsync(input, ct);
        var output = Path.Combine(fx.Dir, (name ?? "conv") + "." + exporter.Extension);
        await FFmpeg.ConvertAsync(exporter.BuildArguments(input, output, source, options), source.Duration, null, ct);
        return await FFmpeg.ProbeAsync(output, ct);
    }

    [Fact]
    public async Task AllAvailableFormats_ConvertFromVideoWithAudio()
    {
        RequireFFmpeg();
        var failures = new List<string>();

        foreach (var exporter in Exporters.Media.Where(e => e.IsAvailable))
        {
            try
            {
                var result = await ConvertAsync(fx.VideoWithAudio, exporter, new MediaExportOptions(), "all");
                if (exporter.RequiresVideo)
                {
                    if (!result.HasVideo)
                        failures.Add($"{exporter.Extension}: 비디오 트랙 없음");
                    var keepsAudio = exporter.Extension is not ("gif" or "webp");
                    if (keepsAudio != result.HasAudio)
                        failures.Add($"{exporter.Extension}: 오디오 트랙 {(result.HasAudio ? "있음" : "없음")}");
                }
                else if (result.HasVideo || !result.HasAudio)
                {
                    failures.Add($"{exporter.Extension}: 오디오 전용이 아님");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{exporter.Extension}: {ex.Message}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task EveryVideoCodecChoice_Converts()
    {
        RequireFFmpeg();
        var failures = new List<string>();

        foreach (var exporter in Exporters.Media.Where(e => e.IsAvailable && e.VideoCodecs.Count > 1))
            foreach (var codec in exporter.VideoCodecs.Skip(1))
            {
                try
                {
                    var result = await ConvertAsync(fx.VideoOnly, exporter, new MediaExportOptions { VideoCodec = codec.Name }, "codec");
                    if (!result.HasVideo)
                        failures.Add($"{exporter.Extension}/{codec.Name}: 비디오 트랙 없음");
                }
                catch (Exception ex)
                {
                    failures.Add($"{exporter.Extension}/{codec.Name}: {ex.Message}");
                }
            }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task CopyStreams_RemuxesWithoutReencoding()
    {
        RequireFFmpeg();
        var source = await FFmpeg.ProbeAsync(fx.VideoWithAudio, TestContext.Current.CancellationToken);
        var mkv = Exporters.Media.FindByExtension("mkv")!;
        Assert.True(mkv.CanCopy(source));

        var result = await ConvertAsync(fx.VideoWithAudio, mkv, new MediaExportOptions { CopyStreams = true }, "remux");
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("aac", result.AudioCodec);

        // h264는 WebM에 담을 수 없으므로 복사 불가
        Assert.False(Exporters.Media.FindByExtension("webm")!.CanCopy(source));
    }

    [Fact]
    public async Task CopyStreams_ExtractsAacToM4aAsIs()
    {
        RequireFFmpeg();
        var m4a = Exporters.Media.FindByExtension("m4a")!;
        var args = m4a.BuildArguments(fx.VideoWithAudio, "out.m4a",
            await FFmpeg.ProbeAsync(fx.VideoWithAudio, TestContext.Current.CancellationToken),
            new MediaExportOptions { CopyStreams = true });
        Assert.Contains("copy", args);

        var result = await ConvertAsync(fx.VideoWithAudio, m4a, new MediaExportOptions { CopyStreams = true }, "extract");
        Assert.Equal("aac", result.AudioCodec);
        Assert.False(result.HasVideo);
    }

    [Fact]
    public async Task ResolutionAndFrameRate_AreApplied()
    {
        RequireFFmpeg();
        var mp4 = Exporters.Media.FindByExtension("mp4")!;
        var result = await ConvertAsync(fx.VideoWithAudio, mp4,
            new MediaExportOptions { MaxResolution = 120, FrameRate = 10 }, "scaled");

        Assert.Equal((160, 120), (result.Width, result.Height));
        Assert.Equal(10, result.FrameRate, 1);
    }

    [Fact]
    public async Task AnimatedGif_IsDetectedByFrameCount()
    {
        RequireFFmpeg();
        var gif = Exporters.Media.FindByExtension("gif")!;
        var result = await ConvertAsync(fx.VideoOnly, gif,
            new MediaExportOptions { MaxResolution = gif.DefaultMaxResolution, FrameRate = gif.DefaultFrameRate }, "anim");

        Assert.Equal("gif", result.VideoCodec);
        Assert.True(result.FrameCount > 1);
    }

    [Fact]
    public async Task OddDimensions_AreRoundedForYuv420Codecs()
    {
        RequireFFmpeg();
        var ct = TestContext.Current.CancellationToken;
        var odd = Path.Combine(fx.Dir, "odd.mkv");
        await FFmpeg.ConvertAsync(
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=0.5:size=161x121:rate=10",
            "-c:v", "ffv1", odd,
        ], null, null, ct);

        var result = await ConvertAsync(odd, Exporters.Media.FindByExtension("mp4")!, new MediaExportOptions(), "even");
        Assert.Equal((160, 120), (result.Width, result.Height));
    }
}
