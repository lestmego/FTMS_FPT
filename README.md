# FTMS Companion

Ứng dụng Windows theo dõi ticket FTMS theo thời gian thực và gửi thông báo Telegram.

## Cài đặt

Tải và chạy `dist/FTMS-Companion-Setup-1.0.6.exe` trên Windows 64-bit. Bộ cài chứa .NET 8 và tự cài Microsoft Edge WebView2 Runtime khi cần.

## Xây dựng

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

Bộ cài được tạo trong thư mục `dist`.

## Dữ liệu cục bộ

Token Telegram, cấu hình người dùng, SQLite và phiên WebView2 được lưu trong `%LOCALAPPDATA%\FTMS.Companion`; các dữ liệu này không nằm trong repository.

## Telegram qua proxy công ty

Trong **Cài đặt**, nhập **HTTP proxy cho Telegram** theo dạng `http://host:port`, rồi bấm **Gửi thử**. Nếu mạng không cần proxy, để trống ô này: ứng dụng sẽ kết nối Telegram trực tiếp và không dùng proxy hệ thống của Windows. Nếu có proxy, toàn bộ API Telegram dùng proxy đã nhập; thay đổi có hiệu lực ngay sau khi lưu mà không cần khởi động lại. Proxy chỉ áp dụng cho Telegram, còn trang FTMS trong WebView2 vẫn dùng cấu hình mạng của Windows. Proxy có tài khoản/mật khẩu chưa được hỗ trợ; nếu proxy kiểm tra TLS thì chứng thư gốc của công ty phải được Windows tin cậy.

Nếu máy cũng chạy Telegram Checklist Reporter, có thể lấy `TELEGRAM_PROXY_HOST` và `TELEGRAM_PROXY_PORT` từ `D:\Notification Telegram\config.env` và ghép thành `http://host:port`. Không nhập bot token hoặc API hash của ứng dụng đó vào ô proxy. Sau khi lưu, các thông báo đang chờ trong SQLite sẽ được thử gửi lại khi FTMS còn đăng nhập và đồng bộ thành công.

## Lọc thông báo

Ứng dụng chỉ gửi cảnh báo cho ticket thuộc `TOC - Phòng Dịch vụ Data Center`. Email tự động từ `fti.sd02@fpt.com`, `ihub.akabot2` và `ducvm19@fpt.com` được bỏ qua khi chọn email mới nhất. Không lọc theo miền hay từ khóa. Tiêu đề thông báo lấy nguyên văn tiêu đề ticket từ FTMS, gồm cả phần `[Miền ...]`.
