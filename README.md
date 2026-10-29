# Ext Convert

[![CI](https://github.com/yejun614/ExtConvert/actions/workflows/ci.yml/badge.svg)](https://github.com/yejun614/ExtConvert/actions/workflows/ci.yml)

파일 미리보기 + 파일 형식 변환 데스크톱 앱 (C# / .NET 10 / Avalonia 12). Windows, Linux, macOS 지원.

## 기능

- 파일을 창에 **드래그 앤 드롭**하거나, 빈 영역 클릭 / `파일 > 열기` (Ctrl+O)로 선택
- 이미지 미리보기 — 투명 영역은 체커보드로 표시
  - 기본: PNG, JPG, BMP, GIF, WebP, ICO
  - FFmpeg 사용: TIFF, TGA, PSD, DDS, AVIF, HEIC/HEIF, JPEG XL, JPEG 2000, QOI, PCX, EXR, HDR, PPM/PGM/PBM/PAM/PFM, SGI, Sun Raster, XBM, XPM, XWD, DPX, FITS, WBMP
  - **영역 선택 모드** (아래 편집 바의 `⬚ 영역 선택` 또는 `변환 > 선택 영역만 변환`): 기본은 전체 변환이고, 켰을 때만 고른 영역을 잘라서 저장/변환 (동영상도 동일)
    - 바깥에서 끌어 새 영역, 안쪽을 끌어 이동, 모서리·변 손잡이로 크기 조절 (영역 밖은 어둡게, 크기 표시)
    - X·Y·너비·높이 칸에 픽셀 값 직접 입력
    - 비율 고정: 자유 / 원본 비율 / 1:1 / 4:3 / 3:2 / 16:9 / 3:4 / 2:3 / 9:16 (고정하면 모서리 손잡이만)
    - 저장/변환 메뉴 이름이 `선택 영역...`으로 바뀌고 파일 이름에 크기가 붙음 (예: `photo_640x360.png`)
- 동영상/오디오 미리보기 (FFmpeg 필요) — 동영상은 썸네일, 상태바에 길이·해상도·코덱 표시
  - 동영상: MP4, M4V, MOV, MKV, WebM, AVI, WMV, ASF, FLV, F4V, TS, M2TS, MTS, MPG, VOB, 3GP, OGV, MXF, RM/RMVB 등
  - 오디오: MP3, M4A, AAC, WAV, FLAC, OGG, Opus, WMA, AIFF, AC3, DTS, AMR, APE, WavPack, MKA, CAF 등
  - **애니메이션 GIF**는 동영상처럼 재생되고 동영상 형식으로 저장 가능
  - **동영상 편집**: 구간 선택(시작/끝)과 영역 선택(화면 일부)을 따로 또는 함께 켤 수 있음
    - 구간 선택을 켜면 재생 바 위에 타임라인이 나타남 (오디오 트랙이 있으면 파형 표시). 조작은 아래 오디오 구간 선택과 같음
    - 영역 선택은 이미지와 같은 방식이며, 좌표는 회전이 반영된 화면 크기 기준. 동영상 형식과 현재 프레임 저장에 적용 (오디오 추출에는 구간만 적용)
    - 영역을 자르면 재인코딩이 필요하므로 `재인코딩 없이 저장`은 숨겨짐. 구간만 자를 때 재인코딩 없이 저장하면 키프레임 단위로 잘림
    - 저장 파일 이름 예: `clip_0012-0045_640x360.mp4`
  - **오디오는 파형으로 표시** — 분석하면서 앞에서부터 점진적으로 그려지고, 재생된 부분은 강조색. 파형을 클릭하거나 끌었다 놓으면 그 위치로 이동
  - **구간 선택 모드** (아래 편집 바의 `✂ 구간 선택` 또는 `변환 > 선택 구간만 변환`): 기본은 전체 변환이고, 켰을 때만 고른 구간을 저장/변환
    - 파형을 끌어서 구간 선택, 양 끝을 끌어 조절, `Shift`+클릭으로 가까운 끝 이동, 클릭은 탐색
    - 시작/끝 칸에 시각 직접 입력 (`1:23.4`, `83.4`, `1:02:03` 형식, `Enter`로 적용)
    - `[` / `]` 버튼 또는 `I` / `O` 키: 현재 재생 위치를 시작/끝으로
    - 재생하면 구간 처음부터 재생하고 구간 끝에서 멈춤
    - 저장/변환 메뉴 이름이 `선택 구간...`으로 바뀌고 파일 이름에 구간이 붙음 (예: `song_0012-0045.mp3`)
- **동영상/오디오 재생**: 재생/일시정지, 탐색 바, 시간 표시, 볼륨 슬라이더(0~200%, 기본 100%), 음소거 — FFmpeg 디코딩 + OpenAL Soft 출력, 오디오 기준 립싱크
  - 볼륨: 100%가 원본 음량, 100%를 넘기면 증폭 (200% = 2배, +6dB). 100% 근처로 끌면 정확히 100%로 맞춰지고, 증폭 중에는 % 표시가 강조색
    - 0~100%는 OpenAL 게인(즉시 반영), 100% 초과분은 OpenAL에 넘기기 전 PCM에 DSP로 적용 (증폭 + 리미터로 클리핑 방지, 변경은 최대 약 0.25초 뒤 반영)
  - 단축키: `Space` 재생/일시정지 · `←`/`→` 5초 이동 · `↑`/`↓` 볼륨 · `M` 음소거 · 미리보기 클릭으로 재생/일시정지
- **`변환` 메뉴**: `이미지` / `동영상` / `오디오` 하위 메뉴에서 형식을 바로 골라 변환. 열린 파일로 만들 수 있는 형식만 활성화됩니다
  - 이미지 파일 → 이미지 형식만, 동영상 → 동영상·오디오(오디오 트랙이 있을 때)·이미지(현재 프레임), 오디오 → 오디오 형식만
  - 설치된 ffmpeg에 인코더가 없는 형식은 비활성화되고 이유가 툴팁으로 표시됨
- `파일 > 다른 이름으로 저장` (Ctrl+Shift+S)으로 형식 변환. 설치된 ffmpeg에 해당 인코더가 있는 형식만 목록에 나타납니다.
  - **이미지 → 이미지**
    - 기본: **ICO** (16~256px 중 원하는 크기를 한 파일에, 정사각형이 아니면 투명 여백), **PNG**, **JPEG**, **BMP** (투명하면 32비트 알파)
    - FFmpeg 사용: WebP, GIF, TIFF, AVIF, JPEG XL, JPEG 2000, TGA, QOI, OpenEXR, Radiance HDR, DPX, PCX, SGI, PPM
    - 투명도를 지원하지 않는 형식(JPEG, AVIF, HDR, DPX, PCX, PPM)은 흰색 배경에 합성
  - **동영상 → 동영상**: MP4, MKV, WebM, MOV, AVI, WMV, FLV, MPEG-TS, MPEG-2(MPG), 3GP, OGV, 애니메이션 GIF, 애니메이션 WebP
    - 옵션: 비디오 코덱 (MP4: H.264/H.265/AV1, MKV: +VP9, WebM: VP9/AV1/VP8, MOV: +ProRes), 화질, 해상도(짧은 변 기준, 원본보다 키우지 않음), 프레임 레이트
    - 원본 코덱을 그대로 담을 수 있는 컨테이너면 **재인코딩 없이 저장**(리먹싱) 선택 가능 — 빠르고 화질 손실 없음
  - **동영상 → 이미지**: 재생 위치의 프레임을 원본 해상도로 PNG/JPEG/BMP/TIFF 등에 저장
  - **동영상/오디오 → 오디오**: MP3, M4A(AAC), AAC, Opus, Ogg Vorbis, FLAC, WAV, AIFF, WavPack, WMA, AC3
    - 손실 압축 형식은 비트레이트 선택. 같은 코덱이면 재인코딩 없이 추출 (예: MP4 안의 AAC → M4A)
  - 변환 중 상태바에 진행률과 취소 버튼
- 명령줄 인자로 파일 경로를 넘기면 바로 열기: `ExtConvert image.png`

## FFmpeg

동영상/오디오 기능은 시스템의 `ffmpeg`, `ffprobe`를 실행합니다. 다음 순서로 찾습니다.

1. 환경 변수 `EXTCONVERT_FFMPEG_DIR`
2. 앱 실행 파일 폴더 (또는 그 아래 `ffmpeg/` 폴더) — 배포 시 함께 넣어도 됨
3. `PATH`
4. macOS `/opt/homebrew/bin`, `/usr/local/bin` / Linux `/usr/bin`, `/snap/bin`

설치: Windows `winget install Gyan.FFmpeg` · macOS `brew install ffmpeg` · Linux `sudo apt install ffmpeg`

소리 출력은 OpenAL Soft를 사용합니다 (`Silk.NET.OpenAL.Soft.Native` 패키지에 Windows/macOS/Linux용 네이티브 라이브러리가 포함되어 별도 설치 불필요).

## 빌드 / 실행

[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 필요.

```sh
dotnet run --project src/ExtConvert
dotnet test
```

## CI / 릴리스

- **CI** (`.github/workflows/ci.yml`): 브랜치 푸시와 `main` 대상 PR마다 Windows / Linux / macOS에서 빌드 + 테스트. FFmpeg를 설치해서 동영상/오디오 테스트까지 실행 (러너의 FFmpeg에 없는 인코더 형식은 자동으로 제외)
- **릴리스** (`.github/workflows/release.yml`): `v`로 시작하는 태그를 푸시하면 테스트 후 `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64` 단일 실행 파일을 만들어 GitHub Release에 업로드 (버전은 태그에서)

```sh
git tag v1.0.0
git push origin v1.0.0
```

## 배포용 단일 실행 파일

```sh
dotnet publish src/ExtConvert -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o publish/win-x64
dotnet publish src/ExtConvert -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64
dotnet publish src/ExtConvert -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64
```

## 구조

```
src/ExtConvert/
  MainWindow.axaml(.cs)       드롭 영역, 미리보기, 메뉴
  IcoOptionsWindow.axaml(.cs) ICO 크기 선택 대화상자
  MediaOptionsWindow.axaml(.cs) 동영상/오디오 저장 옵션 (코덱·화질·해상도·프레임 레이트·비트레이트)
  Controls/
    WaveformView.cs           파형 막대 그리기, 재생 위치 표시, 클릭/드래그 탐색
    CropOverlay.cs            이미지 영역 선택 레이어 (이동·크기 조절 손잡이, 비율 고정)
  Conversion/
    IExporter.cs              모든 출력 형식의 공통 인터페이스
    IImageExporter.cs         이미지 변환기 인터페이스 + ExportOptions
    Exporters.cs              변환기 등록 목록 + 열 수 있는 확장자 (새 형식은 여기에 추가)
    IcoExporter.cs / PngExporter.cs / JpegExporter.cs / BmpExporter.cs
    FFmpegImageExporter.cs    ffmpeg 인코더로 저장하는 이미지 형식 (WebP, AVIF, TIFF 등)
    ImageOps.cs               비율 유지 리사이즈 / 배경 합성 / 영역 자르기
  Media/
    FFmpeg.cs                 ffmpeg/ffprobe 탐색, 인코더 목록, 실행, 진행률 파싱, 취소
    MediaInfo.cs              ffprobe JSON 해석
    IMediaExporter.cs         FFmpeg 변환기 인터페이스 + MediaExportOptions
    AudioExporter.cs          오디오 형식 (코덱·비트레이트·스트림 복사 가능 코덱)
    VideoExporter.cs          동영상 컨테이너 (코덱 선택지·화질·크기/프레임 필터·스트림 복사)
    Waveform.cs               오디오 파형 분석 (8kHz 모노로 디코딩해 구간별 최소/최대값)
    Playback/
      MediaPlayer.cs          재생/일시정지/탐색, 오디오 시계 기준 프레임 선택
      VideoStream.cs          ffmpeg rawvideo(BGRA) 프레임 큐
      AudioStream.cs          ffmpeg PCM → OpenAL 스트리밍 버퍼
      VolumeBooster.cs        100% 초과 음량 DSP (증폭 + 리미터, 배율 변경 램프)
tests/ExtConvert.Tests/       Avalonia.Headless 기반 변환 테스트 (미디어 테스트는 ffmpeg가 없으면 건너뜀)
```

새 출력 형식은 `Exporters.Images` / `Exporters.Media`에 `FFmpegImageExporter` / `AudioExporter` / `VideoExporter` 항목을 추가하거나, `IImageExporter` / `IMediaExporter`를 직접 구현해서 추가하면 저장 대화상자에 자동으로 나타납니다.
