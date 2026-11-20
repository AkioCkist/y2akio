# Hướng dẫn chuyển các tối ưu sang bản điện thoại

Tài liệu này ghi lại **mọi thay đổi về tốc độ và tải xuống** đã làm cho bản PC (commit `e9f78d3`), viết theo cách không phụ thuộc nền tảng để áp dụng cho bản điện thoại. Mỗi mục có: vấn đề, cách làm, tham số cụ thể, chỗ tham chiếu trong code PC và ghi chú riêng cho mobile.

Bản PC dùng C#/.NET + `yt-dlp`. Nếu bản điện thoại dùng `yt-dlp` qua thư viện nhúng (ví dụ `youtubedl-android`, Chaquopy/Python, hoặc gọi một server), các lệnh và tham số `yt-dlp` bên dưới giữ nguyên. Phần engine tải chỉ cần HTTP client + ghi file theo offset, viết được bằng Kotlin/Swift/Dart.

## Mục lục

1. [Số đo trên PC](#1-số-đo-trên-pc)
2. [Tăng tốc phân tích link (analyze)](#2-tăng-tốc-phân-tích-link-analyze)
3. [Tăng tốc bắt đầu tải](#3-tăng-tốc-bắt-đầu-tải)
4. [Engine tải đa kết nối kiểu IDM](#4-engine-tải-đa-kết-nối-kiểu-idm)
5. [Ghép engine với yt-dlp](#5-ghép-engine-với-yt-dlp)
6. [HLS/DASH và tự thử lại](#6-hlsdash-và-tự-thử-lại)
7. [Trình quản lý tải (hàng đợi)](#7-trình-quản-lý-tải-hàng-đợi)
8. [Các tiện ích khác](#8-các-tiện-ích-khác)
9. [Thứ tự nên làm trên điện thoại](#9-thứ-tự-nên-làm-trên-điện-thoại)
10. [Bảng tra file code PC](#10-bảng-tra-file-code-pc)

---

## 1. Số đo trên PC

| Việc | Trước | Sau |
|---|---|---|
| Tải 1080p 129.5 MB (YouTube) | 23.3 s | 12.3 s |
| Tải 4K 689 MB | ~4 MB/s (yt-dlp 1 kết nối) | ~35 MB/s (6 kết nối) |
| Bắt đầu tải sau khi analyze (YouTube) | 2–4 s (trích xuất lại) | 0.6 s (`--load-info-json`) |
| Analyze video TikTok/Facebook/Dailymotion | 2 lần trích xuất | 1 lần |
| Đóng app khi đang tải, mở lại | Tải lại từ đầu | Tải tiếp từ chỗ dừng |

Lợi ích của đa kết nối phụ thuộc vào server: nhanh hơn khi server bóp tốc độ *mỗi kết nối* (googlevideo của YouTube, nhiều CDN). Trên mạng di động yếu, lợi ích nhỏ hơn, nhưng resume và tự thử lại thì có ích nhất trên điện thoại.

---

## 2. Tăng tốc phân tích link (analyze)

### 2.1 Video lẻ không phải YouTube: dùng lại kết quả probe

**Vấn đề:** link lạ được probe bằng `--flat-playlist` để biết có phải playlist không, rồi chạy thêm `--dump-single-json` đầy đủ, tức trích xuất hai lần.

**Cách làm:** `--flat-playlist` chỉ ngăn yt-dlp mở từng mục *trong playlist*. Với video lẻ, kết quả probe đã có đủ `formats`. Quy tắc:

```
json = yt-dlp --dump-single-json --flat-playlist --playlist-items 1-500 <url>
if json.entries là mảng và có > 1 phần tử  → playlist
else if json không có "entries" và json.formats là mảng không rỗng → dùng luôn json làm thông tin video
else → chạy dump đầy đủ: yt-dlp --dump-single-json --no-playlist <url>
```

Đã kiểm chứng TikTok và Dailymotion có `formats` trong probe. Vimeo thì không, nên rơi về dump đầy đủ (vẫn đúng).

**Code PC:** `YtDlpService.AnalyzeAsync` trong `Services/YtDlp/YtDlpService.Analysis.cs`.

### 2.2 Link YouTube chắc chắn là 1 video: bỏ probe

Đã có từ trước. Các dạng sau đi thẳng tới một lần dump: `youtu.be/<id>`, `watch?v=<id>` (không có `list=`), `/shorts/<id>`, `/embed/<id>`, `/live/<id>`, `/v/<id>`. Song song, gọi oEmbed `https://www.youtube.com/oembed?url=<url>&format=json` (~0.2s) để hiện tiêu đề và thumbnail trước. Xem `LooksLikeSingleVideoUrl`.

### 2.3 Không dùng cookie cho YouTube

yt-dlp không bao giờ gửi cookie cho YouTube (gửi thì bị chặn bot). Vì vậy với link YouTube, **đừng giải mã và ghi file cookie**: tiết kiệm thời gian và không để lộ cookie dạng rõ ra đĩa.

```
needsCookies(url) = host KHÔNG thuộc youtube.com / youtu.be / youtube-nocookie.com
```

**Code PC:** `YtDlpService.NeedsCookies`.

### 2.4 Post Facebook/X: chạy song song

Với link post Facebook (`/share/p/`, `/posts/`, `/permalink.php`, `/photo`...) hoặc X (`/status/<id>`), app phải tải trang trước mới biết đó là post ảnh (gallery) hay video (đưa sang yt-dlp). Trước đây hai bước chạy nối tiếp.

**Cách làm:** chạy yt-dlp analyze **cùng lúc** với bước tải trang gallery. Nếu là post ảnh thì hủy yt-dlp (kill process). Nếu là video thì kết quả yt-dlp đã sẵn hoặc sắp xong.

**Code PC:** `HomePage.AnalyzeAsync` + `AbandonSpeculative` (`Views/HomePage.xaml.cs`).

### 2.5 TikTok bị tường lửa chặn: nhớ trạng thái

TikTok trả trang thử thách bot cho HTTP client thường. Sau **một lần** bị chặn, trong **30 phút** tiếp theo gọi thẳng đường impersonate (`yt-dlp --impersonate chrome --write-pages`), bỏ qua request thường chắc chắn sẽ bị chặn.

**Code PC:** `TikTokPhotoService` (biến `_skipPlainHttpUntil`).

### 2.6 Khởi động nóng công cụ

Lần analyze đầu tiên sau khi mở app chậm vì file cache còn nguội và antivirus phải quét. Ngay khi app mở, chạy ngầm `yt-dlp --version` (cùng `deno --version` và `ffmpeg -version`).

**Mobile:** nếu dùng thư viện yt-dlp nhúng (Python), gọi khởi tạo thư viện ngay lúc mở app trên luồng nền (ví dụ `YoutubeDL.init()` của youtubedl-android, hoặc import module Python trước). Lần khởi tạo Python trên Android thường mất 1–3 giây, đáng làm trước.

---

## 3. Tăng tốc bắt đầu tải

### 3.1 Dùng lại JSON của bước analyze (quan trọng nhất)

**Vấn đề:** bấm Tải thì yt-dlp nhận lại URL và trích xuất từ đầu: tải trang, gọi API player, giải JS challenge. Mất 2–4 giây, đúng việc vừa làm lúc analyze.

**Cách làm:**
1. Lúc analyze, **giữ nguyên chuỗi JSON thô** mà `--dump-single-json` trả về, cùng thời điểm phân tích.
2. Lúc tải, nếu JSON còn mới (**dưới 3 giờ**, vì link ký số của YouTube hết hạn sau khoảng 6 giờ), ghi nó ra file tạm `*.info.json`, rồi gọi:
   ```
   yt-dlp --load-info-json <file> [các tham số tải như cũ]
   ```
   thay cho `yt-dlp <url>`.
3. Nếu lần chạy đó lỗi (403, link hết hạn...), chạy lại bằng URL như bình thường.
4. Xóa file tạm sau khi xong.

Đo được: 0.6s so với 2.1s trước khi byte đầu tiên về.

**Code PC:** `VideoInfo.InfoJson` / `AnalyzedAtUtc`, `DownloadRequest.InfoJson`, `YtDlpService.DownloadAsync`.

### 3.2 Tải nhiều mảnh HLS/DASH cùng lúc

Thêm `--concurrent-fragments N` (N = số kết nối, PC mặc định 8, tối đa 16). Có ích cho Facebook, X, TikTok, livestream (định dạng chia mảnh).

### 3.3 Recode bằng GPU

Khi người dùng ép codec (H.264/HEVC/AV1), PC thử lần lượt `*_nvenc`, `*_qsv`, `*_amf` bằng một lần encode thử:
```
ffmpeg -f lavfi -i color=c=black:s=320x240:r=30:d=0.5 -frames:v 10 -c:v <encoder> -f null -
```
Lệnh trả mã 0 thì dùng encoder đó, nếu không thì dùng CPU. Kết quả được nhớ suốt phiên.

**Mobile:** dùng encoder phần cứng của máy: Android `MediaCodec` (ffmpeg-kit có `h264_mediacodec` / `hevc_mediacodec`), iOS `h264_videotoolbox` / `hevc_videotoolbox`. Vẫn nên encode thử một lần như trên rồi nhớ kết quả. Recode bằng CPU trên điện thoại rất chậm và nóng máy, nên ưu tiên phần cứng hoặc khuyến nghị để "Auto" (không recode).

---

## 4. Engine tải đa kết nối kiểu IDM

Đây là phần lớn nhất. Engine chỉ cần: HTTP client có header `Range`, ghi file theo offset (random access), một file JSON trạng thái. **Code PC:** `Services/Downloads/SegmentedDownloader.cs` (~600 dòng, không phụ thuộc UI).

### 4.1 Tham số mặc định

| Tham số | Giá trị PC | Ghi chú cho mobile |
|---|---|---|
| Số kết nối tối đa / file | 8 (YouTube 6) | Mạng di động: 4 là hợp lý; Wi-Fi: 6–8 |
| Ngưỡng cắt đoạn (MinSplitSize) | 1 MB | Đoạn chỉ được cắt khi còn ≥ 2 × ngưỡng |
| Cửa sổ mỗi request | 10 MB | Request quá dài bị googlevideo bóp tốc độ |
| Timeout treo (không nhận byte) | 20 s | |
| Số lần thử lại liên tiếp | 10 | Đếm lại từ 0 khi nhận được byte |
| Backoff | 1, 2, 4, 8… tối đa 30 s | |
| Số lần làm mới URL hết hạn | 3 | |
| Buffer đọc | 64 KB | |
| Báo tiến độ | 500 ms | |
| Lưu trạng thái | 1 s | |

### 4.2 Thuật toán

**Bước 1: probe, và dùng luôn kết nối probe.**
```
resp = GET url, Range: bytes=0-(WINDOW-1), Accept-Encoding: identity
if resp.status == 206 và Content-Range = "bytes 0-x/TOTAL":
    hỗ trợ Range; tổng kích thước = TOTAL
else if resp.status == 200:
    không hỗ trợ Range → tải 1 kết nối, không resume được
lưu ETag, Last-Modified
tạo file .y2apart, cấp sẵn kích thước TOTAL (file thưa nếu được)
segments = [ {start:0, end:TOTAL, pos:0} ]
kết nối #1 = resp (đọc tiếp response này, không tốn request thừa)
```

**Bước 2: chia đoạn động (điểm cốt lõi của IDM).**
```
mỗi khi số kết nối đang chạy < MAX:
    nếu có đoạn chưa xong mà không ai tải (sót lại từ lần resume) → giao đoạn đó
    ngược lại:
        L = đoạn ĐANG CHẠY có (end - pos) lớn nhất
        nếu (L.end - L.pos) < 2 × MIN_SPLIT → thôi, không cắt nữa
        mid = L.pos + (L.end - L.pos) / 2
        đoạn mới = {start: mid, end: L.end, pos: mid}
        L.end = mid          ← cập nhật dưới khóa
        mở kết nối mới cho đoạn mới
```
Kết nối nào xong đoạn của mình thì **không nghỉ** mà quay lại bước trên, cắt đôi đoạn lớn nhất còn lại. Nhờ vậy tới cuối không có kết nối nào ngồi chờ một kết nối chậm.

**Bước 3: vòng lặp của mỗi kết nối.**
```
loop:
    (pos, end) = đọc dưới khóa
    nếu pos >= end → xong
    resp = GET Range: bytes=pos-(min(end, pos+WINDOW)-1)
    kiểm tra: status 206, Content-Range bắt đầu đúng = pos, tổng không đổi
    đọc từng buffer (timeout treo 20 s mỗi lần đọc):
        dưới khóa: n = min(đã đọc, seg.end - seg.pos); writePos = seg.pos
        nếu n <= 0 → đóng response, quay lại loop
        (giới hạn tốc độ nếu có)
        ghi buffer[0..n] vào file tại offset writePos
        dưới khóa: seg.pos += n
        nếu n < đã đọc, hoặc seg.pos >= seg.end → đoạn bị cắt ngắn / xong → đóng response
```
**Vì sao ghi ngoài khóa vẫn an toàn:** chỗ cắt luôn cách `pos` ít nhất `MIN_SPLIT` (1 MB), lớn hơn hẳn một buffer (64 KB). Vì vậy một lần ghi đang dở không bao giờ vượt qua `end` mới. Phải giữ điều kiện `MIN_SPLIT ≥ 2 × BUFFER`.

**Bước 4: xử lý lỗi.**

| Tình huống | Xử lý |
|---|---|
| Lỗi mạng, timeout, 5xx, 429, kết nối treo | Thử lại **đoạn đó** với backoff; các đoạn khác vẫn chạy |
| 403 / 410 / 401 | Link ký số hết hạn: dừng mọi kết nối, lưu trạng thái, xin URL mới (trích xuất lại bằng yt-dlp, lấy format cùng `format_id`), tiếp tục từ các offset đã lưu |
| 416, tổng kích thước đổi, server thôi trả 206 | File trên server đã đổi: xóa file tạm, tải lại từ đầu (tối đa 2 lần) |
| Đĩa đầy | Lỗi luôn, không thử lại |

**Bước 5: hoàn tất.** Kiểm tra tổng byte đã ghi bằng TOTAL, đóng file, đổi tên `.y2apart` thành tên thật, xóa `.y2a.json`.

### 4.3 File trạng thái để resume (`<tên>.y2a.json`)

```json
{
  "Version": 1,
  "Url": "https://...",
  "TotalSize": 712445280,
  "RangeSupported": true,
  "ETag": "\"abc\"",
  "LastModified": "Tue, 30 Dec 2025 15:08:20 GMT",
  "Segments": [ { "Start": 0, "End": 118740880, "Downloaded": 34603008 }, ... ]
}
```
- Ghi khoảng mỗi giây và khi dừng. Ghi qua file `.tmp` rồi đổi tên để không bao giờ hỏng file nửa chừng.
- **Khi resume:** chỉ dùng trạng thái nếu file `.y2apart` có đúng `TotalSize` byte. Gửi request cho đoạn chưa xong đầu tiên, rồi so tổng kích thước và ETag/Last-Modified (nếu cả hai bên đều có). Khớp thì dùng luôn response đó làm kết nối #1; không khớp thì tải lại từ đầu.
- **Không** so URL, vì URL ký số đổi sau mỗi lần trích xuất. googlevideo không có ETag nhưng có `Last-Modified`.

### 4.4 Chi tiết HTTP

- Gửi `Accept-Encoding: identity` và **tắt tự giải nén** của HTTP client: byte range phải trỏ đúng vào byte lưu trên server.
- Gửi kèm `http_headers` mà yt-dlp trả cho từng format (User-Agent, Accept, Accept-Language, Sec-Fetch-Mode, Referer...).
- Không đặt timeout tổng cho request (file lớn tải lâu). Chỉ dùng timeout kết nối (15 s) và timeout treo mỗi lần đọc (20 s).
- Cho phép nhiều kết nối song song tới cùng host (OkHttp: `Dispatcher.maxRequestsPerHost`, mặc định chỉ 5).

### 4.5 Giới hạn tốc độ (token bucket)

```
mỗi lần ghi n byte:
    tokens = min(capacity, tokens + elapsed × limit)    // capacity = max(limit/4, 64KB)
    tokens -= n
    nếu tokens < 0 → ngủ (-tokens / limit) giây (tối đa 5 s)
```
Dùng **một limiter chung** cho toàn app và **một limiter riêng** cho mỗi job. Kết nối nào cũng chờ cả hai. `limit = 0` nghĩa là không giới hạn. **Code PC:** `Services/Downloads/RateLimiter.cs`.

### 4.6 File thưa (sparse)

Trên Windows/NTFS, ghi ở offset xa trong file đã cấp sẵn sẽ bắt hệ điều hành điền số 0 cho khoảng trống phía trước (chậm với file lớn), nên PC đánh dấu file là sparse.

**Mobile:** ext4/f2fs (Android) và APFS (iOS) vốn hỗ trợ file thưa. Chỉ cần `RandomAccessFile.setLength(total)` (Android) hoặc `FileHandle.truncate(atOffset:)` (iOS), không cần gì thêm. Nhớ kiểm tra dung lượng trống trước khi cấp file.

### 4.7 Bộ test cần có

PC có 12 test với một server HTTP cục bộ cố ý gây lỗi (`y2akio.Tests/`). Nên viết lại cùng các kịch bản này cho mobile (ví dụ dùng MockWebServer của OkHttp):

1. Nhiều kết nối, byte đúng (so SHA-256).
2. Server không hỗ trợ Range → 1 kết nối.
3. Không có Content-Length.
4. Kết nối bị cắt giữa chừng, nhiều lần.
5. Kết nối treo → hết timeout thì thử lại.
6. 403 → làm mới URL → tải tiếp.
7. 403 mà không làm mới được → báo lỗi, giữ file dở.
8. Tạm dừng rồi tiếp tục → lần sau tải < 75% file.
9. File trên server đổi khi resume → tải lại, ra nội dung mới.
10. Giới hạn tốc độ được tôn trọng.
11. Lỗi 5xx được thử lại.
12. Ghi đè file cũ.

---

## 5. Ghép engine với yt-dlp

**Ý tưởng:** engine chỉ lo phần tải byte. Ghép video + audio, ghi metadata, nhúng thumbnail, phụ đề, chuyển đổi vẫn để yt-dlp làm, vì yt-dlp **bỏ qua việc tải những file đã có sẵn** nhưng vẫn chạy hậu xử lý (đã kiểm chứng).

### 5.1 Các bước

```
1. Có info JSON (từ analyze; hoặc chưa có / đã cũ thì chạy dump mới) → ghi ra info.json tạm.
2. Hỏi yt-dlp sẽ tải gì, lưu tên gì (không tốn mạng, ~0.6 s):
     yt-dlp -j --no-warnings --load-info-json info.json <cùng tham số -f / -o / --merge-output-format / -x như lúc tải>
   Đọc từ JSON trả về:
     filename                    → tên file cuối cùng
     requested_formats[]         → (nếu ghép video+audio) mỗi phần tử có format_id, url, protocol, ext, http_headers, filesize
     hoặc chính object gốc       → (nếu chỉ 1 format) url, protocol, ext, http_headers
3. Tên file từng format (đúng quy tắc của yt-dlp):
     có requested_formats: <filename bỏ phần mở rộng cuối>.f<format_id>.<ext của format>
     chỉ 1 format:         đúng filename
4. Với mỗi format đủ điều kiện → engine tải về đúng tên đó.
   Đủ điều kiện = protocol là http/https, KHÔNG có "fragments", KHÔNG có trường "cookies", có url.
5. Chạy yt-dlp --load-info-json info.json <tham số tải như cũ>.
   yt-dlp in "has already been downloaded" cho các file engine đã tải, rồi tự merge và hậu xử lý.
   Format nào engine không làm thì yt-dlp tự tải.
6. Thành công: xóa file trạng thái engine và file .fNNN còn sót (nếu yt-dlp không dùng).
   Engine lỗi bất kỳ lúc nào: ghi log, để yt-dlp tự tải (fallback).
```

**Ví dụ thật:**
```
filename = ".../Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film.mp4"
requested_formats = [ {format_id:"399", ext:"mp4", protocol:"https"}, {format_id:"251", ext:"webm", protocol:"https"} ]
→ engine tải ".../Big Buck Bunny ... .f399.mp4" và ".../Big Buck Bunny ... .f251.webm"
→ yt-dlp: "has already been downloaded" ×2 → [Merger] → [Metadata] → [EmbedThumbnail]
```

### 5.2 Những điểm dễ sai

- **Tiền tố ngày giờ trong tên file** phải lấy theo **lúc người dùng bấm tải** (lưu trong job), không phải "bây giờ". Nếu không, tạm dừng rồi tiếp tục sẽ ra tên khác và không tìm lại được file dở.
- File format đã có sẵn, không có file trạng thái và đúng `filesize` (nếu biết) thì coi là xong, bỏ qua. Không tin `filesize_approx`.
- File cuối đã tồn tại → bỏ qua engine; yt-dlp chỉ chạy lại hậu xử lý.
- Làm mới URL khi gặp 403: chạy lại dump, **ghi đè info.json** (để yt-dlp ở bước 5 dùng link mới), chạy lại bước 2, lấy format có cùng `format_id`.
- Chạy engine trên luồng nền, **không** để vòng lặp tải chạy trên luồng UI (bản PC từng suýt mắc lỗi này).

**Code PC:** `Services/YtDlp/YtDlpService.Engine.cs` (`PrefetchWithEngineAsync`, `ResolveDownloadAsync`, `RefreshFormatSourceAsync`, `CleanupEngineFiles`).

---

## 6. HLS/DASH và tự thử lại

Định dạng chia mảnh (m3u8/mpd) vẫn để yt-dlp tải, kèm các tham số sau:

```
--concurrent-fragments <số kết nối>
--retries 10
--fragment-retries 20
--retry-sleep http:exp=1:30
--retry-sleep fragment:exp=1:30
--socket-timeout 20
--limit-rate <KB>K            (nếu có giới hạn tốc độ)
```

Ngoài ra app tự **chạy lại yt-dlp tối đa 3 lần** (chờ 2, 4, 8 s) nếu stderr có dấu hiệu lỗi mạng. yt-dlp tải tiếp file `.part` và mảnh dở nhờ `--continue` (mặc định). Dấu hiệu lỗi mạng:

```
timed out, Connection reset, Connection aborted, Connection refused, Remote end closed,
IncompleteRead, HTTP Error 500/502/503/504, getaddrinfo failed,
Temporary failure in name resolution, No route to host, Network is unreachable,
giving up after, Unable to download video data
```

**Code PC:** `BuildDownloadArgs`, `IsTransientNetworkError`.

---

## 7. Trình quản lý tải (hàng đợi)

### 7.1 Mô hình job

```
Job {
  id, request (url, loại, chất lượng, thư mục, info JSON, thời điểm yêu cầu...),
  title, site, thumbnailUrl, qualityLabel, createdAt, completedAt,
  status: Queued | Downloading | Paused | WaitingForNetwork | Completed | Failed | Canceled,
  percent, statusText, error, savedPath, speedLimitKBps,
  partialFiles[]     ← các file engine đã bắt đầu, để Hủy thì xóa được
}
```
Lưu toàn bộ danh sách vào `downloads.json` mỗi khi trạng thái đổi (không phải mỗi nhịp tiến độ), ghi qua file tạm rồi đổi tên. Job đã xong thì xóa `InfoJson` cho file nhẹ. Giữ tối đa 200 mục đã xong.

### 7.2 Quy tắc chạy

- **Bơm hàng đợi:** số job đang chạy < giới hạn song song (PC mặc định 3) thì chạy job Queued cũ nhất.
- **Tạm dừng:** hủy token, đánh dấu lý do = Pause. Engine lưu trạng thái, process yt-dlp bị kill.
- **Hủy:** lý do = Cancel → xóa `partialFiles` (+ `.y2apart`, `.y2a.json`, `.part`).
- **Mất mạng:** mọi job đang chạy dừng với lý do Network → WaitingForNetwork. Có mạng lại thì → Queued.
- **Lỗi giống lỗi mạng:** tự thử lại tối đa 3 lần, mỗi lần chờ 30 s (hiện "Lỗi mạng — thử lại sau 30s (1/3)").
- **Đóng app:** dừng với lý do Shutdown, lưu trạng thái "Downloading". Mở lại thì job đó thành Queued (nếu bật "tự tải tiếp khi mở app") hoặc Paused.
- **Hẹn giờ:** giữ các job Queued cho tới giờ hẹn. Giờ đã qua trong hôm nay thì hiểu là ngày mai.
- **Khi xong tất cả:** Không làm gì / Ngủ / Tắt máy, chỉ kích hoạt nếu có ít nhất một job hoàn tất sau khi chọn.
- **Cookie dùng chung:** đếm số job đang dùng file cookie. Chỉ xóa file khi lượt cuối cùng xong (trước đây job này xóa mất file của job khác).

### 7.3 Chữ hiển thị (đừng hiện dòng thô của yt-dlp)

| Dòng yt-dlp | Hiển thị |
|---|---|
| `[download] 45.3% of ~12.34MiB at 2.50MiB/s ETA 00:05 (frag 3/120)` | `45.3% · 12.34 MiB · 2.50 MiB/s · ETA 00:05 · 3/120` |
| `[download] 100% of 9.73MiB` (không có tốc độ) | `Đang xử lý...` |
| `[Merger]` | `Đang ghép video và âm thanh...` |
| `[Metadata]` | `Đang ghi metadata...` |
| `[ThumbnailsConvertor]` / `[EmbedThumbnail]` | `Đang nhúng ảnh thu nhỏ...` |
| `[ExtractAudio]` | `Đang chuyển đổi âm thanh...` |
| `[VideoConvertor]` / `[VideoRemuxer]` | `Đang chuyển mã video...` |
| `[EmbedSubtitle]` / `[SubtitlesConvertor]` | `Đang thêm phụ đề...` |
| `[Fixup...]` | `Đang sửa container...` |

Engine tải xong mà yt-dlp chưa bắt đầu thì hiện ngay "Đang xử lý..." để chữ không đứng yên. Tiến độ của engine: `284.6 MB / 689.2 MB · 31.5 MB/s · 6 kết nối · ETA 0:15`, và bỏ phần "kết nối" khi bằng 0.

Mục đã xong: ẩn thanh tiến độ, hiện `tên file · kích thước`. Đường dẫn đầy đủ để ở chỗ chạm giữ hoặc tooltip.

**Code PC:** `Services/DownloadQueueService.cs`, `Models/DownloadJob.cs`, `Views/DownloadsPage.xaml`, `Helpers/SegmentBar.cs` (thanh đoạn: mỗi đoạn vẽ phần đã tải, đoạn đang chạy dùng màu nhấn kèm vạch tại vị trí ghi).

---

## 8. Các tiện ích khác

| Tính năng | PC | Gợi ý mobile |
|---|---|---|
| Theo dõi clipboard | Khi copy link http(s) mới, Trang chủ hỏi có phân tích luôn không. Kiểm tra khi clipboard đổi và khi cửa sổ được kích hoạt | Android 10+ / iOS chỉ đọc clipboard khi app ở foreground, nên kiểm tra ở `onResume` / `sceneDidBecomeActive`. Tốt hơn nữa: nhận link qua **Share sheet** (intent `ACTION_SEND` / Share Extension) |
| Thư mục con theo trang | `…\YouTube`, `…\TikTok`, `…\Facebook`, `…\Instagram`, `…\X`, ... | Dùng tên album/thư mục con trong MediaStore (Android) hoặc album Photos (iOS) |
| Video trong gallery tải đa kết nối | Video của post FB/IG/X/TikTok đi qua engine (UA + Referer), lỗi thì dùng cách cũ | Như PC |
| Lịch sử dùng chung | `HistoryService` toàn app, job xong ghi vào lịch sử dù đang ở trang nào | Như PC |
| Ngủ/Tắt máy khi xong | `SetSuspendState` / `shutdown /s /t 60` | **Bỏ qua.** Thay bằng thông báo "Đã tải xong" |

---

## 9. Thứ tự nên làm trên điện thoại

Xếp theo lợi ích trên công sức:

1. **Dùng lại info JSON khi tải** (mục 3.1): sửa ít, nhanh hơn 1.5–3.5 s mỗi lần tải.
2. **Dùng lại kết quả probe cho video lẻ** (2.1) + **không dùng cookie cho YouTube** (2.3).
3. **Tham số tự thử lại và mảnh song song cho yt-dlp** (mục 6): chỉ thêm tham số, rất đáng trên mạng di động chập chờn.
4. **Khởi tạo nóng thư viện yt-dlp** (2.6).
5. **Engine đa kết nối + resume** (mục 4–5): công sức lớn nhất, nhưng là thứ làm bản PC nhanh gấp đôi và tải tiếp được sau khi bị ngắt.
6. **Hàng đợi chạy nền** (mục 7) theo cơ chế nền của từng hệ điều hành:
   - **Android:** foreground service (loại `dataSync`) có thông báo tiến độ, hoặc WorkManager cho job dài. Theo dõi mạng bằng `ConnectivityManager.NetworkCallback`. Tùy chọn "chỉ tải qua Wi-Fi" là điều người dùng mobile mong đợi.
   - **iOS:** app không chạy nền lâu được; dùng `URLSession` background configuration cho phần tải HTTP (hệ điều hành tự tải tiếp kể cả khi app bị tắt). Phần ghép/hậu xử lý chạy khi app quay lại. Engine nhiều kết nối tự viết chỉ chạy khi app đang mở; khi app xuống nền, chuyển sang một background task / 1 kết nối.
7. **Chữ trạng thái dễ đọc và thanh đoạn** (7.3).
8. **Tiện ích** (mục 8).

**Lưu ý riêng cho mobile:**
- Mặc định 4 kết nối khi dùng mạng di động, 6–8 khi dùng Wi-Fi. YouTube luôn tối đa 6.
- Pin: engine nhiều kết nối không làm tốn CPU hơn đáng kể (phần lớn là chờ mạng), nhưng nên giữ wake lock / foreground service trong lúc tải.
- Bộ nhớ: dùng buffer 64 KB cho mỗi kết nối, **không** đọc cả file vào RAM (bản PC hiện vẫn đọc video gallery vào RAM để nén ZIP; trên điện thoại nên ghi thẳng ra file).

---

## 10. Bảng tra file code PC

| Chủ đề | File |
|---|---|
| Engine chia đoạn, resume, retry | `y2akio/Services/Downloads/SegmentedDownloader.cs` |
| File trạng thái `.y2a.json` | `y2akio/Services/Downloads/DownloadState.cs` |
| Giới hạn tốc độ | `y2akio/Services/Downloads/RateLimiter.cs` |
| File thưa (chỉ Windows) | `y2akio/Services/Downloads/SparseFile.cs` |
| Ghép engine với yt-dlp | `y2akio/Services/YtDlp/YtDlpService.Engine.cs` |
| Tham số tải, thử lại, info JSON | `y2akio/Services/YtDlp/YtDlpService.Download.cs` |
| Analyze, probe, cookie, YouTube | `y2akio/Services/YtDlp/YtDlpService.Analysis.cs` |
| Khởi động nóng | `y2akio/Services/YtDlp/YtDlpService.Process.cs` |
| Recode GPU | `y2akio/Services/YtDlp/YtDlpService.Encoders.cs` |
| Hàng đợi | `y2akio/Services/DownloadQueueService.cs` |
| Model job + chữ hiển thị | `y2akio/Models/DownloadJob.cs` |
| Trang Downloads, thanh đoạn | `y2akio/Views/DownloadsPage.xaml(.cs)`, `y2akio/Helpers/SegmentBar.cs` |
| Phân tích song song FB/X, cookie lease | `y2akio/Views/HomePage.xaml.cs`, `y2akio/Services/AppSettingsService.cs` |
| TikTok WAF memo | `y2akio/Services/TikTokPhotoService.cs` |
| Clipboard | `y2akio/Services/ClipboardWatcher.cs` |
| Test engine | `y2akio.Tests/` |
| Tài liệu thiết kế + số đo | `docs/download-engine-plan.md`, `docs/analyze-optimization.md` |
