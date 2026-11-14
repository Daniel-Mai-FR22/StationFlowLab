# TrainStation — tàu đến/đi và bảng giờ đồng bộ

Hệ thống dùng một đồng hồ mô phỏng chung cho chuyển động tàu, cửa tàu, bảng giờ trong sảnh, bảng ở ke và giao diện. Đây là lịch minh họa của Gare de Valmont, không phải dữ liệu khai thác SNCF trực tiếp.

## Xem trong Unity

Mở `Assets/TrainStation/Scenes/TrainStation.unity` rồi Play. Bấm **3 / QUAIS** để quan sát tàu tại ke; **1 / HALL** để xem bảng giờ trong sảnh. Có bốn đoàn tàu trên các đường 2, 4, 5 và 7. Hai đoàn đã dừng tại ke lúc bắt đầu, hai đoàn vào ga sau đó. Tàu tiến về phía ụ chặn, dừng đúng vị trí, mở cửa, chờ, đóng cửa rồi đổi hướng rời ga đầu cuối.

Bảng **CIRCULATIONS** ở góc dưới bên phải có giờ mô phỏng, giờ đến/đi, số hiệu, đích đến, đường và trạng thái từng chuyến. **PAUSE** dừng riêng đồng hồ/chuyển động tàu; **REPRENDRE** tiếp tục. **RECOMMENCER** đưa lịch tàu về thời điểm đầu. Các nhân vật trong sảnh dùng hệ thống hành vi riêng.

Đồng hồ mặc định bắt đầu **14:20**, chạy với tỷ lệ **1 giây thực = 3 giây mô phỏng**. Chu kỳ lịch là **12 phút mô phỏng**. Sau khi rời phạm vi trình diễn, cùng đoàn tàu được dùng cho lượt tiếp theo; giờ hiển thị tăng theo chu kỳ, đồng hồ không nhảy ngược về 14:20.

Ở tốc độ mặc định, tàu đường 5 tới vị trí dừng sau khoảng 20 giây thực; tàu đường 2 bắt đầu rời ga sau khoảng 40 giây. Bảng UI ghi giờ đến/đi theo thứ tự **ARR. / DÉP.**; giờ trên bảng chuyến được hiển thị tới phút, đồng hồ hiện tại hiển thị cả giây.

## Tài nguyên và chỉnh sửa

- `Assets/TrainStation/RailOperations/Resources/TrainStationRail/StationRailSchedule.asset`: lịch dùng chung, chứa số hiệu chuyến, ga đi/đến, đường, mốc đến/đi và thời lượng chuyển động/cửa.
- `Assets/TrainStation/RailOperations/Prefabs/Station_TER_FourCar.prefab`: đoàn tàu bốn toa, với các toa lồng prefab, bánh xe, bogie, cửa và chi tiết thân tàu.
- `Station_TER_CabCar.prefab` và `Station_TER_TrailerCar.prefab` trong cùng thư mục: toa có cabin và toa hành khách để tái sử dụng, chỉnh sửa chi tiết.
- Các đoàn tàu, điểm vào/dừng/ra, phần ray nối dài và bộ điều khiển nằm trong nhóm **12 Rail operations** của Hierarchy. Chúng được tạo và lưu trong Editor.
- `StationRailOperations` giữ tham chiếu lịch và danh sách tàu, tỷ lệ đồng hồ, trạng thái chạy/tạm dừng.
- `StationTrain` giữ `serviceId`, `trackNumber`, `visualRoot`, `approachPoint`, `berthPoint`, `exitPoint` và các cánh cửa phía ke.
- `StationTimetableDisplay` giữ tham chiếu tới cùng một bộ điều khiển và từng ô TextMeshPro có sẵn. Chỉnh cỡ chữ, bố cục và màu nền trực tiếp trong Hierarchy.

Mốc `arrivalOffsetSeconds` và `departureOffsetSeconds` tính từ giờ bắt đầu lịch. Số âm đặt lần đến trước thời điểm bắt đầu, để tàu đã ở ke khi mở kịch bản. `approachDurationSeconds` là thời gian từ điểm vào tới điểm dừng; `departureDurationSeconds` là thời gian từ điểm dừng tới điểm ra; `doorDurationSeconds` là thời gian mở hoặc đóng cửa. Khoảng dừng phải đủ cho thao tác cửa.

Khi thêm chuyến/đoàn tàu, dùng ID duy nhất và tham chiếu đúng đường. Chỉnh các điểm đường đi theo cùng tim ray và giữ toàn bộ đoàn tàu trong hành lang đã dựng. Cửa chỉ mở phía tiếp giáp ke. Có thể thay mô hình trong prefab mà giữ bộ điều khiển, nhưng cần kiểm tra chiều dài, cao độ bánh, khoảng hở ke và các tham chiếu cửa.

Inspector của `StationRailOperations` có nút **Preview initial timetable and saved train poses** để áp lại tư thế ban đầu và nội dung bảng trong Edit mode sau khi chỉnh lịch. Lưu scene khi đã kiểm tra. Trong Play có nút tạm dừng/tiếp tục và khởi động lại lịch.

Các sự kiện `trainArrived`, `trainDeparted`, `trainStateChanged` truyền đối tượng tàu; `simulationReset` báo khởi động lại. Có thể nối listener trong Inspector cho các kịch bản sau. Bộ kiểm tra lịch từ chối ID trùng, tham chiếu thiếu, thời gian không hợp lệ hoặc các khoảng chiếm cùng đường bị chồng nhau.

## Trạng thái hiển thị

| Trạng thái tàu | Nội dung bảng |
| --- | --- |
| Chưa vào phạm vi trình diễn | PRÉVU / ATTENDU |
| Đang vào ga | EN APPROCHE |
| Đã dừng, đang mở cửa | ARRIVÉ / OUVERTURE |
| Dừng tại ke | À QUAI |
| Đang đóng cửa | DÉPART IMMINENT |
| Đang rời ga | DÉPART EN COURS |

Bảng lấy trạng thái trực tiếp từ cùng phép mô phỏng điều khiển tàu. Bảng phía sảnh hiển thị giờ đi và đích đến; mặt phía ke hiển thị giờ đến và ga xuất phát. Các bảng treo tại ke lọc theo cặp đường của ke đó. Đồng hồ kim trong sảnh cũng dùng thời gian này.

Nhân vật hiện vẫn hoạt động trong sảnh/sảnh nối ke. Phần này bổ sung tàu và lịch chạy; chưa có hành vi lên/xuống tàu hoặc kết nối lịch tàu với quyết định của hành khách.

## Kiểm tra

Các lệnh trong menu **Efrei Research Lab → Train Station → Rail** và báo cáo trong `Reports/TrainStationRail` hỗ trợ kiểm tra scene đã lưu, chu trình Play và ảnh tham chiếu. Nội dung nghiệm thu thực tế được ghi sau khi chạy kiểm tra.

[Báo cáo nghiệm thu](../../StationFlowLab_Archive_20261020/Reports/TrainStationRail/Acceptance.vi.md) ghi kết quả cuối: kiểm tra hình học, 23 bảng đồng bộ, chu kỳ Play lặp lại, pause/reset và bảo toàn số đối tượng đều thành công.

