# Steam Trailer Tool (`steam trailer tool`) 인수인계 문서 (`AGENTS.md`)

## 1. 프로젝트 개요 & 버전
- **목적:** Steam 상점 페이지에서 재생되는 트레일러를 원본 스트림 그대로(재인코딩 없음) mp4로 저장. GUI(Avalonia) + CLI.
- **대상:** 게임 모드가 아니라 Steam 상점 웹 API 클라이언트이므로 게임 버전 고정(Build ID, DepotDownloader, .acf 차단)과 Mono/Il2Cpp 백엔드는 **해당 없음**.
  대신 고정되어야 하는 것은 **Steam 상점 API 응답 형식**이며, 2026-11-24 기준 실측 형식을 아래 §3에 기록했다.
- **기술 스택:** `.NET 10.0`(`net10.0`, `win-x64`), `Avalonia` 12.0.5, `CommunityToolkit.Mvvm` 8.4.2, xUnit 2.9.3. 앱 버전 `1.0.0`.
- **검증 완료(2026-11-24):**
  - Muse Dash 2(AppID 4839390): CLI·GUI 모두 1080p60 H.264 + AAC 192k, 75.3초 전체 길이 mp4 저장, `ffmpeg -f null` 전체 디코딩 오류 없음.
  - 같은 트레일러 AV1 720p 저장 확인, Muse Dash(774171) 360p를 ffmpeg 없는 환경(PATH 제거)에서 영상/음성 분리 저장 확인.
  - GUI는 Windows UI Automation(ValuePattern/InvokePattern)으로 입력→분석→다운로드를 실제 조작해 확인.

## 2. 파이프라인
1. `SteamStoreClient.ParseAppId` — 숫자 또는 `…/app/<id>` 패턴(상점·커뮤니티·SteamDB·agecheck URL)에서 AppID 추출.
2. `SteamStoreClient.GetTrailersAsync` — `https://store.steampowered.com/api/appdetails?appids=<id>&filters=basic,movies&l=koreana`
   → `data.name`, `data.movies[]`(`id`, `name`, `thumbnail`, `dash_h264`, `dash_av1`, `hls_h264`, `highlight`).
3. `TrailerDownloader.PickManifest` — 선택 코덱의 MPD, 없으면 다른 코덱으로 폴백. 둘 다 없고 구형 `mp4.max`가 있으면 직접 다운로드.
4. `DashManifestParser.Parse` — 첫 Period의 video/audio Representation을 파싱, 세그먼트 URL을 전부 절대 URL로 펼침(이미지 썸네일 타일은 버림).
5. `DashManifest.SelectVideo(maxHeight)` / `SelectAudio()` — 최대 높이 이하 최고 해상도 + 최고 비트레이트 음성.
6. `TrailerDownloader.DownloadTrackAsync` — init + 세그먼트를 **슬라이딩 윈도우(영상 6, 음성 3) 병렬 요청, 순서대로 기록**해 트랙별 fMP4(`*.video.part`, `*.audio.part`) 생성. 세그먼트당 최대 4회 재시도(5xx/408/429/네트워크 오류, 지수 백오프).
7. `FfmpegMuxer.MuxAsync` — `ffmpeg -i video -i audio -map 0:v:0 -map 1:a:0 -c copy -movflags +faststart -f mp4 out.part` → `File.Move`로 최종 파일 교체.

## 3. 설계 의도 & 우회 사유 (Rationale)
- **HTML 스크래핑 대신 appdetails API:** 현재 상점 페이지는 React로 렌더링되어 영상 URL이 초기 HTML에 없다. API는 출시 전 게임(Muse Dash 2, "출시 예정")도 movies를 돌려준다.
- **HLS가 아닌 DASH 사용:** 같은 CDN 경로에 `hls_264_master.m3u8`도 있지만 H.264만 제공하고, HLS 마스터는 FRAME-RATE=30으로 표기된다(DASH는 60/1, 실제 파일도 60fps). DASH는 H.264/AV1 둘 다 있고 SegmentTemplate 구조가 단순해 ffmpeg 없이도 직접 받을 수 있다. `HlsH264Url`은 모델에만 보관하고 사용하지 않는다.
- **ffmpeg의 DASH demuxer에 맡기지 않고 직접 다운로드:** ffmpeg 없는 환경에서도 동작하게 하고, 진행률·취소·재시도·병렬 다운로드를 직접 제어하기 위함. ffmpeg는 최종 `-c copy` 병합에만 선택적으로 사용.
- **init + 세그먼트 단순 이어붙이기:** Steam 세그먼트는 fMP4(moof/mdat)라 이어 붙이면 그 자체로 재생 가능한 fragmented MP4가 된다. 그래서 ffmpeg가 없을 때 `.video.mp4`/`.audio.m4a`를 그대로 남겨도 재생된다.
- **세그먼트 개수 = ceil(전체 길이 × timescale / duration − 1e-6):** 75.3s/3s → 26개. 정확히 나눠떨어지는 경우 부동소수 오차로 1개 더 붙지 않게 epsilon을 둔다. 그래도 마지막 세그먼트가 실제로 없을 수 있어 **마지막 세그먼트만 404를 허용**한다.
- **MPD URL의 `?t=` 쿼리:** 캐시 버스터라 세그먼트 상대 경로 해석 시 버려져도 된다(`new Uri(base, relative)`가 쿼리를 버림). 실측으로 세그먼트 200 응답 확인.
- **`.part` 원자적 저장:** 실패·취소 시 `.part`를 지워 깨진 mp4가 남지 않게 한다(형제 레포 `soundcloud tool`과 같은 규칙).
- **Core / GUI / CLI 분리:** Avalonia는 WinExe라 콘솔 출력·대기가 불편하므로 CLI를 별도 콘솔 프로젝트(`steam-trailer.exe`)로 분리했다. 로직은 전부 Core에 있다.
- **기본 저장 폴더:** GUI·CLI 모두 `%USERPROFILE%\Downloads\SteamTrailer_Downloads`. 형제 도구(`soundcloud tool` → `Downloads\SoundCloud_Downloads`, `youtube tool` → `Downloads\YouTube_Downloads`)와 같은 규칙이므로 바꾸지 말 것 (초기 버전에서 `Videos\Steam Trailers`로 했다가 사용자 요청으로 수정).
- **파일명:** `게임명 - 트레일러명 [높이p 코덱].mp4`. 같은 앱 안에서 트레일러명이 겹치면(대소문자 무시) `(트레일러ID)`를 붙인다.

## 4. 핵심 파일
- `SteamTrailerTool.Core/Services/SteamStoreClient.cs` — AppID 파싱, appdetails 파싱, 공유 `HttpClient`(`SharedHttp`)
- `SteamTrailerTool.Core/Services/DashManifestParser.cs` — MPD 파서 ($RepresentationID$/$Number%0Nd$/$Time$/$Bandwidth$, SegmentTimeline, BaseURL 체인)
- `SteamTrailerTool.Core/Services/TrailerDownloader.cs` — 트랙 다운로드, 진행률(비트레이트 가중), 병합/폴백
- `SteamTrailerTool.Core/Services/FfmpegMuxer.cs` — ffmpeg 탐색 및 `-c copy` 병합
- `SteamTrailerTool.Core/Services/FileNameHelper.cs` — Windows 파일명 정리, 중복 트레일러명 처리
- `steam trailer tool/ViewModels/MainWindowViewModel.cs` — 분석/다운로드/취소/전체선택/폴더 열기
- `steam trailer tool/ViewModels/TrailerItemViewModel.cs` — 항목별 체크·썸네일(Bitmap Dispose)·진행률
- `SteamTrailerTool.Cli/Program.cs` — CLI 인자 처리
- `SteamTrailerTool.Tests/` — 파서·URL·JSON·파일명 테스트(34개)

## 5. 시도했다가 버린 것 / 주의 (Dead Ends)
- **appdetails의 `mp4`/`webm` 필드:** 2025년 이전 형식. 2026-11 현재 구작(Muse Dash 774171)까지 전부 `dash_*`/`hls_h264`로 바뀌었고 `mp4`/`webm`은 응답에 없다. 혹시 남은 앱을 위해 `LegacyMp4Url` 경로만 유지.
- **`filters=movies` 단독:** 게임 이름이 오지 않는다. 반드시 `filters=basic,movies`.
- **PowerShell 5.1로 UI Automation 스크립트 실행 시:** 스크립트에 한글/이모지 버튼 이름이 있으면 UTF-8 **BOM**이 없을 때 깨져서 요소를 못 찾는다. 앱 첫 실행은 창 생성까지 4초 이상 걸릴 수 있어 `MainWindowHandle`이 0으로 나온다(대기 후 재시도).
- **Fluent 기본 강조색(보라):** 체크박스가 보라색으로 떠서 `App.axaml`에서 `ColorPaletteResources Accent="#1A9FFF"`(Steam 파랑)로 덮었다.
- **appdetails 요청 제한:** 짧은 시간에 많이 부르면 429. 클라이언트는 메시지만 띄우고 자동 재시도하지 않는다.

## 6. 빌드 · 배포
```powershell
dotnet build "steam trailer tool.slnx"
dotnet test SteamTrailerTool.Tests
# 단일 EXE (publish\steam trailer tool.exe, publish\steam-trailer.exe)
dotnet publish "steam trailer tool/steam trailer tool.csproj" -c Release -o publish
dotnet publish SteamTrailerTool.Cli -c Release -o publish
```
- 배포 시 ffmpeg를 함께 넣고 싶으면 `ffmpeg.exe`를 exe 옆에 두면 자동 인식된다.
- `publish/`와 (구버전 CLI 출력 위치였던) `downloads/`는 `.gitignore` 처리됨.
