# FTMS Companion - Bao cao audit he thong

Ngay audit: 23/09/2026

Pham vi audit:

- Doi chieu ma nguon voi `plan.md`.
- Kiem tra ung dung dang chay tai `D:\FTMS-FPT`.
- Kiem tra cau hinh khoi dong Windows.
- Kiem tra SQLite snapshot, event va Telegram outbox.
- Kiem tra cac luong trang thai, email, SLA, terminal ticket va auto-login.

## 1. Ket luan chung

Ung dung da dap ung phan lon luong chinh: theo doi ticket, phat hien thay doi, gui Telegram, chong trung, chay ngam, khoi dong cung Windows va loai ticket dong khoi bo nho hoat dong.

Tuy nhien, ung dung chua dat day du tat ca tieu chi trong `plan.md`. Ba hang muc can uu tien xu ly la:

1. Bao dam moi thong bao co noi dung email moi nhat day du.
2. Quan ly canh bao SLA theo tung chu ky SLA.
3. Hoan thien auto-login recovery va nhat ky van hanh.

## 2. Cac sai sot phat hien

### 2.1. Muc do cao - Chua bao dam noi dung email trong moi thong bao

Ket qua database tai thoi diem audit:

- Tong so event: 144.
- Event khong co `LatestEmail.Body`: 13.
- Telegram outbox dang cho: 0.
- Telegram outbox loi: 0.

Hien tai formatter van co the gui thong bao voi noi dung `Khong lay duoc noi dung email` neu FTMS chua tra file email.

Dieu nay chua dap ung yeu cau:

- Moi thay doi trang thai phai kem email moi nhat.
- Moi thong bao Telegram phai co nguoi gui, thoi gian gui, tieu de va noi dung email.

Can xu ly:

- Khong dung tieu de ticket thay cho noi dung email.
- Lay `fileId` tu lich su email hoac C88.
- Doc noi dung tu `/ihub/email/ReadMailFromFile?fileId=...`.
- Neu file chua san sang, dua event vao trang thai cho va thu lai truoc khi gui Telegram.
- Chi danh dau event da gui sau khi da lay du noi dung email hop le.

Tep lien quan:

- `src/FTMS.Desktop/WebViewFtmsClient.cs`
- `src/FTMS.Application/TicketChangeDetector.cs`
- `src/FTMS.Application/NotificationFormatter.cs`

### 2.2. Muc do cao - SLA chua tach theo chu ky

Event key SLA hien gom:

- Ma ticket.
- Loai event.
- Trang thai cu va moi.
- Email ID.
- Nguong SLA.

Neu SLA duoc khoi phuc, sau do ticket lai di vao cung mot nguong voi cung trang thai va email, event key co the trung voi lan truoc. Khi do canh bao SLA moi se bi chan.

Can xu ly:

- Them `SlaCycleId` hoac thoi diem bat dau chu ky SLA vao snapshot.
- Reset cac nguong da gui khi SLA quay lai muc an toan.
- Tao event key gom ticket, nguong va chu ky SLA.
- Bao dam moi nguong chi gui mot lan trong mot chu ky, nhung duoc gui lai trong chu ky moi.

Tep lien quan:

- `src/FTMS.Application/TicketChangeDetector.cs`
- `src/FTMS.Domain/Models.cs`

### 2.3. Muc do cao - Auto-login recovery chua hoan chinh

Hien tai khi phat hien het phien, ung dung dieu huong WebView2 ve URL FTMS. Co che nay phu thuoc vao session SSO da luu trong profile.

Con thieu:

- Retry login theo chu ky co kiem soat.
- Xac nhan trang danh sach FTMS da tai thanh cong truoc khi resume polling.
- Thong bao Windows khi CAS/MFA yeu cau nguoi dung thao tac.
- Nhat ky thoi diem het phien, retry va dang nhap thanh cong.
- Co che tranh dieu huong login lap lien tuc.

Tep lien quan:

- `src/FTMS.Desktop/WebViewFtmsClient.cs`
- `src/FTMS.Application/TicketMonitor.cs`

### 2.4. Muc do trung binh - Lich su terminal khong dung retention trong plan

Hien tai tat ca snapshot terminal bi xoa ngay tai moi lan cleanup.

Uu diem:

- Ticket dong duoc giai phong khoi bo nho va bang snapshot.
- Database khong bi phinh boi ticket da dong.

Sai khac voi `plan.md`:

- Plan yeu cau giu compact history trong thoi gian retention mac dinh 30 ngay.
- Retention phai cau hinh duoc.

Can quyet dinh:

- Neu yeu cau cuoi cung la xoa ngay: cap nhat lai `plan.md`.
- Neu can audit 30 ngay: giu event compact, xoa payload email nhay cam va chi xoa khi het retention.

Tep lien quan:

- `src/FTMS.Infrastructure/SqliteTicketStore.cs`

### 2.5. Muc do trung binh - Polling khong tam dung khi nguoi dung thao tac

`TicketMonitor.RunAsync` nhan `userIsActive` nhung khong su dung gia tri nay.

Hien tai:

- Auto-refresh giao dien co tam dung khi nguoi dung thao tac.
- API polling van chay moi 2 giay.

Dieu nay khac voi plan, nhung co the phu hop voi yeu cau realtime moi nhat cua nguoi dung.

Can quyet dinh:

- Neu uu tien realtime: cap nhat plan de polling khong tam dung.
- Neu uu tien giam tai FTMS: dung `userIsActive` de tam dung polling.

Tep lien quan:

- `src/FTMS.Application/TicketMonitor.cs`
- `src/FTMS.Desktop/CompactWindow.xaml.cs`

### 2.6. Muc do trung binh - Loi API bi an

Trong cac script WebView co nhieu khoi `catch {}` khong ghi log.

Rui ro:

- Khong biet `GetEmailByCode`, C88 hay `ReadMailFromFile` bi loi.
- Kho phan biet API khong co du lieu voi loi parse JSON.
- Kho dieu tra khi Telegram nhan noi dung thieu.

Can xu ly:

- Tra ve ma loi co cau truc tu JavaScript.
- Ghi log endpoint, HTTP status va ticket code.
- Khong ghi token Telegram hoac noi dung email nhay cam vao log.

Tep lien quan:

- `src/FTMS.Desktop/WebViewFtmsClient.cs`

### 2.7. Muc do trung binh - Settings chua day du

Settings hien co:

- Telegram bot token.
- Telegram chat ID.
- Tu dong refresh.
- Chu ky refresh.
- Khoi dong cung Windows.

Con thieu so voi plan:

- FTMS base URL.
- Polling interval.
- Idle delay.
- Telegram event toggles.
- SLA thresholds.
- Terminal retention.
- Tuy chon minimize to tray.

Tep lien quan:

- `src/FTMS.Desktop/SettingsStore.cs`
- `src/FTMS.Desktop/CompactSettingsWindow.xaml`

### 2.8. Muc do thap - Dashboard chua day du

Dashboard hien co:

- Moi/phan cong.
- Dang thuc hien.
- Tam ngung.
- Hoan thanh va dong dang bi gop.
- SLA.

Con thieu:

- Ticket tao trong ngay.
- Hoan thanh va dong tach rieng.
- Nguoi dung dang xu ly ticket.
- Chi so SLA nguy co va vi pham tach rieng.

Tep lien quan:

- `src/FTMS.Desktop/CompactWindow.xaml`
- `src/FTMS.Desktop/CompactWindow.xaml.cs`

### 2.9. Muc do thap - Outbox chua luu Telegram response

Outbox hien luu:

- Event key.
- Message.
- So lan thu.
- Lan thu tiep theo.
- Thoi diem gui.
- Loi cuoi.

Con thieu:

- Telegram message ID.
- Telegram response code.
- Destination/chat ID da rut gon.
- Trang thai gui ro rang.

Vi khong co message ID, ung dung khong the sua hoac xoa thong bao cu mot cach tin cay.

Tep lien quan:

- `src/FTMS.Infrastructure/TelegramOutboxSender.cs`
- `src/FTMS.Infrastructure/SqliteTicketStore.cs`

### 2.10. Muc do thap - Chua co automated tests

Khong tim thay project test trong solution.

Can bo sung test cho:

- Anh xa trang thai FTMS.
- Loc Akabot va email tu dong.
- Tach email goc trong quoted history.
- Cat chu ky va thong bao bao mat.
- Event key chong trung.
- Chu ky SLA.
- Terminal eviction.
- Telegram formatter.

## 3. Cac hang muc dang hoat dong dung

### Build va runtime

- Build thanh cong.
- Khong co warning.
- Khong co error.
- Ung dung dang chay tu `D:\FTMS-FPT`.

### Chay ngam va khoi dong Windows

- Co system tray.
- Dong cua so se an xuong tray thay vi tat ung dung.
- Registry co muc `FTMS Companion`.
- Executable startup tro dung den ban dang chay.

### Bao mat

- Telegram token duoc bao ve bang Windows DPAPI.
- Khong luu mat khau FPT trong settings hoac SQLite.
- WebView2 su dung user-data directory rieng de giu session.

### Theo doi API

- Polling API moi 2 giay.
- Auto-refresh danh sach FTMS moi 5 giay.
- Auto-refresh bam nut Kendo `k-pager-refresh`, khong reload toan trang.
- Co observer de dong bo ngay khi API danh sach cap nhat.

### Trang thai FTMS

Anh xa hien tai:

- `0`: Tao moi.
- `1`: Phan cong.
- `2`: Dang thuc hien.
- `3`: Hoan thanh.
- `4`: Tam ngung.
- `5`: Dong.
- `7`: Huy.
- `8`: Khong xu ly.

### Telegram va chong trung

- Event key la duy nhat.
- Outbox co unique constraint theo event key.
- Khong co duplicate event trong database tai thoi diem audit.
- Khong co duplicate outbox trong database tai thoi diem audit.
- Khong co thong bao dang cho hoac dang loi tai thoi diem audit.
- Co retry theo exponential backoff khi Telegram loi.

### Ticket terminal

- Ticket terminal duoc loai khoi active-memory cache.
- Snapshot terminal hien khong con trong database.
- Co thong bao rieng cho ticket da dong, huy va khong xu ly.

### Du lieu tai thoi diem audit

- Active snapshots: 24.
- Terminal snapshots: 0.
- Tong event: 144.
- Terminal event: 12.
- EmailReceived event: 7.
- Pending outbox: 0.
- Failed outbox: 0.
- Duplicate event key: 0.
- Duplicate outbox key: 0.

## 4. Thu tu de xuat xu ly

### Buoc 1 - Email delivery gate

- Event chi duoc gui khi email provider da hoan tat.
- Retry `GetEmailByCode`, C88 va `ReadMailFromFile`.
- Luu trang thai `WaitingForEmail` trong outbox.
- Co gioi han retry va thong bao loi van hanh.

### Buoc 2 - SLA cycle

- Them cycle ID.
- Reset threshold khi SLA phuc hoi.
- Test cac tinh huong pause/resume/overdue.

### Buoc 3 - Auto-login production flow

- State machine cho authenticated, expired, recovering va waiting-for-user.
- Windows notification khi can MFA.
- Resume polling an toan sau khi login.

### Buoc 4 - Logging va audit

- Rolling file log.
- Log endpoint, ticket code, status va retry.
- Khong log token hoac full email nhay cam.

### Buoc 5 - Settings va dashboard

- Bo sung toan bo cau hinh con thieu.
- Tach cac chi so dashboard.

### Buoc 6 - Automated tests

- Unit test domain rules.
- Integration test SQLite outbox.
- Test formatter voi cac mau email FTMS thuc te.

## 5. Tieu chi hoan thanh de xuat

He thong chi nen duoc danh dau hoan tat khi:

- Khong co thong bao trang thai nao bi gui khi email con dang cho tai.
- Moi thong bao co email hop le hoac co trang thai loi duoc theo doi ro rang.
- SLA gui dung mot lan trong moi chu ky.
- Auto-login co state machine va thong bao MFA.
- Tat ca loi API duoc ghi log.
- Settings phu cac tham so van hanh trong plan.
- Co automated tests cho cac rule quan trong.
- Build, test va long-running monitoring deu thanh cong.
# Đính chính sau khi tải lại Kendo grid - 23/09/2026

- Lần đối chiếu 14:22 dùng dữ liệu đang giữ trên giao diện FTMS, chưa bấm đúng nút `Tải lại` của Kendo grid.
- Sau khi bấm `Tải lại`, 8 ticket từng hiển thị `Phân công` không còn trong bộ lọc `Mới, Phân công`.
- Vì vậy không thể kết luận ứng dụng đổi sai các ticket đó sang `Tạm ngưng`; dữ liệu API của ứng dụng mới hơn dữ liệu giao diện cũ.
- Các vấn đề email người gửi, nội dung email và cách kiểm chứng SLA vẫn cần xử lý độc lập.

# Đối chiếu trực tiếp FTMS - 23/09/2026 14:22-14:37

## Sai lệch xác nhận trên dữ liệu đang chạy

- Trang FTMS `Danh sách hỗ trợ` đang hiển thị 8 ticket, tất cả ở trạng thái `Phân công`.
- Ứng dụng chỉ lưu đúng `RQ202609230151` là trạng thái `1 - Phân công`.
- Ứng dụng lưu sai `RQ202609230148`, `RQ202609230142`, `RQ202609230135`, `RQ202609230115` thành trạng thái `4 - Tạm ngưng`.
- Ứng dụng không có snapshot hoạt động cho `RQ202609230138`, `RQ202609230121`, `IN202609230138` dù các ticket này đang hiện trên FTMS.
- Đây không phải dữ liệu SQLite cũ: thời gian `updated_at` vẫn được cập nhật theo vòng đồng bộ hiện tại.

## Sai lệch email

- `RQ202609230151`: FTMS hiển thị người gửi `huyenntb4@fpt.com`, ứng dụng lưu `fti.support@fpt.com`.
- `RQ202609230148`: FTMS hiển thị `HuyenNT87@fpt.com`, ứng dụng không có người gửi.
- `RQ202609230142` và `RQ202609230135`: phần thân email trong snapshot chỉ dài tương đương tiêu đề/fallback, chưa phải đầy đủ nội dung email mới nhất.
- `RQ202609230115` có nguồn `Others` và người tạo `thonv7`; không được tự suy diễn thành địa chỉ email nếu FTMS không cung cấp email thật.

## Nguyên nhân kỹ thuật cần xử lý tiếp

- Luồng chuẩn hóa danh sách đang chọn nhầm biểu diễn/bản ghi khi API trả cấu trúc lồng hoặc nhiều bản ghi cùng mã.
- Bộ `unwrap()` trả mảng đầu tiên tìm thấy, chưa chứng minh đó luôn là mảng dữ liệu grid chính.
- Trường email lấy từ lịch sử có thể ghi đè email khách hàng trên danh sách bằng thư nội bộ `fti.support@fpt.com`.
- SLA trong ứng dụng đang lấy các giá trị chênh lệch rất lớn (ví dụ hơn 1400 phút) trong khi FTMS hiển thị `Bình thường`; chưa đủ tin cậy để phát cảnh báo Telegram.

## Thay đổi an toàn đã giữ lại

- Danh sách được khử trùng mã ticket trước khi ghép lịch sử đóng; giữ bản ghi đầu tiên theo thứ tự API thay vì để bản ghi trùng phía sau âm thầm ghi đè.
- Bản thử nghiệm ép bộ lọc trạng thái đã được gỡ vì chưa sửa được sai lệch và có nguy cơ bỏ sót trạng thái khác.
