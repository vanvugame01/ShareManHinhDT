# Kiến trúc và kế hoạch nghiệm thu ShareManHinhDT

## Mục tiêu đã chốt

C# cho cả Windows và Android; chỉ xem màn hình Android trên PC qua LAN; phân phối cá nhân/nội bộ; một điện thoại mỗi PC; chất lượng 720p/30 hình mỗi giây; ưu tiên dễ bảo trì. Mục tiêu độ trễ là ít nhất 95% mẫu không quá 500 ms trong mạng nội bộ ổn định.

Nền tảng: Windows 11 x64, Android 10/API 29 trở lên. Target Android API 36. APK gồm ARM64 và ARM32. Bản hiện tại 1.1.3, Android versionCode 5. .NET SDK được ghim trong `global.json`; CsWin32 ghim trong dự án Windows; ZXing.Net 0.16.11 ghim trong Shared.

## Kiến trúc thực tế

```text
Android Activity: ghép đôi, đối chiếu chứng chỉ, cấp quyền
  → Foreground Service + MediaProjection
  → Surface + MediaCodec H.264 Baseline, 2,5 Mbps, tối đa 30 fps
  → H.264 Annex B + SPS/PPS
  → giao thức v1 qua TCP/TLS
  → Windows IMFTransform H.264, chế độ độ trễ thấp
  → NV12 → Color Converter hệ thống → BGR32
  → bộ đệm ArrayPool → chỉ giữ khung đã giải mã mới nhất
  → WPF WriteableBitmap, giữ tỷ lệ và không cắt hình
```

- `Shared`: thông điệp, đóng khung dữ liệu, JSON sinh mã để dùng được khi linker tối ưu APK, chuyển NAL sang Annex B, tính kích thước và tiện ích ghép đôi.
- `Windows`: kho chứng chỉ người dùng, bộ nhận, pipeline Media Foundation, giao diện WPF. CsWin32 chỉ là dependency khi biên dịch; không có bộ codec bên thứ ba trong gói.
- `Android`: giao diện Activity, TLS client, lưu pin, heartbeat và dịch vụ chia sẻ. Dịch vụ không khởi động lại tự động khi bị hệ thống hủy.

### Giao thức v1

Header 24 byte, số nguyên theo big-endian: magic `SMDT` 4 byte, phiên bản 2 byte, loại 2 byte, độ dài 4 byte, timestamp microsecond 8 byte và cờ 4 byte. Khung H.264 tối đa 4 MiB; thông điệp điều khiển tối đa 128 KiB. Timestamp không âm, giới hạn để chuyển sang đơn vị Media Foundation không tràn số.

Các loại: xác thực bằng mã/tên thiết bị, chấp nhận phiên, cấu hình video, khung video, yêu cầu khung khóa, dừng, ping/pong và lỗi. Cấu hình mang kích thước, tốc độ hình, bitrate và SPS/PPS. Windows kiểm tra trạng thái, giới hạn video và kích thước thực từ decoder; sau mỗi cấu hình chỉ nhận hình từ khung khóa.

TLS 1.2/1.3; kiểm tra pin SHA-256 toàn chứng chỉ trước khi gửi mã. QR cung cấp pin trực tiếp; nhập thủ công khi chưa biết chứng chỉ vẫn dùng kết nối thăm dò bị từ chối và đối chiếu vân tay. Pin đã lưu khác pin QR không được ghi đè tự động; chỉ lưu pin mới sau khi PC chấp nhận mã.

### QR ghép đôi và camera

QR có định dạng `sharemanhinhdt://pair?v=1&ip=...&port=48731&code=...&fp=...&exp=...`; thời hạn là Unix timestamp theo giây. Shared tạo/đọc dữ liệu, kiểm tra các trường, loại dữ liệu sai/thiếu/lặp, QR không thuộc ứng dụng và mã đã hết hạn. Schema QR độc lập với giao thức truyền video v1.

Windows có bộ chọn IPv4, ưu tiên Ethernet/Wi-Fi có gateway; QR gồm viền trắng và được tạo lại khi đổi IP/mã. Sự kiện trạng thái kết nối riêng điều khiển việc ẩn QR, không suy luận từ chuỗi thông báo giao diện.

Android dùng Activity quét riêng không exported, Camera2 và ImageReader YUV420; xử lý crop, row stride/pixel stride của mặt phẳng Y. HandlerThread lấy ảnh và sao chép mặt phẳng Y; mọi ảnh đã lấy đều được Close rồi Dispose trong finally, kể cả ảnh bị bỏ qua. Bộ đệm camera được trả trước khi giao bản sao cho QrDecodeWorker trên ThreadPool. Tối đa một tác vụ giải mã, khoảng cách ít nhất 200 ms, không có hàng đợi; mỗi tác vụ sở hữu bản sao riêng. Khi dừng, kết quả muộn bị bỏ qua; callback UI cũng kiểm tra đúng phiên camera hiện tại. QrScanGate chỉ nhận một kết quả hợp lệ; camera đóng trước khi trả thông tin cho Activity chính và kết nối. Khi chuyển nền, đóng camera; khi trở lại, chờ camera cũ đóng trước khi mở lại. Thiếu camera hoặc từ chối quyền vẫn có đường nhập thủ công. Lỗi camera/giải mã được ghi qua Android.Util.Log với nhãn ShareManHinhDT.QR.

### Toàn màn hình Windows

Trạng thái isFullScreen tách riêng khỏi WindowStyle. Khi bật bằng nút hoặc F11, các vùng tiêu đề, ghép đôi và trạng thái bị Collapsed; vùng xem phủ toàn bố cục, không margin/góc bo, nền đen và Stretch Uniform. MonitorFromWindow/GetMonitorInfo chọn màn hình chứa cửa sổ; SetWindowPos dùng tọa độ vật lý để phủ cả vùng thanh tác vụ. Cửa sổ tạm dùng Topmost và NoResize. Esc/F11 khôi phục style, resize mode, Topmost, vị trí/kích thước và trạng thái trước đó. Mất kết nối chỉ xóa hình, không mở lại các khung ngoài; placeholder bị ẩn trong chế độ này.

ZXing.Net nằm trong cả hai gói, không cần ứng dụng scanner ngoài hoặc Google Play Services. Giấy phép Apache-2.0 và thông tin tác giả được đưa vào ZIP/APK.

Mã có 6 chữ số, hiệu lực 5 phút; đổi sau 5 lần sai và sau phiên đã được xác thực. Chỉ xử lý một kết nối tại một thời điểm. Android gửi ping mỗi 5 giây; Windows ngắt nếu không nhận dữ liệu trong 30 giây. Android có timeout cho kết nối, heartbeat và ghi dữ liệu để không giữ phiên chết vô hạn.

### Vòng đời và xoay màn hình

Android khởi động dịch vụ foreground trước khi lấy MediaProjection. Callback thu hồi quyền kết thúc phiên. Khi xoay hoặc đổi kích thước vùng chia sẻ, dùng lại VirtualDisplay qua resize/setSurface, tạo lại encoder và gửi cấu hình mới; Windows tạo lại decoder. Không gọi lại createVirtualDisplay trên token Android 14 đã dùng.

Nút dừng, tắt màn hình, xóa tác vụ hoặc mất kết nối đóng phiên và giải phóng encoder, Surface, VirtualDisplay, projection, socket. Phiên mới phải được người dùng cấp quyền lại. Việc dừng phiên cũ không được ngắt nhầm phiên mới vừa kết nối.

### Sửa phiên tự dừng và chẩn đoán bản 1.1.2

Mỗi SenderSession có ConnectionId nội bộ và CaptureRunState giữ nguyên kết quả kết thúc đầu tiên. CapturePermissionGate chỉ nhận kết quả cấp quyền cho đúng kết nối, một lần. CaptureService nhận ConnectionId qua Intent và giữ phiên sở hữu trước bước khởi động; dịch vụ không sở hữu phiên không được ngắt kết nối hiện tại. AppSession dùng thế hệ kết nối để dọn dẹp cũ không ghi đè lỗi phiên mới. Dọn dẹp độc lập từng tài nguyên; lỗi thứ cấp được log nhưng không đổi nguyên nhân đầu tiên.

Log Android nhãn ShareManHinhDT.Capture gồm bước khởi động, Android/model, codec, kích thước, căn chỉnh, profile, cấu hình và khung đầu tiên; lỗi có stack trace và DiagnosticInfo kể cả ngoại lệ codec lồng trong ngoại lệ khác. Giữ tối đa 200 mục log cho báo cáo lỗi; nút Chi tiết lỗi/Sao chép lỗi dùng được khi đã ngắt kết nối. Không ghi mã ghép đôi, dữ liệu cấp quyền hoặc khóa ký. MessageType.Error hiện có được dùng hai chiều; PC giữ lỗi phiên gần nhất khi quay lại chờ, không đổi wire v1.

Encoder chọn trong MediaCodecList RegularCodecs, hỗ trợ AVC và Surface. Chọn kích thước lớn gần mục tiêu 720p, căn chỉnh theo bội chung với 2, kiểm tra hỗ trợ 30 fps; sai lệch log tỷ lệ tối đa 0,03, kích thước tối thiểu 48 theo decoder Windows. Chỉ đặt Baseline khi codec quảng bá; thử lại bằng codec mới không có KeyMaxFpsToEncoder nếu cấu hình lần đầu thất bại. Bitrate nằm trong giới hạn codec; FrameRate giữ 30.

Kiểm thử đã tái hiện lỗi Windows ở 574 × 1280: MF có kích thước bộ đệm lớn hơn vùng hình. Decoder nay đọc minimum/geometric aperture, kiểm tra vùng crop đúng cấu hình, chuyển màu theo kích thước bộ đệm và stride, rồi sao chép vùng hình thực. Nếu thiếu aperture, chỉ chấp nhận kích thước đúng hoặc phần đệm căn chỉnh 16; vẫn giới hạn kích thước và từ chối vùng hình không khớp. Đây là lỗi xác nhận bằng kiểm thử cục bộ; chưa chứng minh là nguyên nhân trên điện thoại đã báo lỗi.

## Kết quả kiểm thử ngày 07/10/2026

Bản 1.1.3 sửa lỗi InvalidCastException đã được người dùng xác nhận bằng stack trace tại GetDisplaySize. AndroidServices.Require chuyển dịch vụ bằng JavaCast qua JNI, kiểm tra null và ghi wrapper C#/lớp Java/tên kiểu đích; áp dụng cho window, display, camera, notification, projection và clipboard. Vẫn dùng window context/MaximumWindowMetrics trên Android 11+, GetRealMetrics trên Android 10; kiểm tra display/bounds/kích thước dương. Callback đổi cấu hình chuyển lỗi qua FailCapture thay vì để ngoại lệ thoát callback.

CameraPreviewTransform tính Matrix3x2 độc lập nền tảng. TextureView đã xử lý sensorOrientation: dùng góc cảm biến chỉ để xác định kích thước ảnh đã định hướng, bỏ giãn tỷ lệ mặc định, bù -Display.Rotation rồi scale đồng đều/căn giữa/cắt phần dư. Góc màn hình 0 không thêm góc cảm biến. Adapter chuyển ma trận vector hàng sang Android Matrix vector cột. DisplayListener đăng ký khi Activity resumed, gỡ khi pause/destroy; cập nhật preview khi display quay 180°, kích thước thay đổi, cấu hình đổi và resume. Không xoay dữ liệu YUV của luồng giải mã.

| Kiểm tra | Trạng thái |
|---|---|
| Biên dịch Windows và Android | Đạt; không có cảnh báo/lỗi C# trong các lần build cuối. |
| 18 nhóm kiểm thử giao thức/QR/trạng thái/ma trận | Đạt trong lần triển khai cuối: 16 nhóm cũ và 2 nhóm ma trận, với cảm biến 0/90/180/270, display 0/90/180/270, buffer 4:3/16:9 và view dọc/ngang/vuông; kiểm tra góc, tâm, crop, scale đồng đều, không lật gương và dữ liệu sai. QR bổ sung xoay 180/270. Log test/publish/verify ghi trong nhật ký triển khai. |
| Hiển thị QR Windows | Đạt: kiểm tra IP/mã/hạn/trạng thái; QR từ BitmapSource thực trên giao diện giải mã được. |
| Camera Android trên điện thoại thật | Chưa chạy: cần xác minh quyền, lấy nét, hướng camera, quét QR PC, hủy/chuyển nền và mở lại. |
| JNI và preview bản 1.1.3 trên thiết bị thật | Chưa xác minh thời gian chạy. Kiểm thử C# ma trận và build Release/trimming không thay thế kiểm tra dịch vụ JNI, listener và ảnh thực trên Xiaomi Android 16 hoặc Android 10. |
| Codec Windows thật | Đạt: mã hóa/giải mã 90 khung H.264 1280 × 720, kiểm tra kích thước, màu và chiều ảnh. |
| Thay đổi cấu hình | Đạt trên Windows: ngang → dọc → 574 × 1280 → ngang; kiểm tra màu/chiều ảnh sau crop và từ chối aperture khác cấu hình. |
| Bộ nhận thật qua loopback TCP/TLS | Đạt: pin QR sai bị chặn trước xác thực, mã sai/đúng, cấu hình, yêu cầu khung khóa, nhận và giải mã video, heartbeat, dừng; gửi lỗi PC về điện thoại, nhận lỗi encoder và giữ lỗi sau quay lại chờ. |
| Tốc độ giải mã và chuyển màu cục bộ | Khoảng 207–263 fps ở các lần chạy Release; đây là phép đo ngắn, không gồm Android, mạng hoặc WPF. |
| Giao diện Windows | Render được ra PNG tại `artifacts/qa/windows-main.png`; kiểm tra trực quan bố cục. |
| Toàn màn hình Windows | Đạt tự động: ẩn các khung/placeholder, bỏ margin/góc bo, render hình dọc/ngang với nền đen ở vùng dư, Esc/F11 khôi phục cửa sổ thường và phóng to. Có ảnh `artifacts/qa/windows-fullscreen.png`. Kiểm thử nhiều màn hình/DPI và thanh tác vụ thực tế còn cần nghiệm thu. |
| ZIP Windows kèm runtime và APK ký nội bộ | Đạt scripts/Deploy.ps1 và Verify-Release.ps1: ba bước test/publish/verify có mã thoát 0; Release Android được trimming, checksum khớp, assembly trong ZIP khớp gói mới, EXE kèm runtime khởi động/đóng với mã thoát 0; APK 1.1.3/versionCode 5 xác minh chữ ký v3 và đúng khóa RSA 3072-bit nội bộ. Manifest min API 29/target API 36. Xem nhật ký triển khai để lấy dung lượng, SHA-256 và log thực tế. |
| Điện thoại thật của ba hãng | Chưa chạy: không có thiết bị kết nối trong môi trường triển khai. |
| FPS/độ trễ toàn tuyến, chạy 30 phút | Chưa nghiệm thu; không suy ra từ tốc độ decoder cục bộ. |

## Quy trình nghiệm thu trên thiết bị thật

1. Chọn ít nhất ba hãng; có máy Android 14 trở lên và máy Android 10–13. Ghi model, phiên bản Android, CPU PC và cấu hình mạng.
2. Cài ZIP trên một máy Windows 11 x64 không cài .NET riêng; cài APK đã ký, giữ debugging tắt.
3. Truyền nội dung chuyển động ở kích thước mục tiêu trong 30 phút. Ghi chỉ số **Nhận** và **Hiển thị** trên PC; trung bình tối thiểu 27 fps khi nội dung chuyển động liên tục. Với nội dung tĩnh, encoder có thể không phát đủ 30 khung mỗi giây.
4. Quay đồng thời hai màn hình với bộ đếm thời gian/chuyển động rõ ràng, đo ít nhất 100 mẫu trải đều phiên. Ít nhất 95% mẫu có độ trễ không quá 500 ms. Không dùng chênh lệch đồng hồ Android/PC khi chưa đồng bộ.
5. Theo dõi bộ nhớ bằng Task Manager và công cụ Android. Không treo; bộ nhớ sau giai đoạn làm nóng không tăng liên tục qua phiên.
6. Thử xoay, chọn một ứng dụng, từ chối quyền, dừng từ thông báo/hệ thống, khóa điện thoại, chuyển mạng, ngắt Wi-Fi và kết nối lại.
7. Thử mã sai, mã hết hạn, chứng chỉ thay đổi, dữ liệu lỗi và Firewall chặn; trạng thái rõ ràng, đóng tài nguyên và có thể bắt đầu lại.
8. Thử QR trên màn hình PC: lựa chọn IP khác, thay mã, QR hết hạn, mã không thuộc ứng dụng, pin khác; quét thành công chỉ tạo một kết nối. Kiểm tra từ chối quyền camera, hủy quét, chuyển nền/mở lại và nhập thủ công.
9. Trên máy model 25080RABDG, Android 16, OS 3.0.308.0.WPPMIXM.C07 đã báo lỗi: hướng camera vào vùng trống 60 giây rồi đưa QR vào; camera phải còn chuyển động và kết nối một lần. Hủy/mở lại 10 lần, chuyển nền, xoay máy; kiểm tra không còn lỗi maxImages. Hiện chưa có thiết bị kết nối nên chưa xác minh hành vi trả bộ đệm ở thời gian chạy trên Android.
10. Toàn màn hình PC: thử Esc/F11, cửa sổ thường/phóng to, dọc/ngang, mất kết nối và nhiều màn hình với DPI khác nhau; không có khung ngoài hoặc thanh tác vụ che hình, khôi phục đúng cửa sổ cũ.
11. Nghiệm thu bản 1.1.2 trên máy Android 16 đã báo lỗi: xác nhận toàn màn hình, truyền hình chuyển động ít nhất 60 giây, xoay và chuyển ứng dụng; dừng từ cả hai phía. Nếu thất bại, sao chép Chi tiết lỗi trên Android và trạng thái PC; kiểm tra lỗi đầu tiên còn nguyên sau dọn dẹp. Thử cấp quyền rồi ngắt/kết nối lại để xác nhận kết quả/callback cũ không tác động phiên mới. Chưa có thiết bị nên các tình huống vòng đời Android này chưa xác minh thời gian chạy.
12. Nghiệm thu bản 1.1.3: log phải đọc được display/window service và kích thước dương, đi qua bước encoder/VirtualDisplay. Cầm dọc hướng camera vào chữ và QR PC, ảnh phải đứng đúng; thử display 90/180/270, hủy, nền/quay lại, mở lại. Kiểm tra listener chỉ hoạt động khi màn hình quét resumed, camera vẫn trả bộ đệm và chỉ kết nối một lần. Xác nhận hình truyền 60 giây và xoay/dừng/kết nối lại. Thử riêng nhánh Android 10 và 11+ khi có thiết bị.

Ghi kết quả cho từng thiết bị. Chỉ đánh dấu đạt 720p30/500 ms khi cả tuyến Android → Wi-Fi → Windows được đo. Nếu cần tối ưu, ưu tiên đo thời gian từng công đoạn trước khi quyết định đổi sang Direct3D; không tự đưa thêm codec hoặc module ngoài.

## Bảo trì và phát hành

Chạy `scripts/Test.ps1`, `scripts/Publish.ps1` rồi `scripts/Verify-Release.ps1`. Bước xác minh kiểm tra checksum, cấu hình runtime, khởi động EXE, chữ ký và manifest APK; không thay thế nghiệm thu điện thoại thật. Khóa ký nội bộ được tái sử dụng, không tạo lại cho mỗi lần phát hành; giữ bản sao lưu ngoài mã nguồn. Phân phối ZIP/APK/checksum, không phân phối `.tools/` hoặc khóa/mật khẩu ký.

Dùng `scripts/Deploy.ps1` để chạy ba bước trên, lưu stdout/stderr theo lần chạy ở artifacts/logs, ghi mã thoát vào steps.json và thêm mục vào [Nhật ký triển khai](NhatKyTrienKhai.md), giờ Asia/Saigon. Thư mục có log cũ không được tái sử dụng. Mục thất bại giữ nguyên; chỉ ghi hoàn tất khi kiểm thử, publish và verify đều có mã thoát 0. Verify kiểm tra assembly bên trong ZIP khớp gói vừa biên dịch, phiên bản Windows/APK, versionCode và vân tay khóa ký nội bộ. Nhật ký ghi dung lượng và SHA-256 của gói.

Theo dõi cập nhật .NET desktop và workload Android riêng; mobile SDK có vòng hỗ trợ ngắn hơn desktop. Gói kèm runtime cần phát hành lại khi cập nhật runtime. Không có máy chủ, telemetry hay cơ chế cập nhật tự động.
