# TrainStation — Gare de Valmont

Scene: `Assets/TrainStation/Scenes/TrainStation.unity`.

Scene có cấu hình 1 nhân viên accueil, 3 nhân viên bán hàng/dịch vụ, 3 nhân viên vệ sinh và **300 hành khách**: 18 người ngồi, 72 người trong 24 nhóm trò chuyện, 90 người trong 30 nhóm đi bộ và 120 người đi một mình. Toàn bộ hành khách không mang hành lý; vali đã được xóa khỏi scene và các prefab. Mọi loại hành khách thử sự kiện ngẫu nhiên mỗi 12–35 giây khi rảnh, có thể ghé accueil hoặc xếp hàng tại café, Relay và billetterie rồi quay lại hoạt động trước đó. Số lượt ghé tự động mỗi quầy được giới hạn bằng sức chứa hàng cộng thêm 4 người đang tiếp cận; thứ tự vẫn chỉ được xác định khi thực sự đến. Scene có 24 chỗ ngồi và NavMesh chỉ cho đi trên sàn ở sảnh, cửa tiệm, sảnh nối ke và bốn ke ga. [Hướng dẫn kịch bản xã hội, Prefabs và các sự kiện](TrainStation-Social.vi.md) mô tả cách chỉnh sửa. [Nghiệm thu 300 hành khách](../../StationFlowLab_Archive_20261020/Reports/TrainStationSocial/Dense-Crowd-Acceptance.vi.md) ghi kết quả kiểm thử và giới hạn của bản đông người; các báo cáo 60 người trước đó là lịch sử. Phần bên dưới chủ yếu mô tả kiến trúc và UI nền.

Hệ thống tàu đến/đi và bảng giờ dùng chung đồng hồ mô phỏng được mô tả trong [hướng dẫn lịch tàu](TrainStation-Rail.vi.md). Tàu, tuyến chạy, lịch và các bảng hiển thị đều có tham chiếu được lưu trong Editor.

Hệ thống quan sát gồm một camera sảnh điều khiển bằng chuột và 14 camera giám sát cố định gắn vào cột, tường hoặc xà. [Hướng dẫn camera](TrainStation-Cameras.vi.md) liệt kê 15 góc nhìn, điều khiển, prefab và cách chỉnh vị trí lắp.

Đây là một nhà ga đầu cuối hư cấu tại Pháp, lấy cảm hứng từ bối cảnh SNCF. [Bài báo Scientific Data](https://www.nature.com/articles/s41597-024-04071-9) được dùng làm tham khảo về môi trường nhà ga phục vụ nghiên cứu; dữ liệu ga trong bài báo thuộc Cộng hòa Séc. Gare de Valmont không phải bản sao của ga trong bài báo hoặc một ga SNCF cụ thể.

## Mở và chỉnh sửa trong Editor

1. Mở scene trên trong cửa sổ Project, hoặc chọn **Efrei Research Lab → Train Station → Open TrainStation**.
2. Không cần bấm Play. Mở root **TrainStation — Gare de Valmont** trong Hierarchy để chọn từng khối kiến trúc, đồ nội thất, đường ray, biển báo hoặc thành phần UI.
3. Chỉnh Transform, Renderer, Material, Collider và nội dung TextMeshPro trong Inspector như đối tượng Unity thông thường. Chọn đối tượng rồi nhấn **F** trong Scene view để đến đúng vị trí.
4. Lưu scene bằng **Ctrl+S**. Các thay đổi được lưu trực tiếp trong scene.

Toàn bộ môi trường và Canvas được dựng một lần bởi công cụ Editor rồi ghi vào scene. Chế độ Play không sinh thêm nhà ga hoặc giao diện. Script điều khiển trình bày chỉ chuyển các camera đã lưu và bật/tắt các nhóm có sẵn.

Khi bấm **Play** với `TrainStation` đang mở, chỉ nhà ga chạy. `JunctionLab` khởi động bằng component `DemoController` đã lưu trong scene ngã tư; không còn cơ chế tự tạo ngã tư khi chạy một scene bất kỳ. `TrainStation` đứng đầu danh sách scene trong Build Settings để bản build thông thường mở nhà ga. Menu cũ **Build Windows demo** vẫn dành riêng cho Junction Lab; mở/build demo cũ không xóa TrainStation khỏi danh sách scene.

## Các khu vực trong Hierarchy

| Nhóm | Nội dung |
| --- | --- |
| `01 Architecture — hall et galerie` | Sảnh chính 72 × 56 m, ba cửa vào, mặt đứng đá, cửa kính, hệ cột và vì kèo, mái vòm kính, cánh dịch vụ và sảnh ngang nối các ke ga. |
| `02 Interior — commerces et services` | Nhóm `01 Services`: tiếp đón/bán vé, café/boulangerie, Relay/librairie, WC tiếp cận xe lăn/gia đình, phòng chờ, gửi hành lý, sơ cứu/an ninh/nhân viên. Nhóm `02 Mobilier`: bàn thông tin, bảng giờ đi, đồng hồ, máy tự phục vụ, ghế và dẫn hướng. |
| `03 Rail infrastructure — quais et voies` | Hành lang đường sắt, tám đường ray, bốn ke đảo, mái che ke, thiết bị dây điện trên cao, lối bảo trì và biển thoát ra. |
| `04 Exterior — parvis et ville` | Quảng trường, sân dịch vụ và đường đi hai bên ga, cây và bồn cây, biển SNCF, bản đồ khu vực, đường đón trả khách, điểm taxi/bus, giá để xe đạp, các mặt đứng phố Pháp và nền khu vực 350 × 450 m. |
| `07 Lighting — daylight` | Ánh sáng trời và các nguồn sáng bổ trợ trong sảnh. |
| `08 Cameras — hall orbit and fixed surveillance` | Rig camera sảnh điều khiển bằng chuột. 14 rig CCTV được parent trực tiếp vào cột, tường hoặc xà tại khu vực quan sát; danh sách đủ 15 camera nằm trong `TrainStationViewControls`. |
| `Commandes de présentation — références sérialisées` | Component `TrainStationViewControls` với các tham chiếu camera, nhóm mái và UI. |
| `Interface — Canvas éditable` | Canvas uGUI, tiêu đề, danh mục khu vực, nút camera, nút mái và thông tin bối cảnh; toàn bộ Text/Image/Button đều có sẵn trong Hierarchy. |
| `10 Scenario anchors — empty, no simulation` | Các điểm neo trống cho cửa vào, các khu chức năng, ke ga, lối ra và vị trí quan sát để phát triển kịch bản sau này. |
| `11 Social life — authored actors, seats and navigation` | Prefab 300 hành khách và 7 nhân viên, chỗ ngồi, quầy/hàng chờ, tuyến vệ sinh và NavMesh đã lưu. Nhóm con `10 Dense passenger population — 300 saved travellers` chứa 240 hành khách bổ sung và các điểm neo/tuyến cố định; hành vi chạy khi Play. |
| `12 Rail operations` | Đoàn tàu prefab, điểm vào/dừng/ra, lịch tàu và hạ tầng nối dài; các bảng giờ dùng chung đồng hồ mô phỏng. |

Trong nhóm đường sắt, mỗi **Voie** có ballast, tà vẹt, ray, ụ chặn đầu ray và biển số riêng. Mỗi **Quai** có sàn, dải cảnh báo sát mép, tuyến dẫn hướng, cột/mái che, ghế, thùng rác, bảng thông tin, điểm trợ giúp và tủ kỹ thuật. Các nhóm mang tên **Roofs - ...** cho phép ẩn mái để quan sát bố trí bên trong mà vẫn giữ nguyên cột và khung kết cấu.

## Camera, mái và UI

Chọn **Commandes de présentation — références sérialisées**. Trong Inspector, danh sách đủ **15 camera** hoạt động ngay trong Edit mode. Mở tab **Game** để xem camera đã chọn. Các nút bên dưới chuyển góc, trở về camera sảnh, mở danh mục, bật/tắt mái hoặc giao diện; thao tác hỗ trợ Undo.

Trong Play mode, chọn **CAMERA LIBRE** rồi giữ **chuột phải** để xoay, giữ **nút giữa** để dịch tâm, lăn **bánh xe** để zoom và nhấn **Home** để reset góc sảnh. Dùng **CHOISIR UNE CAMERA** để mở 15 góc nhìn; **PRECEDENTE / SUIVANTE** hoặc **Page Up / Page Down** để chuyển lần lượt. Phím **1–6** chọn nhanh sáu mục đầu; **F1** bật/tắt giao diện. Các CCTV giữ nguyên góc lắp. [Hướng dẫn camera](TrainStation-Cameras.vi.md) có danh mục và cách chỉnh prefab. Bảng **VIE DE LA GARE** điều khiển các sự kiện ngẫu nhiên của hành khách.

Để đổi nội dung UI, mở **Interface — Canvas éditable** và chỉnh trực tiếp các component TextMeshProUGUI, Image, Button hoặc RectTransform. CanvasScaler dùng kích thước tham chiếu **1600 × 900**. Màu vật liệu cảnh nằm trong `Assets/TrainStation/Materials`; thay đổi một vật liệu dùng chung sẽ tác động đến mọi đối tượng sử dụng vật liệu đó.

## Kích thước và giới hạn của bản tĩnh

- Đơn vị dựng hình là mét. Sàn hành khách nằm gần cao độ `Y = 0`; bốn ke đảo rộng 8 m, dài 170 m, trải từ `Z = 44` đến `Z = 214`.
- Tám tim đường ray cách mép ke gần nhất 1,7 m. Khoảng cách giữa hai mặt trong đầu ray là 1,435 m. Hệ thống tàu bổ sung đoạn ray nối dài sau `Z = 214` và lối vào/ra đoàn tàu.
- Đồ nội thất, cây, biển báo và công trình phụ là mô hình hình học mô-đun, có thể chỉnh sửa; không phải bản quét ảnh, BIM hoàn công hoặc mô hình kỹ thuật xác nhận tuân thủ tiêu chuẩn SNCF.
- Bảng giờ trong sảnh, tại ke và đồng hồ được đồng bộ với các tàu trong lịch mô phỏng. Lịch là dữ liệu minh họa chỉnh được, không kết nối dịch vụ SNCF trực tiếp; chưa có vé điện tử.
- NavMesh và nhân vật hoạt động ở sảnh, các cửa tiệm, sảnh nối ke và vùng an toàn trên bốn ke. Chỉ collider sàn được chỉ định mới có thể đi; 71 băng ghế và 21 bồn cây có vùng loại trừ từ sàn lên trên đồ vật. Lệnh bake navigation giữ chính sách này. Tàu chạy trong hành lang đường ray đã dựng; chưa mô phỏng mạng đường sắt bên ngoài hay hành khách lên/xuống tàu. Phòng dịch vụ và công trình phố ngoài ga phục vụ bố trí bối cảnh; mức độ nội thất không đồng đều giữa mọi công trình.

## Tạo bản mới và kiểm tra

**Open TrainStation** và **Create a new editable scene** sẽ mở scene hiện có nếu scene đã được tạo; không ghi đè các chỉnh sửa thủ công. **Generate a separate fresh copy** tạo scene khác với tên mới để so sánh với bản đã chỉnh sửa.

Menu **Validate saved scene** kiểm tra scene đang mở và ghi báo cáo trong `Reports/TrainStation/validation.json`. Menu **Save reference images** xuất các góc chụp vào `Reports/TrainStation`. Những lệnh này là công cụ hỗ trợ kiểm tra; việc có lệnh trong menu không tự chứng minh scene đã vượt qua kiểm tra.

Menu **Check Play mode isolation** chạy ba lượt Play: TrainStation → JunctionLab → TrainStation. Lưu các chỉnh sửa scene trước khi dùng lệnh này. Công cụ kiểm tra việc tách hai kịch bản, camera/đèn và callback UI, sau đó trả về scene cùng thiết lập Editor ban đầu. Kết quả nằm trong `Reports/TrainStation/play-mode-check.json`; lượt kiểm tra ngày 28/09/2026 đã thành công trong dự án chính.

Kết quả lịch sử của bản tĩnh trước khi bổ sung nhân vật: [báo cáo nghiệm thu](../../StationFlowLab_Archive_20261020/Reports/TrainStation/Acceptance.vi.md). Bản tĩnh khi đó có 6.545 đối tượng được lưu sẵn, 6 camera và 8 nút UI; kiểm tra 12 tuyến tiếp cận tại 928 điểm đã hoàn thành không có lỗi. Số liệu này không mô tả scene hiện hành. Báo cáo kịch bản xã hội nằm trong `Reports/TrainStationSocial`; xem [nghiệm thu quần thể 300 người](../../StationFlowLab_Archive_20261020/Reports/TrainStationSocial/Dense-Crowd-Acceptance.vi.md) để biết kết quả kiểm thử đông người, thay vì dùng các báo cáo 60 người hoặc bản tĩnh.
