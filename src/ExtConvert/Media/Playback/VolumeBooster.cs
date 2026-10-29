using System.Runtime.InteropServices;

namespace ExtConvert.Media.Playback;

/// <summary>
/// 100%를 넘는 음량을 위한 DSP: s16le 스테레오 PCM을 제자리에서 증폭하고 리미터로 클리핑을 막는다.
/// OpenAL 출력은 원본(100%)보다 크게 낼 수 없으므로 증폭은 버퍼에 넣기 전에 샘플 자체에 적용해야 한다.
/// <list type="bullet">
/// <item>배율 변경은 한 버퍼에 걸쳐 직선으로 바꿔서 "딱" 소리(zipper noise)를 막는다.</item>
/// <item>리미터: 증폭된 프레임이 <see cref="Ceiling"/>을 넘으면 그 즉시 음량을 낮추고(attack 0),
/// 이후 <c>releaseMs</c>에 걸쳐 천천히 되돌린다. 출력은 항상 Ceiling 이하라 잘리는 소리가 나지 않는다.</item>
/// <item>배율 1이고 리미터가 쉬고 있으면 아무것도 하지 않는다 (원본 그대로, 비트 단위로 동일).</item>
/// </list>
/// 디코딩 스레드 하나에서만 호출한다.
/// </summary>
internal sealed class VolumeBooster
{
    /// <summary>리미터 상한 (풀스케일 대비, 약 -0.3dBFS).</summary>
    public const float Ceiling = 0.97f;

    private readonly float _releaseCoeff;
    private float _boost = 1f;
    /// <summary>리미터가 현재 적용 중인 감쇠 배율 (1 = 감쇠 없음).</summary>
    private float _reduction = 1f;

    public VolumeBooster(int sampleRate, double releaseMs = 150) =>
        _releaseCoeff = (float)Math.Exp(-1.0 / (sampleRate * releaseMs / 1000));

    /// <summary>지금 적용 중인 증폭 배율.</summary>
    public float Boost => _boost;

    /// <summary>처음부터 이 배율로 시작한다 (스트림 시작 시 1에서 올라가는 램프 없이).</summary>
    public void StartAt(float boost) => _boost = Math.Max(1f, boost);

    /// <summary>
    /// <paramref name="pcm"/>(s16le, 스테레오 인터리브)을 증폭한다. 배율은 이 버퍼 동안 현재 값에서 <paramref name="target"/>로 바뀐다.
    /// </summary>
    public void Process(Span<byte> pcm, float target)
    {
        target = Math.Max(1f, target);
        if (target == 1f && _boost == 1f && _reduction >= 0.9999f)
        {
            _reduction = 1f;
            return;
        }

        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        var frames = samples.Length / 2;
        if (frames == 0)
            return;

        var boost = _boost;
        var step = (target - boost) / frames;
        var reduction = _reduction;

        for (var f = 0; f < frames; f++)
        {
            boost += step;
            var l = samples[2 * f] / 32768f * boost;
            var r = samples[2 * f + 1] / 32768f * boost;

            // 두 채널 중 큰 쪽 기준으로 같은 감쇠를 적용해야 좌우 균형(정위)이 흔들리지 않는다
            var peak = Math.Max(Math.Abs(l), Math.Abs(r));
            var needed = peak > Ceiling ? Ceiling / peak : 1f;
            reduction = needed < reduction ? needed : needed + (reduction - needed) * _releaseCoeff;

            samples[2 * f] = ToShort(l * reduction);
            samples[2 * f + 1] = ToShort(r * reduction);
        }

        _boost = target; // 누적 오차 제거
        _reduction = reduction;
    }

    private static short ToShort(float v) => (short)Math.Clamp(MathF.Round(v * 32768f), short.MinValue, short.MaxValue);
}
