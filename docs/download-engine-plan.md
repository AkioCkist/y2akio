# Download engine kiểu IDM

Trạng thái: **đã triển khai (cả 5 giai đoạn).** Tài liệu này mô tả các kỹ thuật tải giống Internet Download Manager có trong Y2Akio: chia file thành nhiều đoạn tải song song, tự tải tiếp khi bị ngắt, tự thử lại, tạm dừng/tiếp tục, hàng đợi.

## Kết quả đo (2026-11-20, video YouTube "Big Buck Bunny 60fps 4K")

| Trường hợp | Engine đa kết nối | yt-dlp thường |
|---|---|---|
| 1080p, 129.5 MB (gồm ~3s analyze) | 12.3 s | 23.3 s |
| Best (4K), 689 MB, trong app | 31.5 s (~35 MB/s, 6 kết nối) | — |
| Đóng app khi đang tải 207 MB rồi mở lại | Tải tiếp ngay từ ~233 MB, file cuối giống hệt bản tải liền mạch | — |

Kiểm thử tự động: `y2akio.Tests` (12 test, server HTTP cục bộ có tiêm lỗi): đa kết nối, server không hỗ trợ Range, không có Content-Length, rớt kết nối, kết nối treo, link hết hạn được làm mới, link hết hạn không làm mới được, pause/resume không tải lại phần đã có, file thay đổi khi resume, giới hạn tốc độ, lỗi 5xx, ghi đè file cũ.

```powershell
dotnet test .\y2akio.Tests
```

## Khác so với kế hoạch ban đầu

- **Không tự merge bằng ffmpeg.** Engine tải từng format vào đúng tên file yt-dlp sẽ dùng (`<tên>.f<id>.<ext>`, lấy từ `yt-dlp -j` trên info JSON), rồi yt-dlp thấy file "already downloaded" và tự merge + hậu xử lý. Ít code hơn, và tên file luôn khớp với yt-dlp.
- **HLS/DASH (giai đoạn 4)** dùng bộ tải mảnh của yt-dlp: số mảnh song song bằng số kết nối, `--fragment-retries 20`, `--retry-sleep exp=1:30`, `--socket-timeout 20`, và app tự chạy lại yt-dlp tối đa 3 lần khi lỗi mạng (yt-dlp tải tiếp mảnh dở nhờ `--continue`). Chưa có bộ phân tích m3u8/mpd riêng.
- **Thư mục tự xếp** theo *trang web* (`…\YouTube`, `…\TikTok`), vì app đã tách sẵn thư mục video/âm thanh.
- Engine được viết thành mã .NET thuần trong `Services/Downloads/` để project test biên dịch thẳng mã nguồn, không phụ thuộc WinUI.

IDM là phần mềm đóng nguồn nên không có mã để sao chép. Cái làm IDM nhanh là một kỹ thuật đã được mô tả công khai, gọi là *dynamic file segmentation*. Engine dưới đây là một bản triển khai riêng của kỹ thuật đó.

## 1. Hiện trạng

- yt-dlp tự tải. Định dạng HTTP thường (phần lớn video YouTube) tải qua **1 kết nối**, mỗi lần 10 MB. Định dạng HLS/DASH đã tải 4 mảnh cùng lúc (`--concurrent-fragments 4`).
- Khi tải lỗi, file `.part` còn lại và lần tải sau tiếp tục từ đó (`--continue` mặc định). Nhưng app không tự thử lại, không có nút tạm dừng hay hủy, và đóng app là mất trạng thái.
- Đã kiểm chứng: nếu file kết quả đã có sẵn, `yt-dlp --load-info-json` báo *"has already been downloaded"* nhưng **vẫn chạy hậu xử lý** (metadata, nhúng thumbnail). Nhờ vậy engine riêng có thể lo phần tải, còn yt-dlp lo phần hậu xử lý.

## 2. Kiến trúc

```
Analyze (yt-dlp -J) ──► info JSON (đã có: VideoInfo.InfoJson)
        │
        ▼
FormatResolver: yt-dlp --load-info-json -f "<spec>" -J --simulate
        │   → requested_formats[]: url, http_headers, protocol, ext, filesize
        ▼
┌──────────── protocol = http/https ──────────────┐   protocol = m3u8/dash
│ SegmentedDownloader (engine IDM, .NET thuần)    │   → FragmentDownloader (giai đoạn 4)
│  video.fNNN.mp4 + audio.fNNN.webm               │     hoặc yt-dlp -N 8 (dự phòng)
└──────────────────────┬──────────────────────────┘
                       ▼
     ffmpeg -c copy merge → đúng tên file yt-dlp sẽ dùng (--print filename)
                       ▼
     yt-dlp --load-info-json … (file đã có → chỉ chạy hậu xử lý:
     metadata, thumbnail, phụ đề, recode GPU)
```

Engine lỗi ở bất kỳ bước nào thì quay về `YtDlpService.DownloadAsync` như hiện nay. Người dùng không bao giờ bị kẹt.

File mới (dự kiến):

| File | Vai trò |
|---|---|
| `Services/Downloads/SegmentedDownloader.cs` | Engine chia đoạn: probe, lập lịch kết nối, chia đoạn động, ghi theo offset |
| `Services/Downloads/DownloadSegment.cs` | Một đoạn: `Start`, `End` (co lại được), `Downloaded` |
| `Services/Downloads/DownloadState.cs` | File trạng thái `*.y2a.json` để tải tiếp sau khi tắt app hoặc rớt mạng |
| `Services/Downloads/RateLimiter.cs` | Token bucket dùng chung để giới hạn tốc độ |
| `Services/Downloads/DownloadQueueService.cs` | Hàng đợi, số job chạy song song, pause/resume/cancel, lưu hàng đợi khi đóng app |
| `Services/Downloads/FormatResolver.cs` | Gọi yt-dlp để lấy URL, header, kích thước của format đã chọn |
| `Views/DownloadsPage.xaml` | Trình quản lý tải: danh sách job, thanh đoạn kiểu IDM, tốc độ, ETA |

## 3. Thuật toán chia đoạn động

### 3.1 Probe
1. `GET` với `Range: bytes=0-` (không dùng `HEAD`, vì nhiều CDN trả `HEAD` sai). Đọc `Content-Range` hoặc `Content-Length`, `Accept-Ranges`, `ETag`, `Last-Modified`.
2. Server trả `206` kèm tổng kích thước thì cho phép chia đoạn. Trả `200` thì tải bằng 1 kết nối, vẫn tải tiếp được nếu lần sau server hỗ trợ Range.
3. **Dùng luôn kết nối probe làm worker #1** cho đoạn `[0, size)`. Không tốn thêm request nào.

### 3.2 Chia đoạn động (điểm cốt lõi của IDM)
- Tạo trước file `.y2apart` với `SetLength(size)` (kiểm tra dung lượng trống trước). Mỗi worker ghi thẳng vào offset của mình bằng `RandomAccess.WriteAsync`, không cần nối file ở cuối.
- Có slot kết nối trống (tối đa `N`, mặc định 8) thì **tìm đoạn đang chạy còn lại nhiều byte nhất**. Nếu phần còn lại ≥ `2 × MinSegment` (mặc định 1 MB) thì **cắt đôi phần còn lại**: đoạn cũ thu `End` về điểm giữa (cập nhật nguyên tử), worker mới nhận nửa sau.
- Worker xong đoạn của mình thì **không nghỉ** mà lặp lại bước cắt đôi trên đoạn lớn nhất còn lại. Vì vậy cuối quá trình tải không có kết nối nào ngồi chờ một kết nối chậm. Đây là lý do IDM giữ được tốc độ cao đến tận 100%.
- Worker đọc tới `End` hiện tại của đoạn (đọc lại sau mỗi buffer) thì dừng. Byte thừa do request đã gửi trước khi `End` co lại thì bỏ đi.
- Mỗi request giới hạn khoảng 10 MB (`Range: a-b` theo cửa sổ). Lý do: YouTube/googlevideo bóp tốc độ request dài, giống `http_chunk_size` của yt-dlp.
- Số kết nối mặc định theo host: googlevideo 4–6 (nhiều hơn dễ bị 403), CDN khác 8, tối đa 16.

### 3.3 Thử lại và làm mới URL
- Lỗi mạng, timeout, `5xx`: thử lại đoạn đó với backoff 1, 2, 4, 8… tối đa 30 giây, tối đa 10 lần. Đoạn khác vẫn chạy.
- Timeout khi không nhận byte nào (stall) trong 20 giây: hủy request, thử lại.
- `403` hoặc `410` (URL ký số hết hạn): tạm dừng mọi worker, chạy lại `FormatResolver` để lấy URL mới. Nếu cùng kích thước thì tiếp tục từ các offset đã lưu, không tải lại byte nào. Khác kích thước thì bắt đầu lại.
- `416` hoặc kích thước thay đổi giữa chừng: bắt đầu lại từ 0.

### 3.4 Tải tiếp (resume)
- `DownloadState` lưu URL nguồn, header, kích thước, `ETag`, `Last-Modified` và danh sách đoạn `[start, end, downloaded]`. Ghi khoảng 1 giây một lần (debounce) và khi tạm dừng, ghi qua file tạm rồi đổi tên để tránh hỏng file.
- Mở lại app: hàng đợi nạp các job dở. Khi tải tiếp, so `ETag`/`Last-Modified`/kích thước; khác thì tải lại từ đầu.
- Mạng rớt (`NetworkChange.NetworkAvailabilityChanged`): tự tạm dừng, có mạng lại thì tự tiếp tục.

### 3.5 Hoàn tất
- Kiểm tra tổng byte bằng kích thước, flush, đổi tên `.y2apart` thành tên đích, xóa `.y2a.json`.
- Có `filesize` từ yt-dlp thì đối chiếu. Video thì có thể chạy `ffprobe` nhanh để bắt file hỏng.

### 3.6 Đo tốc độ
- Đếm byte toàn cục bằng `Interlocked`. Tốc độ làm mượt bằng EMA (α ≈ 0.3) mỗi 500 ms. ETA = byte còn lại ÷ tốc độ EMA.
- Giao diện vẽ thanh đoạn giống IDM: mỗi đoạn một màu, cho thấy kết nối nào đang chạy.

## 4. Tính năng kiểu IDM

| Tính năng | Giai đoạn |
|---|---|
| Chia đoạn động, nhiều kết nối | 1 |
| Tự thử lại, làm mới URL hết hạn | 1 |
| Tạm dừng / tiếp tục / hủy | 2 |
| Tải tiếp sau khi tắt app, rớt mạng | 2 |
| Trình quản lý tải (danh sách, thanh đoạn, tốc độ, ETA) | 2 |
| Hàng đợi, số job song song | 3 |
| Giới hạn tốc độ (toàn cục và từng job) | 3 |
| Tải HLS/DASH song song, tải tiếp theo mảnh | 4 |
| Theo dõi clipboard: copy link là tự hiện nút tải | 5 |
| Hẹn giờ tải, tắt máy hoặc ngủ khi xong | 5 |
| Tự xếp file vào thư mục theo loại | 5 |
| Gallery (TikTok/FB/IG/X) tải qua engine | 5 |
| Tích hợp trình duyệt (extension + native messaging) | Sau cùng, tùy chọn |

## 5. Các giai đoạn

1. **Engine lõi.** `SegmentedDownloader` cùng bộ test với một server `HttpListener` cục bộ hỗ trợ Range và có thể tiêm lỗi: rớt kết nối, 403 giữa chừng, server chậm, không hỗ trợ Range. Xong khi tải 500 MB đúng checksum dưới mọi kịch bản lỗi.
2. **Tích hợp YouTube và định dạng HTTP.** `FormatResolver`, merge bằng ffmpeg, giao hậu xử lý cho yt-dlp, dự phòng về yt-dlp khi lỗi. Thêm cài đặt "Engine tải: Y2Akio (đa kết nối) / yt-dlp". Xong khi tải giống hệt bản dùng yt-dlp: metadata, thumbnail, phụ đề, recode.
3. **Trình quản lý tải và resume.** `DownloadsPage`, pause/resume/cancel, lưu trạng thái, hàng đợi, giới hạn tốc độ.
4. **HLS/DASH.** Tải mảnh song song, tải tiếp theo mảnh. Trước mắt dùng `yt-dlp -N 8` vì đã có `--continue`.
5. **Tiện ích.** Clipboard, hẹn giờ, tự tắt máy, thư mục theo loại, gallery qua engine.

## 6. Rủi ro

- **Lợi ích phụ thuộc server.** Đa kết nối chỉ nhanh hơn khi server giới hạn tốc độ *mỗi kết nối* (googlevideo, nhiều CDN). Nếu đường truyền đã dùng hết với 1 kết nối thì không nhanh hơn. Đo trước và sau ở giai đoạn 2.
- **YouTube chặn khi mở quá nhiều kết nối** (403). Giới hạn 4–6 kết nối cho googlevideo, gặp 403 lặp lại thì giảm dần.
- **URL hết hạn** (khoảng 6 giờ với YouTube): có bước làm mới URL ở mục 3.3.
- **CDN cần cookies hoặc header riêng**: dùng `http_headers` trong `requested_formats`. Facebook vốn cần `--impersonate`, nên định dạng của Facebook giữ dùng yt-dlp nếu .NET bị chặn.
- **FAT32** không cho file trên 4 GB: báo lỗi sớm khi tạo trước file.
- **Antivirus quét file lớn khi đổi tên**: chỉ đổi tên một lần lúc hoàn tất.
