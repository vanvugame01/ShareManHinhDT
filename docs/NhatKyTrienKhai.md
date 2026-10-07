# Nhật ký triển khai ShareManHinhDT

## 2026-10-07 — chuẩn bị bản 1.1.2

- Vấn đề: ghép đôi thành công nhưng chia sẻ tự dừng; lỗi gốc bị ghi đè thành thông báo dừng. Thông báo bảo mật Android không đủ để xác định nguyên nhân.
- Thay đổi: bảo toàn nguyên nhân đầu tiên; chi tiết/sao chép lỗi Android; ràng buộc quyền và dịch vụ với đúng phiên; encoder H.264/Surface theo khả năng codec; dọn dẹp tiếp tục khi một tài nguyên lỗi; PC giữ và gửi lỗi về điện thoại.
- Kiểm thử bổ sung đã tái hiện lỗi PC với video dọc 574 × 1280: kích thước đầu ra giải mã có phần đệm nhưng mã cũ yêu cầu khớp tuyệt đối. Sửa xử lý aperture, kích thước bộ đệm và stride, cắt về vùng hình hợp lệ; vẫn từ chối vùng hình khác cấu hình phiên.
- Kiểm tra biên dịch Android đầu tiên: mã thoát 1, bảy lỗi kiểu dữ liệu/nullability của binding .NET Android. Đã sửa; lần kiểm tra tiếp theo có mã thoát 0, không cảnh báo/lỗi. Log: [android-build](../artifacts/logs/20261007-115026-1.1.2/android-build.log).
- Kiểm thử trước sửa phần đệm: mã thoát 1 do `Kích thước H.264 không khớp cấu hình phiên`. Log: [test-development](../artifacts/logs/20261007-115026-1.1.2/test-development.log).
- Biên dịch sửa aperture đầu tiên: mã thoát 1 do chữ ký COM `GetBlob` khác kiểu truyền dự kiến. Đã sửa. Log: [windows-padding-fix](../artifacts/logs/20261007-115026-1.1.2/windows-padding-fix.log).
- Kiểm thử Windows sau sửa aperture: mã thoát 0. Log: [windows-padding-fix-2](../artifacts/logs/20261007-115026-1.1.2/windows-padding-fix-2.log).
- Các lần kiểm tra trên là giai đoạn phát triển. Kết quả đóng gói/xác minh cuối cùng được ghi riêng bên dưới. Chưa xác minh lỗi tự dừng trên điện thoại thật.

## 2026-10-07 11:56:19 +07:00 — bản 1.1.2

Múi giờ Asia/Saigon. Bắt đầu kiểm thử và tạo gói phát hành; nhật ký thô: `artifacts/logs/20261007-115026-1.1.2/`.

Chưa hoàn tất; có bước thất bại. Sửa lỗi và chạy lại, giữ nguyên nhật ký lần này. Kết thúc: 2026-10-07 11:56:58 +07:00.

| Lệnh | Mã thoát | Log |
|---|---|---|
| `scripts/Test.ps1` | 0 | [test](../artifacts/logs/20261007-115026-1.1.2/test.log) |
| `scripts/Publish.ps1` | 0 | [publish](../artifacts/logs/20261007-115026-1.1.2/publish.log) |
| `scripts/Verify-Release.ps1` | 1 | [verify](../artifacts/logs/20261007-115026-1.1.2/verify.log) |

Lỗi triển khai: Bước verify thất bại, mã thoát 1. Xem D:\MyProject\ShareManHinhDT\artifacts\logs\20261007-115026-1.1.2\verify.log

Chưa nghiệm thu điện thoại thật Android 16, truyền hình 60 giây, FPS/độ trễ và PC nhiều màn hình/DPI. Kết quả tự động không thay thế nghiệm thu thiết bị thật.

## 2026-10-07 11:58:09 +07:00 — bản 1.1.2

Múi giờ Asia/Saigon. Bắt đầu kiểm thử và tạo gói phát hành; nhật ký thô: `artifacts/logs/20261007-115809-1.1.2/`.

Hoàn tất kiểm thử tự động, đóng gói và xác minh phát hành. Kết thúc: 2026-10-07 11:58:43 +07:00.

| Lệnh | Mã thoát | Log |
|---|---|---|
| `scripts/Test.ps1` | 0 | [test](../artifacts/logs/20261007-115809-1.1.2/test.log) |
| `scripts/Publish.ps1` | 0 | [publish](../artifacts/logs/20261007-115809-1.1.2/publish.log) |
| `scripts/Verify-Release.ps1` | 0 | [verify](../artifacts/logs/20261007-115809-1.1.2/verify.log) |

| Gói | Số byte | SHA-256 |
|---|---|---|
| [ShareManHinhDT-Android.apk](../artifacts/release/ShareManHinhDT-Android.apk) | 8169901 | `DD96F70A06209CD3F7C42C1E99BC1780B8022441717C2E307D84D256B71FED1E` |
| [ShareManHinhDT-Windows-x64.zip](../artifacts/release/ShareManHinhDT-Windows-x64.zip) | 63405138 | `DF641A4CBDAFBE32DC4609A3DEC85C19C33EAC0F5B7ED9E2C971D5330AC86162` |

APK được kiểm tra cùng vân tay khóa ký nội bộ hiện có; Windows kèm runtime khởi động thành công. Không phân phối khóa hoặc mật khẩu ký.

Chưa nghiệm thu điện thoại thật Android 16, truyền hình 60 giây, FPS/độ trễ và PC nhiều màn hình/DPI. Kết quả tự động không thay thế nghiệm thu thiết bị thật.

### Ghi chú xử lý lần xác minh đầu tiên

Lần triển khai 11:56:19 +07:00 đạt test/publish nhưng verify có mã thoát 1. Gói ZIP không thiếu assembly: Compress-Archive trên Windows dùng dấu phân cách ngược trong tên entry, trong khi bước xác minh mới tìm đường dẫn bằng dấu phân cách xuôi. Đã chuẩn hóa tên entry trước khi so sánh. Lần triển khai tiếp theo dùng thư mục log mới và cả ba bước có mã thoát 0; log cũ được giữ nguyên.

Không có thiết bị trong danh sách `adb devices -l` khi kết thúc. Không kết luận lỗi tự dừng đã được nghiệm thu trên điện thoại Android 16; cần cài cập nhật cả Windows và Android 1.1.2, thử chia sẻ 60 giây và dùng Chi tiết lỗi/Sao chép lỗi nếu còn thất bại.
## 2026-10-07 — chuẩn bị bản 1.1.3

- Vấn đề người dùng xác nhận bằng log: Android 16/API 36, Xiaomi 25080RABDG; foreground và MediaProjection đi qua, GetDisplaySize ném System.InvalidCastException: Arg_InvalidCastException tại bước Kích thước màn hình. Người dùng cũng xác nhận ảnh camera QR bị xoay 90° trong khi giao diện vẫn đứng đúng.
- Thay đổi: helper AndroidServices.Require dùng JavaCast qua JNI cho các dịch vụ, kiểm tra null/display/bounds/kích thước và bảo toàn lỗi trong callback đổi cấu hình. Ghi log dịch vụ, wrapper C# và lớp Java; không ghi mã hoặc dữ liệu cấp quyền.
- Preview: TextureView đã xử lý hướng cảm biến. Ma trận mới chỉ bù hướng màn hình, sửa tỷ lệ, căn giữa/cắt phần dư; không xoay thêm theo cảm biến. DisplayListener cập nhật cả góc 180°, đăng ký/gỡ theo resumed/pause/destroy; giữ dữ liệu YUV và cổng một lần kết nối.
- Biên dịch kiểm tra đầu tiên có mã thoát 1 vì nullability của Activity.OnConfigurationChanged; đã sửa khai báo tham số theo binding. Kết quả cuối của Release/trimming ghi tại bước publish bên dưới.
- Kiểm thử phát triển: mã thoát 0, 18 nhóm đạt; ma trận thử sensor 0/90/180/270 và display 0/90/180/270, buffer 4:3/16:9, view dọc/ngang/vuông. QR giải mã được ảnh xoay 90/180/270.
- Chưa nghiệm thu JNI, ảnh preview và truyền hình 60 giây trên thiết bị thật; chưa có thiết bị kết nối. Kết quả phát hành chính thức được ghi riêng bên dưới.
## 2026-10-07 13:51:42 +07:00 — bản 1.1.3

Múi giờ Asia/Saigon. Bắt đầu kiểm thử và tạo gói phát hành; nhật ký thô: `artifacts/logs/20261007-135142-1.1.3/`.

Hoàn tất kiểm thử tự động, đóng gói và xác minh phát hành. Kết thúc: 2026-10-07 13:52:53 +07:00.

| Lệnh | Mã thoát | Log |
|---|---|---|
| `scripts/Test.ps1` | 0 | [test](../artifacts/logs/20261007-135142-1.1.3/test.log) |
| `scripts/Publish.ps1` | 0 | [publish](../artifacts/logs/20261007-135142-1.1.3/publish.log) |
| `scripts/Verify-Release.ps1` | 0 | [verify](../artifacts/logs/20261007-135142-1.1.3/verify.log) |

| Gói | Số byte | SHA-256 |
|---|---|---|
| [ShareManHinhDT-Android.apk](../artifacts/release/ShareManHinhDT-Android.apk) | 8182189 | `EF935314F174B797DA5E9EAE774D34129D93A32A0BD1D663E0D2042B3F4D5932` |
| [ShareManHinhDT-Windows-x64.zip](../artifacts/release/ShareManHinhDT-Windows-x64.zip) | 63405144 | `A5C00C68DC96296FD680E78F8D342E72E0F47D9BE470B70233B9CDCFCB978CB7` |

APK được kiểm tra cùng vân tay khóa ký nội bộ hiện có; Windows kèm runtime khởi động thành công. Không phân phối khóa hoặc mật khẩu ký.

Chưa nghiệm thu điện thoại thật Android 16, truyền hình 60 giây, FPS/độ trễ và PC nhiều màn hình/DPI. Kết quả tự động không thay thế nghiệm thu thiết bị thật.

### Kết quả bổ sung bản 1.1.3

Đã biên dịch và publish Android Release với trimming, không có lỗi/cảnh báo C# trong bước cuối. Cả 18 nhóm kiểm thử và kiểm thử Windows đều đạt; APK được xác minh versionName 1.1.3, versionCode 5, chữ ký v3 và cùng vân tay khóa nội bộ. Việc kiểm tra mã nguồn xác nhận các dịch vụ đều đi qua AndroidServices.Require; không còn ép trực tiếp GetSystemService sang IWindowManager hoặc các manager khác.

Danh sách adb vẫn không có thiết bị. Chuyển kiểu JNI, ảnh preview thực tế, listener khi xoay/chuyển nền và truyền hình 60 giây trên Xiaomi Android 16 chưa nghiệm thu; kiểm thử ma trận C# không thay thế các kiểm tra này.
## 2026-10-07 15:14:58 +07:00 — bản 1.1.3

Múi giờ Asia/Saigon. Bắt đầu kiểm thử và tạo gói phát hành; nhật ký thô: `artifacts/logs/20261007-151458-1.1.3/`.

Hoàn tất kiểm thử tự động, đóng gói và xác minh phát hành. Kết thúc: 2026-10-07 15:16:03 +07:00.

| Lệnh | Mã thoát | Log |
|---|---|---|
| `scripts/Test.ps1` | 0 | [test](../artifacts/logs/20261007-151458-1.1.3/test.log) |
| `scripts/Publish.ps1` | 0 | [publish](../artifacts/logs/20261007-151458-1.1.3/publish.log) |
| `scripts/Verify-Release.ps1` | 0 | [verify](../artifacts/logs/20261007-151458-1.1.3/verify.log) |

| Gói | Số byte | SHA-256 |
|---|---|---|
| [ShareManHinhDT-Android.apk](../artifacts/release/ShareManHinhDT-Android.apk) | 8182189 | `331E6567A289F5282FEF7A46553291F73230B5B1F16FCC14F50C25ED8F5A80E7` |
| [ShareManHinhDT-Windows-x64.zip](../artifacts/release/ShareManHinhDT-Windows-x64.zip) | 63432472 | `677C83AB74C575B0C42511FE7DF7D8AB47266E97E94D8F0BA9998B7867E3E6EF` |

APK được kiểm tra cùng vân tay khóa ký nội bộ hiện có; Windows kèm runtime khởi động thành công. Không phân phối khóa hoặc mật khẩu ký.

Chưa nghiệm thu điện thoại thật Android 16, truyền hình 60 giây, FPS/độ trễ và PC nhiều màn hình/DPI. Kết quả tự động không thay thế nghiệm thu thiết bị thật.

## 2026-10-07 15:23:33 +07:00 — bản 1.1.3

Múi giờ Asia/Saigon. Bắt đầu kiểm thử và tạo gói phát hành; nhật ký thô: `artifacts/logs/20261007-152333-1.1.3/`.

Hoàn tất kiểm thử tự động, đóng gói và xác minh phát hành. Kết thúc: 2026-10-07 15:24:32 +07:00.

| Lệnh | Mã thoát | Log |
|---|---|---|
| `scripts/Test.ps1` | 0 | [test](../artifacts/logs/20261007-152333-1.1.3/test.log) |
| `scripts/Publish.ps1` | 0 | [publish](../artifacts/logs/20261007-152333-1.1.3/publish.log) |
| `scripts/Verify-Release.ps1` | 0 | [verify](../artifacts/logs/20261007-152333-1.1.3/verify.log) |

| Gói | Số byte | SHA-256 |
|---|---|---|
| [ShareManHinhDT-Android.apk](../artifacts/release/ShareManHinhDT-Android.apk) | 8182189 | `EB74A3115BF14D903426F220F9E009BD9DA825A558F657F30F9762EE36DC2F5C` |
| [ShareManHinhDT-Windows-x64.zip](../artifacts/release/ShareManHinhDT-Windows-x64.zip) | 68659172 | `F5B05E2490E26F734635356346414D1032B7E52152D266ABA5C3E9B358CF5D70` |

APK được kiểm tra cùng vân tay khóa ký nội bộ hiện có; Windows kèm runtime khởi động thành công. Không phân phối khóa hoặc mật khẩu ký.

Chưa nghiệm thu điện thoại thật Android 16, truyền hình 60 giây, FPS/độ trễ và PC nhiều màn hình/DPI. Kết quả tự động không thay thế nghiệm thu thiết bị thật.
