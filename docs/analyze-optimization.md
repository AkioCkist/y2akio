# Tăng tốc Analyze — ghi chú thay đổi

> Tóm tắt thay đổi "Fast Analyze": giảm thời gian phân tích video YouTube khi paste link
> và khi click track trong playlist. Code đã sửa, **chưa build/verify** (bản build cuối bị hủy
> khi đang chạy — cần `dotnet build` hoặc publish rồi test tay theo mục Verify bên dưới).

## Vấn đề

Paste link + Analyze chậm (~7s cho 1 video). Nguyên nhân: `AnalyzeAsync` chạy **2 lần full extraction**:

1. flat-probe `--flat-playlist --playlist-items 1-500` (~3.1s) — **lãng phí**: với URL video đơn nó vẫn trả về đủ formats, mục đích chỉ là phát hiện playlist.
2. full dump `--no-playlist` (~3.9s) — lần này mới thực sự cần (formats/res).

Click 1 track trong playlist: cũng phải chờ full dump (~4s) mới hiện panel, dù `PlaylistEntry` đã có title/duration ngay trong tay.

## Hướng tiếp cận (đã chốt với user)

- **Cắt flat-probe thừa** cho URL chắc chắn là 1 video → single video 7s → ~4s.
- **Card tức thì**: title + thumbnail + uploader hiện ~0.2s qua **YouTube oEmbed**, song song với **1 full dump duy nhất** (~4s) nạp formats/quality/sub.
- **Giữ nguyên sub load chung** trong dump: đo thực tế sub nằm sẵn cùng JSON với formats (0s thêm). Tách ra để "load khi cần" = thêm 1 fetch ~4s → chậm hơn. Nên **không** lazy-load sub.
- Không đổi `RunDumpJsonAsync` — giữ nguyên android `player_client` retry (video bị bot-block, vd `MaIcObWHAOo` → 360p).

## Đo đạc (trước khi sửa)

| Thao tác | Thời gian | Ghi chú |
|---|---|---|
| flat-probe URL video đơn | ~3.1s | trả về đủ formats — chỉ dùng để phát hiện playlist |
| full dump 1 video | ~3.9s | lần chạy thật sự cần thiết |
| flat-probe playlist thật | ~2.1s | trả `entries[]`, KHÔNG có formats từng video |
| YouTube oEmbed (title/author/thumb) | ~0.2s | endpoint rẻ, không spawn yt-dlp |

## Thay đổi chi tiết

### `Services/YtDlpService.cs`

1. **`public static bool LooksLikeSingleVideoUrl(string url)`** — classifier. `NormalizeUrl` trước (cùng normalize như `RunDumpJsonAsync`). Chỉ trả `true` khi *chắc chắn* 1 video:
   - Không phải host YouTube → `false` (giữ đường probe cũ — không đụng fb normalize/impersonate/cookies).
   - Có query `list=` → `false`.
   - `youtu.be/<id>` (1 segment) | `/watch?v=<id>` | `/shorts|embed|live|v/<id>` → `true`.
   - Channel, `/playlist`, mơ hồ → `false`.

2. **Probe-cut trong `AnalyzeAsync`** — đầu method:
   ```csharp
   if (LooksLikeSingleVideoUrl(url))
       return new AnalyzeResult { IsPlaylist = false, Video = await AnalyzeVideoAsync(url, cookiesPath, ct) };
   ```
   Luồng probe → detect playlist phía dưới giữ nguyên.

3. **`public record YouTubePreview(string Title, string Author, string ThumbnailUrl);`** + `private static readonly HttpClient _http` (thêm `using System.Net.Http`).

4. **`public async Task<YouTubePreview?> FetchYouTubePreviewAsync(url, ct)`** — GET `https://www.youtube.com/oembed?url=<encoded>&format=json`. Parse `title`/`author_name`/`thumbnail_url`. **Không bao giờ throw** — lỗi HTTP/parse/non-YouTube → `null`. Không cookies, không chạy yt-dlp.

### `Views/HomePage.xaml.cs`

**Field mới:**
```csharp
private bool _analyzeInProgress; // chống double-click / Enter+click
private bool _previewActive;     // card tạm đang hiện, dump chưa xong
private int _activeDownloads;    // chống re-enable DownloadNow giữa chừng download
```

**Method mới** (khu "Options panel"):
- `ClearOptionsState()` — reset mọi control options (quality/sub combo, `_currentVideo=null`, disable DownloadNow) để không lộ state của video cũ.
- `ShowInstantCard(title, uploader, durationDisplay, thumbUrl)` — `ClearOptionsState()` rồi vẽ ngay title/thumb/duration, hiện `OptionsPanel`.
- `FinishVideoOptions(VideoInfo)` — `_currentVideo = video;` → **dùng lại `PopulateOptionsPanel(video)`** (overwrite toàn bộ: card + sub + `RefreshQualityOptions()`), bật lại DownloadNow nếu không có download nào đang chạy.
- `HidePreviewOnError()` — analyze lỗi lúc chỉ mới card tạm → collapse panel.
- `TryPaintYouTubePreviewAsync(url)` — fire-and-forget: fetch oEmbed, nếu `_previewActive` còn true thì vẽ card. Không `ConfigureAwait(false)` (cần về UI thread).
- `static string? ResolveEntryThumbnail(PlaylistEntry)` — flat entry thiếu thumb → dựng `https://i.ytimg.com/vi/{id}/hqdefault.jpg`.

**`AnalyzeAsync()` (HomePage):** guard `_analyzeInProgress`. Nhánh single-video:
```csharp
_currentVideo = null;
_previewActive = true;
var dumpTask  = _ytDlp.AnalyzeVideoAsync(url, cookiesPath); // 1 full dump, chạy ngay
_ = TryPaintYouTubePreviewAsync(url);                        // oEmbed vẽ card khi về (~0.2s)
var video = await dumpTask;
FinishVideoOptions(video);
```
Nhánh còn lại (playlist/mơ hồ) giữ nguyên gọi `AnalyzeAsync` service + xử lý playlist. `catch` → `HidePreviewOnError()`. `finally` → `_analyzeInProgress = false`.

**`PlaylistListView_ItemClick`:** guard `_analyzeInProgress`; trước khi await dump:
```csharp
ShowInstantCard(entry.Title, "", entry.DurationDisplay, ResolveEntryThumbnail(entry));
var video = await _ytDlp.AnalyzeVideoAsync(entry.Url, cookiesPath);
FinishVideoOptions(video);
```

**Download counter:** `DownloadNowButton_Click` và `DownloadPlaylistButton_Click` — `_activeDownloads++` đầu, `--` trong `finally`. `DownloadNowButton.IsEnabled` giờ = `_activeDownloads == 0`.

## State machine

| Giai đoạn | _currentVideo | _previewActive | OptionsPanel | Quality/sub | DownloadNow |
|---|---|---|---|---|---|
| Bắt đầu analyze single/track | null | true | ẩn / card sau oEmbed | trống | disabled |
| oEmbed về (URL gõ tay) | null | true | Visible (card) | trống | disabled |
| Full dump xong | set | false | Visible | `PopulateOptionsPanel` fill | enabled nếu `_activeDownloads==0` |
| Lỗi | null | false | Collapsed | trống | enabled nếu `_activeDownloads==0` |

`PopulateOptionsPanel` luôn là lần ghi cuối (overwrite toàn bộ) → idempotent từ state preview. `TryPaint...` check `_previewActive` nên không bao giờ vẽ đè sau full-populate.

## Files thay đổi

- `Services/YtDlpService.cs`
- `Views/HomePage.xaml.cs`
- Không đổi XAML, không key localization mới.
- Plan đầy đủ: `C:\Users\doubl\.claude\plans\cozy-knitting-flurry.md`

## Verify (chưa chạy)

- Build: `dotnet build -c Debug -p:Platform=x64` (bản cuối bị hủy — chưa xác nhận compile pass).
- Video thường `https://www.youtube.com/watch?v=aqz-KE-bpKQ` → card ~0.2s, options sau 1 dump ~4s, KHÔNG probe.
- Video bot-block `https://www.youtube.com/watch?v=MaIcObWHAOo` → dump ok qua android retry (360p), 1 fetch.
- Shorts / `https://youtu.be/<id>` → classifier true, 1 dump.
- Playlist thật → vẫn probe → panel playlist; click track → card ngay + 1 dump; double-click track 2 trong lúc chạy bị chặn.
- Regression: Facebook story + `watch?v=X&list=Y` → đường probe cũ còn nguyên.

## Edge case đáng nhớ

- oEmbed lỗi (private/age-restricted/blocked) → `null` → không vẽ card, panel hiện sau 1 dump (~4s) — vẫn nhanh hơn 7s cũ.
- Dump về nhanh hơn oEmbed → `_previewActive` đã false → no-op.
- Thumb suy ra `i.ytimg.com/vi/{id}/hqdefault.jpg` có thể 404 với video rất mới — cosmetic, dump sẽ thay.
- Card tạm chưa có `uploader` khi click track playlist (flat entry không có) — để trống, dump sẽ điền.
