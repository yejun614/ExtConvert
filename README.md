# Ext Convert

파일 미리보기 + 파일 형식 변환 데스크톱 앱 (C# / .NET 10 / Avalonia 12). Windows, Linux, macOS 지원.

## 기능

- 파일을 창에 **드래그 앤 드롭**하거나, 빈 영역 클릭 / `파일 > 열기` (Ctrl+O)로 선택
- 이미지 미리보기 (PNG, JPG, BMP, GIF, WebP, ICO) — 투명 영역은 체커보드로 표시
- 동영상/오디오 미리보기 (MP4, MOV, MKV, WebM, AVI, MP3, M4A, WAV, FLAC 등, FFmpeg 필요) — 동영상은 썸네일, 상태바에 길이·해상도·코덱 표시
- `파일 > 다른 이름으로 저장` (Ctrl+Shift+S)으로 형식 변환
  - **ICO**: 16~256px 중 원하는 크기를 골라 한 파일에 담음 (PNG 압축 엔트리, Vista 이후 표준). 정사각형이 아니면 비율 유지 + 투명 여백
  - **PNG**
  - **JPEG**: 투명 영역은 흰색으로 합성
  - **MP3** (동영상/오디오 → MP3): 비트레이트 선택, 상태바에 진행률과 취소 버튼
- 명령줄 인자로 파일 경로를 넘기면 바로 열기: `ExtConvert image.png`

## FFmpeg

동영상/오디오 기능은 시스템의 `ffmpeg`, `ffprobe`를 실행합니다. 다음 순서로 찾습니다.

1. 환경 변수 `EXTCONVERT_FFMPEG_DIR`
2. 앱 실행 파일 폴더 (또는 그 아래 `ffmpeg/` 폴더) — 배포 시 함께 넣어도 됨
3. `PATH`
4. macOS `/opt/homebrew/bin`, `/usr/local/bin` / Linux `/usr/bin`, `/snap/bin`

설치: Windows `winget install Gyan.FFmpeg` · macOS `brew install ffmpeg` · Linux `sudo apt install ffmpeg`

## 빌드 / 실행

[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 필요.

```sh
dotnet run --project src/ExtConvert
dotnet test
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
  Mp3OptionsWindow.axaml(.cs) MP3 비트레이트 선택 대화상자
  Conversion/
    IExporter.cs              모든 출력 형식의 공통 인터페이스
    IImageExporter.cs         이미지 변환기 인터페이스 + ExportOptions
    Exporters.cs              변환기 등록 목록 (새 형식은 여기에 추가)
    IcoExporter.cs / PngExporter.cs / JpegExporter.cs
    ImageOps.cs               비율 유지 리사이즈 / 배경 합성
  Media/
    FFmpeg.cs                 ffmpeg/ffprobe 탐색, 실행, 진행률 파싱, 취소
    MediaInfo.cs              ffprobe JSON 해석
    IMediaExporter.cs         FFmpeg 변환기 인터페이스 + MediaExportOptions
    Mp3Exporter.cs
tests/ExtConvert.Tests/       Avalonia.Headless 기반 변환 테스트 (미디어 테스트는 ffmpeg가 없으면 건너뜀)
```

새 출력 형식은 `IImageExporter` 또는 `IMediaExporter`를 구현해서 `Exporters.Images` / `Exporters.Media`에 추가하면 저장 대화상자에 자동으로 나타납니다.
