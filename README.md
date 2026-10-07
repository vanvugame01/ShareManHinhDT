# ShareManHinhDT

Ứng dụng C# giúp xem màn hình Android trên PC Windows 11 qua mạng nội bộ, phục vụ trình chiếu và hướng dẫn thao tác. Gồm ứng dụng Windows WPF và ứng dụng Android native; mỗi PC nhận một điện thoại.

Phiên bản **1.1.3** sửa chuyển kiểu dịch vụ Android khi lấy kích thước màn hình và sửa ma trận ảnh camera quét QR để không xoay thêm theo cảm biến. Giữ xử lý video dọc có phần đệm trên Windows, lựa chọn encoder và lỗi gốc của phiên. Có quét QR và toàn màn hình chỉ hiển thị hình điện thoại. Ứng dụng chỉ truyền hình ảnh, giới hạn khung hình trong 1280 × 720 hoặc 720 × 1280, mục tiêu 30 hình/giây. Kích thước được căn chỉnh theo codec, sai lệch tỷ lệ giới hạn khoảng 3%. Người dùng xác nhận quyền chia sẻ cho từng phiên.

## Tải và chạy bản đã biên dịch

Các tệp phát hành nằm trong `artifacts/release/`:

- `ShareManHinhDT-Windows-x64.zip`: giải nén toàn bộ và chạy `windows/ShareManHinhDT.Windows.exe`. Gói kèm runtime .NET, không cần cài .NET, codec ngoài, scrcpy hoặc ADB.
- `ShareManHinhDT-Android.apk`: APK Release đã ký bằng khóa nội bộ của dự án, hỗ trợ Android 10 trở lên, ARM64 và ARM32. Chuyển tệp sang điện thoại và cho phép ứng dụng quản lý tệp cài APK từ nguồn này nếu Android yêu cầu. Không cần root hoặc bật USB debugging.
- `checksums.json`: tên tệp, dung lượng và SHA-256 của các gói.

APK này dùng để phân phối trực tiếp trong nội bộ, chưa phát hành qua Google Play. Không cài APK Debug trước đó để thay thế bản Release: hai bản có khóa ký khác nhau; nếu đã cài bản Debug, cần gỡ bản đó trước.

## Cách kết nối

1. Kết nối điện thoại và PC trong cùng mạng nội bộ. PC có thể dùng Ethernet; ưu tiên Wi-Fi 5 GHz cho điện thoại. Mạng khách có tính năng cách ly thiết bị có thể không kết nối được.
2. Trên Windows, bấm **Bắt đầu nhận**. Ứng dụng hiển thị địa chỉ IPv4, cổng **48731**, mã ghép đôi và vân tay chứng chỉ. Nếu có nhiều địa chỉ, chọn địa chỉ của adapter đang kết nối cùng mạng với điện thoại.
3. Trên Android, bấm **Quét QR để kết nối**, cấp quyền camera và hướng camera sau vào toàn bộ QR trên PC. Ứng dụng điền IP/mã và kết nối ngay; vân tay trong QR được dùng để kiểm tra chứng chỉ PC tự động.
4. Nếu muốn nhập thủ công hoặc không có quyền camera, nhập IP/mã rồi bấm **Kết nối với PC**. Lần đầu theo cách này, đối chiếu **toàn bộ vân tay SHA-256** trên hai thiết bị. Chứng chỉ chỉ được ghi nhớ theo IP sau khi ghép đôi thành công.
5. Bấm **Bắt đầu chia sẻ màn hình**, trả lời yêu cầu quyền của Android. Android 14 trở lên có thể cho chọn chia sẻ toàn màn hình hoặc một ứng dụng.
6. Chuyển sang nội dung muốn trình chiếu. Trên PC, dùng **Toàn màn hình/F11** để ẩn các nút, khung ghép đôi và trạng thái, chỉ giữ hình điện thoại trên nền đen. Nhấn **Esc hoặc F11** để khôi phục cửa sổ. Hình giữ đúng tỷ lệ; khi mất kết nối, vùng xem giữ nền đen.
7. Dừng từ ứng dụng PC, ứng dụng Android hoặc thông báo chia sẻ của Android. Khóa điện thoại/tắt màn hình sẽ kết thúc phiên. Khi bắt đầu lại, kết nối bằng mã mới và xác nhận quyền chia sẻ lại.

Mã ghép đôi có hiệu lực 5 phút, được thay sau phiên hoặc sau 5 lần nhập sai. Nếu mất nhiều thời gian đối chiếu chứng chỉ, nhập lại mã hiện trên PC. Chứng chỉ thay đổi sẽ làm kết nối bị từ chối; chỉ dùng **Quên PC đã tin cậy** sau khi kiểm tra trực tiếp PC và đối chiếu lại.

QR tự cập nhật khi chọn IP hoặc đổi mã; được ẩn khi dừng nhận, hết hạn hoặc đang có điện thoại kết nối. Chỉ quét QR hiển thị trong ứng dụng PC của bạn. QR đã chụp trước đó có thể hết hạn; quét mã mới đang hiển thị. Khi tạm chuyển màn hình quét sang nền, camera được đóng và mở lại khi quay lại. Ảnh xem trước bù góc quay màn hình, giữ tỷ lệ và căn giữa; cập nhật cả khi xoay 180° mà kích thước giao diện không đổi. Không cần cài ứng dụng quét khác hoặc Google Play Services. Bản APK 1.1.3 dùng cùng khóa ký nội bộ với các bản trước nên có thể cài cập nhật.

Bản vá có kiểm thử tự động cho góc/tỷ lệ ma trận và QR xoay 90°/180°/270°; chuyển kiểu JNI và hướng camera trên thiết bị thật chưa được nghiệm thu. Với Xiaomi 25080RABDG Android 16 đã báo lỗi, cần kiểm tra ảnh chữ/QR đứng đúng khi cầm dọc, xoay máy, mở camera 60 giây, hủy/mở lại và chuyển nền/quay lại. Lỗi camera được ghi với nhãn `ShareManHinhDT.QR`; kiểu dịch vụ ghi với nhãn `ShareManHinhDT.Services` trong log Android.

## Khi không kết nối hoặc không có hình

- Kiểm tra IP, mã đang hiển thị, cùng mạng và tính năng cách ly thiết bị trên router.
- Nếu Windows Firewall yêu cầu, cho phép ứng dụng trên **mạng riêng**. Quản trị viên có thể tạo quy tắc cho tệp EXE, TCP 48731, phạm vi mạng nội bộ. Ứng dụng không tự đổi cấu hình tường lửa hoặc mở cổng router.
- Nếu cổng 48731 đang được sử dụng, đóng phiên ShareManHinhDT khác rồi thử lại.
- Chấp nhận quyền chia sẻ màn hình trên Android. Từ chối quyền thông báo không thay thế quyền chia sẻ; Android vẫn có thể quản lý phiên qua giao diện hệ thống.
- Nếu phiên tự dừng, bấm **Chi tiết lỗi → Sao chép lỗi** trên Android. Lỗi đầu tiên được giữ sau dọn dẹp; PC cũng giữ lỗi phiên gần nhất và gửi lỗi bộ nhận về điện thoại. Không cần bật debugging để sao chép lỗi.
- Thông báo “nội dung ứng dụng bị ẩn khỏi tính năng chia sẻ màn hình vì lý do bảo mật” có thể xuất hiện với nội dung nhạy cảm như ô mật khẩu; thông báo này không đủ để kết luận nguyên nhân phiên dừng. Chuyển sang nội dung không được bảo vệ để kiểm tra và đọc chi tiết lỗi nếu phiên kết thúc.
- Một số ứng dụng bảo vệ nội dung bằng `FLAG_SECURE` hoặc DRM nên vùng hình có thể đen. Ứng dụng không vượt qua giới hạn này.
- Windows 11 N có thể cần **Media Feature Pack của Microsoft** nếu thiếu Media Foundation. Các bản Windows thông thường tận dụng codec hệ thống.

## Nhật ký triển khai

Chạy `scripts/Deploy.ps1` để kiểm thử, đóng gói và xác minh theo thứ tự. Script lưu log riêng cho từng lần chạy dưới `artifacts/logs/`, ghi mã thoát và thêm mục vào [Nhật ký triển khai](docs/NhatKyTrienKhai.md). Nhật ký ghi phiên bản, kết quả, kích thước/SHA-256 của gói và các phần chưa nghiệm thu trên thiết bị thật; giữ nguyên cả lần thất bại. Các gói phát hành không chứa khóa ký hoặc mật khẩu.

## Điểm mạnh và điểm yếu

| Điểm mạnh | Điểm yếu |
|---|---|
| Dùng MediaProjection/MediaCodec Android và Media Foundation Windows. | Cần cài APK Android và xác nhận quyền mỗi phiên. |
| Không phụ thuộc Miracast hoặc danh sách điện thoại của Phone Link. | Cần kiểm thử encoder, xoay màn hình và quản lý dịch vụ trên từng hãng. |
| Không cần debugging, driver USB, FFmpeg, VLC hoặc SDL trên PC. | Tự triển khai giao thức và tích hợp API native phức tạp hơn dùng scrcpy. |
| TLS và ghim chứng chỉ; quét QR để ghép đôi nhanh trong LAN. | Chưa tự tìm thiết bị; QR cần camera và lựa chọn IP đúng mạng. |
| Gói Windows kèm runtime, Android kèm runtime trong APK. | ZIP Windows lớn hơn bản phụ thuộc runtime cài sẵn; cần phát hành lại để cập nhật runtime. |

Âm thanh, điều khiển điện thoại, ghi hình, USB và kết nối qua Internet chưa thuộc phiên bản này. Việc có mã nguồn/biên dịch thành công không đồng nghĩa đã đạt nghiệm thu trên mọi điện thoại. Trạng thái kiểm thử xem trong [kế hoạch phát triển](docs/KeHoachPhatTrien.md).

## Biên dịch và kiểm thử

Chạy PowerShell tại thư mục dự án:

```powershell
.\scripts\Initialize-Tools.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1
.\scripts\Verify-Release.ps1
```

Máy biên dịch cần truy cập mạng để tải .NET SDK 10.0.401, workload Android, Android SDK và JDK. Script đặt các công cụ trong `.tools/`, không yêu cầu cài Android Studio. `Initialize-Tools.ps1` chấp nhận giấy phép Android SDK để chuẩn bị bộ biên dịch. Nếu chỉ phát triển Windows, dùng `-WindowsOnly` với script khởi tạo và phát hành.

Khóa ký APK cùng hai tệp mật khẩu nằm trong `.tools/signing/`, không được đưa vào gói hoặc quản lý mã nguồn. Sao lưu an toàn cả thư mục này để các APK cập nhật giữ nguyên chữ ký. Script không ghi mật khẩu ra console. Chứng chỉ nhận TLS trên PC được lưu trong kho chứng chỉ **CurrentUser/My**, tên `ShareManHinhDT Local Receiver`; không thêm vào Trusted Root.

Mã dùng tên tiếng Anh; chú thích viết tiếng Việt không dấu; giao diện và tài liệu dùng tiếng Việt. Không có migration dữ liệu từ phiên bản trước.

## Tài liệu công nghệ

- [MediaProjection và quyền theo phiên](https://developer.android.com/media/grow/media-projection).
- [MediaCodec Android](https://developer.android.com/reference/android/media/MediaCodec).
- [H.264 decoder của Windows](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-decoder), [bộ chuyển màu Windows](https://learn.microsoft.com/en-us/windows/win32/medfound/colorconverter).
- [CsWin32 của Microsoft](https://github.com/microsoft/CsWin32): sinh khai báo interop khi biên dịch.
- [Đóng gói .NET kèm runtime](https://learn.microsoft.com/en-us/dotnet/core/deploying/).
- [ZXing.Net](https://github.com/micjahn/ZXing.Net): thư viện QR đóng gói trong hai ứng dụng; xem [thông tin giấy phép](THIRD-PARTY-NOTICES.md).
