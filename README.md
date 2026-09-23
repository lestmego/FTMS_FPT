# FTMS Companion

Ứng dụng Windows theo dõi ticket FTMS theo thời gian thực và gửi thông báo Telegram.

## Cài đặt

Tải và chạy `dist/FTMS-Companion-Setup-1.0.0.exe` trên Windows 64-bit. Bộ cài chứa .NET 8 và tự cài Microsoft Edge WebView2 Runtime khi cần.

## Xây dựng

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

Bộ cài được tạo trong thư mục `dist`.

## Dữ liệu cục bộ

Token Telegram, cấu hình người dùng, SQLite và phiên WebView2 được lưu trong `%LOCALAPPDATA%\FTMS.Companion`; các dữ liệu này không nằm trong repository.
