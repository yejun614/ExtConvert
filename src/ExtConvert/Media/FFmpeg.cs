using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ExtConvert.Media;

/// <summary>
/// 시스템에 설치된 ffmpeg / ffprobe 실행 파일을 찾아 실행한다.
/// 탐색 순서: EXTCONVERT_FFMPEG_DIR 환경 변수 → 앱 폴더(및 그 아래 ffmpeg/) → PATH → OS별 기본 설치 위치.
/// </summary>
public static class FFmpeg
{
    private static readonly Lazy<string?> FFmpegExe = new(() => Locate("ffmpeg"));
    private static readonly Lazy<string?> FFprobeExe = new(() => Locate("ffprobe"));

    public static string? FFmpegPath => FFmpegExe.Value;
    public static string? FFprobePath => FFprobeExe.Value;
    public static bool IsAvailable => FFmpegPath is not null && FFprobePath is not null;

    private static readonly Lazy<HashSet<string>> Encoders = new(ListEncoders);

    /// <summary>설치된 ffmpeg에 해당 인코더(예: "libx264")가 포함되어 있는지. 빌드마다 다르다.</summary>
    public static bool HasEncoder(string name) => Encoders.Value.Contains(name);

    public static string InstallHint =>
        OperatingSystem.IsWindows() ? "설치: winget install Gyan.FFmpeg" :
        OperatingSystem.IsMacOS() ? "설치: brew install ffmpeg" :
        "설치: sudo apt install ffmpeg (또는 배포판 패키지 관리자)";

    private static string? Locate(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var dirs = new List<string>();

        if (Environment.GetEnvironmentVariable("EXTCONVERT_FFMPEG_DIR") is { Length: > 0 } custom)
            dirs.Add(custom);
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        // Finder/데스크톱에서 실행한 GUI 앱은 셸의 PATH를 물려받지 못하므로 흔한 위치를 직접 확인
        if (OperatingSystem.IsMacOS())
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"]);
        else if (OperatingSystem.IsLinux())
            dirs.AddRange(["/usr/bin", "/usr/local/bin", "/snap/bin"]);

        return dirs
            .Select(d => Path.Combine(d.Trim().Trim('"'), exe))
            .FirstOrDefault(File.Exists);
    }

    private static HashSet<string> ListEncoders()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (FFmpegPath is null)
            return set;
        try
        {
            var psi = new ProcessStartInfo(FFmpegPath, "-hide_banner -encoders")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi)!;
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            // 출력 예: " V....D libx264              libx264 H.264 ..." (헤더 뒤 " ------" 줄 다음부터 목록)
            var started = false;
            while (process.StandardOutput.ReadLine() is { } line)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!started)
                    started = parts is ["------", ..];
                else if (parts.Length >= 2)
                    set.Add(parts[1]);
            }
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        return set;
    }

    /// <summary>ffprobe로 파일 정보를 읽는다.</summary>
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var result = await RunAsync(RequirePath(FFprobePath, "ffprobe"),
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            onStdoutLine: null, ct);
        result.ThrowIfFailed("ffprobe");
        return MediaInfo.Parse(Encoding.UTF8.GetString(result.Stdout));
    }

    /// <summary>
    /// 지정한 시점의 프레임 하나를 PNG로 추출한다. 비디오 트랙이 없으면 null.
    /// <paramref name="maxWidth"/>가 null이면 원본 해상도.
    /// </summary>
    public static async Task<byte[]?> ExtractFramePngAsync(string path, TimeSpan at, int? maxWidth = 960,
        CancellationToken ct = default)
    {
        List<string> args =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path,
            "-frames:v", "1",
        ];
        if (maxWidth is { } mw)
            args.AddRange(["-vf", $"scale='min({mw},iw)':-2"]);
        args.AddRange(["-f", "image2pipe", "-c:v", "png", "pipe:1"]);

        var result = await RunAsync(RequirePath(FFmpegPath, "ffmpeg"), args, onStdoutLine: null, ct);
        return result.ExitCode == 0 && result.Stdout.Length > 0 ? result.Stdout : null;
    }

    /// <summary>
    /// Avalonia가 직접 읽지 못하는 이미지(TIFF, AVIF, HEIC, PSD 등)를 원본 크기의 RGBA PNG로 디코딩한다.
    /// </summary>
    public static async Task<byte[]> DecodeImagePngAsync(string path, CancellationToken ct = default)
    {
        var result = await RunAsync(RequirePath(FFmpegPath, "ffmpeg"),
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", path,
            "-frames:v", "1",
            "-f", "image2pipe", "-c:v", "png", "-pix_fmt", "rgba", "pipe:1",
        ], onStdoutLine: null, ct);

        result.ThrowIfFailed("ffmpeg");
        if (result.Stdout.Length == 0)
            throw new InvalidOperationException("이미지를 디코딩하지 못했습니다.");
        return result.Stdout;
    }

    /// <summary>
    /// ffmpeg 변환을 실행한다. 인자에 "-progress pipe:1"이 포함되어 있으면
    /// <paramref name="progress"/>에 0~1 진행률을 보고한다.
    /// 취소되면 프로세스를 종료하고 <see cref="OperationCanceledException"/>을 던진다.
    /// </summary>
    public static async Task ConvertAsync(IReadOnlyList<string> args, TimeSpan? duration,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var total = duration?.TotalMicroseconds ?? 0;
        var result = await RunAsync(RequirePath(FFmpegPath, "ffmpeg"), args, line =>
        {
            if (progress is null || total <= 0)
                return;
            // -progress 출력 예: "out_time_us=12345678"
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), out var us))
                progress.Report(Math.Clamp(us / total, 0, 1));
            else if (line == "progress=end")
                progress.Report(1);
        }, ct);

        result.ThrowIfFailed("ffmpeg");
    }

    /// <summary>
    /// 출력을 계속 읽어야 하는 ffmpeg 프로세스(재생용 디코더)를 시작한다.
    /// stdout은 호출자가 읽고, stderr는 파이프가 막히지 않도록 버린다. 종료는 호출자 책임.
    /// </summary>
    internal static Process StartStreaming(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(RequirePath(FFmpegPath, "ffmpeg"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 실행 실패");
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private static string RequirePath(string? path, string name) =>
        path ?? throw new FileNotFoundException($"{name}을(를) 찾을 수 없습니다. {InstallHint}");

    private static async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args,
        Action<string>? onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} 실행 실패");

        // stdout/stderr를 동시에 비워야 파이프 버퍼가 차서 멈추는 일이 없다
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Task<byte[]> stdoutTask = onStdoutLine is null
            ? ReadAllBytesAsync(process.StandardOutput.BaseStream)
            : ReadLinesAsync(process.StandardOutput, onStdoutLine);

        // 취소 시 즉시(동기적으로) 프로세스를 종료한다. 창을 닫는 중이라 이후 continuation이
        // 실행되지 않더라도 ffmpeg가 남아 있지 않도록 하기 위함.
        await using (ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        ct.ThrowIfCancellationRequested();

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
            onLine(line);
        return [];
    }

    private sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr)
    {
        public void ThrowIfFailed(string tool)
        {
            if (ExitCode == 0)
                return;
            // ffmpeg 오류 메시지는 보통 stderr 마지막 줄에 있다
            var last = Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? $"exit code {ExitCode}";
            throw new InvalidOperationException($"{tool} 오류: {last}");
        }
    }
}
