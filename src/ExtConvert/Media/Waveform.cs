using System.Buffers.Binary;
using System.Diagnostics;

namespace ExtConvert.Media;

/// <summary>
/// 오디오 파형 요약: 전체 길이를 일정한 구간(bucket)으로 나눠 구간별 최소/최대 샘플 값(-1~1)을 담는다.
/// 백그라운드에서 앞쪽부터 채워지며, <see cref="Count"/>까지만 유효하다.
/// </summary>
public sealed class Waveform
{
    /// <summary>파형 분석용 디코딩 샘플 레이트. 화면 표시에는 이 정도로 충분하고 디코딩이 빠르다.</summary>
    public const int SampleRate = 8000;

    /// <summary>길이를 알 때 나누는 구간 수 (넓은 화면에서도 픽셀보다 많도록).</summary>
    public const int DefaultBuckets = 4000;

    /// <summary>길이를 모를 때의 구간 길이 (샘플 수, 10ms).</summary>
    private const int UnknownDurationBucketSamples = SampleRate / 100;

    private float[] _min;
    private float[] _max;
    private int _count;

    private Waveform(int capacity, bool fixedLength)
    {
        _min = new float[capacity];
        _max = new float[capacity];
        IsFixedLength = fixedLength;
    }

    /// <summary>
    /// 길이를 알아서 구간 수가 미리 정해졌는지. false면 분석이 진행될수록 <see cref="TotalBuckets"/>가 늘어난다.
    /// </summary>
    public bool IsFixedLength { get; }

    /// <summary>지금까지 채워진 구간 수.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>가로축 전체에 해당하는 구간 수 (길이를 모르면 지금까지 채워진 수).</summary>
    public int TotalBuckets => IsFixedLength ? _min.Length : Count;

    /// <summary>채워진 구간 중 가장 큰 절댓값 (정규화용).</summary>
    public float Peak { get; private set; }

    public bool IsComplete { get; private set; }

    public float Min(int i) => _min[i];
    public float Max(int i) => _max[i];

    /// <summary>
    /// 첫 번째 오디오 트랙의 파형을 분석한다. <paramref name="progress"/>에는 같은 인스턴스가
    /// 채워지는 동안 주기적으로 보고된다 (화면에 점진적으로 그리기 위함).
    /// </summary>
    public static async Task<Waveform> ExtractAsync(string path, TimeSpan? duration,
        IProgress<Waveform>? progress = null, CancellationToken ct = default)
    {
        var totalSamples = (duration?.TotalSeconds ?? 0) * SampleRate;
        var fixedLength = totalSamples >= DefaultBuckets;
        var samplesPerBucket = fixedLength ? totalSamples / DefaultBuckets : UnknownDurationBucketSamples;
        var waveform = new Waveform(fixedLength ? DefaultBuckets : 1024, fixedLength);

        using var process = FFmpeg.StartStreaming(
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", path,
            "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-ac", "1", "-ar", SampleRate.ToString(),
            "-f", "s16le", "pipe:1",
        ]);
        await using var _ = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        await Task.Run(() => waveform.Fill(process.StandardOutput.BaseStream, samplesPerBucket, progress, ct), ct);
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        waveform.IsComplete = true;
        progress?.Report(waveform);
        return waveform;
    }

    private void Fill(Stream pcm, double samplesPerBucket, IProgress<Waveform>? progress, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var pending = 0;      // 이전 읽기에서 남은 홀수 바이트
        long sampleIndex = 0;
        var bucket = 0;
        float min = 0, max = 0;
        var nextBoundary = samplesPerBucket;
        var lastReport = Stopwatch.StartNew();

        int read;
        while ((read = pcm.Read(buffer, pending, buffer.Length - pending)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var available = pending + read;
            var usable = available & ~1;

            for (var i = 0; i < usable; i += 2)
            {
                var s = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i)) / 32768f;
                if (s < min) min = s;
                if (s > max) max = s;

                if (++sampleIndex >= nextBoundary)
                {
                    Store(bucket++, min, max);
                    min = max = 0;
                    nextBoundary = (bucket + 1) * samplesPerBucket;
                    if (IsFixedLength && bucket >= _min.Length)
                        return; // 실제 길이가 메타데이터보다 길면 나머지는 버린다
                }
            }

            pending = available - usable;
            if (pending > 0)
                buffer[0] = buffer[usable];

            if (progress is not null && lastReport.ElapsedMilliseconds >= 100)
            {
                progress.Report(this);
                lastReport.Restart();
            }
        }

        // 마지막 덜 찬 구간
        if (sampleIndex > (long)(bucket * samplesPerBucket) && (!IsFixedLength || bucket < _min.Length))
            Store(bucket, min, max);
    }

    private void Store(int index, float min, float max)
    {
        if (index >= _min.Length)
        {
            Array.Resize(ref _min, _min.Length * 2);
            Array.Resize(ref _max, _max.Length * 2);
        }
        _min[index] = min;
        _max[index] = max;
        Peak = Math.Max(Peak, Math.Max(-min, max));
        // 값을 쓴 다음에 개수를 공개해야 UI 스레드가 덜 쓴 값을 읽지 않는다
        Volatile.Write(ref _count, index + 1);
    }
}
