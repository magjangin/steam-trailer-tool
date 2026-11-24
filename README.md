# Steam Trailer Tool

Steam 상점 페이지에서 재생되는 트레일러를 **원본 화질 그대로(재인코딩 없이)** mp4로 저장하는 도구입니다.
GUI(Avalonia)와 CLI 두 가지로 쓸 수 있습니다.

- 입력: 상점 주소(`https://store.steampowered.com/app/4839390/Muse_Dash_2/`) 또는 AppID(`4839390`)
  - `steamcommunity.com/app/…`, `steamdb.info/app/…` 주소도 인식
- 코덱: H.264(기본, 호환성) / AV1(같은 화질에서 용량 약 절반)
- 화질: 최고(보통 1080p) / 1080 / 720 / 480 / 360 이하
- 결과: `게임명 - 트레일러명 [1080p H264].mp4` (영상+음성 병합, `-c copy`)

## 요구 사항

- Windows x64, .NET 10 SDK(빌드 시)
- **ffmpeg** (선택): 영상·음성 트랙을 한 파일로 합칠 때 사용합니다.
  찾는 순서는 `FFMPEG_PATH` 환경 변수 → 실행 파일 옆 `ffmpeg.exe` → `PATH`.
  없으면 `….video.mp4` 와 `….audio.m4a` 두 파일로 저장합니다(둘 다 단독 재생 가능).

## GUI

```powershell
dotnet run --project "steam trailer tool/steam trailer tool.csproj"
```

주소 입력 → **분석하기** → 받을 트레일러 체크 → 코덱/화질/저장 폴더 선택 → **다운로드**.
기본 저장 폴더는 `%USERPROFILE%\Videos\Steam Trailers` 입니다.

## CLI

```powershell
dotnet run --project SteamTrailerTool.Cli -- https://store.steampowered.com/app/4839390/Muse_Dash_2/
```

```
steam-trailer <상점 URL | AppID> [옵션]
  -o, --out <폴더>        저장 폴더 (기본: .\downloads)
  -c, --codec <h264|av1>  영상 코덱 (기본: h264)
  -q, --max-height <N>    최대 세로 해상도 (기본: 최고 화질)
  -i, --index <목록>      받을 트레일러 번호 (1부터, 쉼표 구분)
  -l, --list              목록·화질만 출력
      --lang <언어>       Steam 언어 코드 (기본: koreana)
```

## 빌드 / 배포

```powershell
dotnet build "steam trailer tool.slnx"
dotnet test SteamTrailerTool.Tests
dotnet publish "steam trailer tool/steam trailer tool.csproj" -c Release -o publish
dotnet publish SteamTrailerTool.Cli -c Release -o publish
```

`publish\steam trailer tool.exe`(GUI)와 `publish\steam-trailer.exe`(CLI)가 단일 실행 파일로 생성됩니다.

## 구조

| 경로 | 내용 |
| --- | --- |
| `SteamTrailerTool.Core` | Steam API 조회, MPD 파서, 세그먼트 다운로더, ffmpeg 병합 |
| `steam trailer tool` | Avalonia GUI |
| `SteamTrailerTool.Cli` | 콘솔 버전 (`steam-trailer.exe`) |
| `SteamTrailerTool.Tests` | xUnit 단위 테스트 |

> 트레일러 저작권은 각 게임의 개발사·배급사에 있습니다. 개인 보관·참고 용도로 사용하세요.
