# Camera của TrainStation

## Bản Daily hiện tại — GareFlowDemo

Phần này áp dụng cho `Assets/TrainStation/Scenes/TrainStationDaily.unity`. Scene Daily có **16 camera giám sát cố định và 1 camera orbit**. Bốn camera **HALL-01, HALL-02, HALL-03, HALL-04** quan sát đài phun nước giữa sảnh từ bốn góc khác nhau; vị trí, hướng và calibration riêng của từng camera được lưu cùng video. Camera orbit dùng để quan sát và follow voyageur, không nằm trong danh sách video CCTV.

Trong **Collecte de données**, chọn camera trước **Démarrer la collecte**. Cấu hình mới chọn sẵn bốn camera Hall; **Toutes** chọn toàn bộ 16 CCTV. Dropdown duy nhất chọn **480p (854 × 480), 720p (1280 × 720) hoặc HD (1920 × 1080)**, mặc định **720p**. Không có lựa chọn FPS cố định: hệ thống thử ghi theo từng frame Unity thực sự chạy. Panneau hiển thị FPS game và FPS video đã ghi của từng camera, đo trên cửa sổ trượt cố định một giây; trong giây đầu các giá trị có thể thấp. `sync.jsonl` ghi mẫu `kind: measured_frame_rates` cùng horodatage; `frames.jsonl` bổ sung `captureIntervalRealSeconds`, `captureFpsInstantaneous` và `captureFpsRolling1s`. Hai giá trị đầu là `null` ở ảnh đầu mỗi camera. Các luồng dữ liệu avatar/sự kiện có nhịp riêng và phải đo từ timestamp theo từng luồng/agent, không lấy FPS video làm nhịp chung. AVI/MJPEG vẫn phát theo cadence danh nghĩa cố định (`nominalPlaybackFps`, trường tương thích `videoFps`); khi phân tích phải dùng các stamp thực để khôi phục nhịp ghi biến thiên, không coi đây là video VFR.

Bắt đầu thu thập luôn đặt tốc độ mô phỏng về **×1**. **×4 và ×8 bị khóa khi còn thu thập, kể cả trong lúc pause**; pause vẫn hoạt động và khi tiếp tục sẽ chạy ở ×1. Sau **Arrêter et finaliser les fichiers**, các điều khiển tốc độ được mở lại. FPS game/video vẫn được đo bằng thời gian thực, độc lập với việc pause thời gian mô phỏng.

Các lượt ghi Daily mới dùng manifest v2/layout `per_agent.v1`: `agents/index.json` dẫn tới dữ liệu từng `agentId` trong `agents/<safeDir>/`, còn `sync.jsonl` và `video/<cameraId>/` dùng chung. Để ghép ảnh với avatar, lấy `captureStamp` từ file trong thư mục của đúng `agentId` rồi nối với `stamp` của ảnh qua `(runId, syncSampleId)`. Không gom theo `actorSlotId`, vì slot hiển thị được tái sử dụng. Các mục index có thể chưa có thư mục nếu chưa có dữ liệu và không bật profile. Camera, calibration, FPS thực, pause và khóa ×1 không đổi; dữ liệu cũ không bị di chuyển.

Các phần dưới đây mô tả **scene TrainStation gốc** với 14 CCTV và 1 camera quan sát. Danh mục và tổng số 15 góc nhìn bên dưới thuộc scene gốc, không phải danh mục 17 góc của scene Daily.

---

## Scene TrainStation gốc

Scene `Assets/TrainStation/Scenes/TrainStation.unity` có **15 camera được lưu sẵn**: một camera điều khiển bằng chuột giữa sảnh và 14 camera giám sát cố định. Camera, điểm neo, thân camera, điểm quan sát và UI chọn góc nhìn đều chỉnh được trong Editor.

## Quan sát trong Play

Chọn **CAMERA LIBRE** ở thanh dưới để trở về camera sảnh. Thao tác trên phần hình ảnh 3D:

| Thao tác | Kết quả |
| --- | --- |
| Giữ chuột phải rồi kéo | Xoay quanh tâm quan sát của sảnh. |
| Giữ nút giữa rồi kéo | Dịch tâm quan sát trong giới hạn sảnh. |
| Lăn bánh xe chuột | Thu/phóng khoảng cách tới tâm. |
| **Home** | Khôi phục góc nhìn ban đầu của camera sảnh đang chọn. |
| **RECENTRER** | Chọn camera sảnh và khôi phục góc nhìn ban đầu. |
| **PRECEDENTE / SUIVANTE**, hoặc **Page Up / Page Down** | Chuyển lần lượt qua toàn bộ 15 camera. |
| **CHOISIR UNE CAMERA** | Mở danh sách 15 góc nhìn; chọn một ô để chuyển camera. |
| Phím **1–6** | Chọn nhanh sáu mục đầu trong bảng bên dưới. |
| **F1** | Ẩn/hiện giao diện. |

Chuột trái tiếp tục dùng cho các nút UI. Các bảng giao diện chặn thao tác camera ở phần chúng che phủ. Con trỏ được nhả khi thả nút đang kéo, chuyển camera hoặc cửa sổ mất focus. Khi quay lại camera sảnh trong cùng lượt Play, góc nhìn vừa điều chỉnh được giữ lại; dùng **RECENTRER** để trở về góc gốc.

Camera sảnh có giới hạn góc cao, khoảng cách và vùng dịch tâm. Camera tự rút ngắn khoảng cách khi gặp kiến trúc, rồi trở về khoảng cách đã yêu cầu khi đường nhìn thông thoáng. Các nhân vật không làm camera giật lùi khi đi ngang. Camera giám sát không nhận thao tác xoay, dịch hay zoom bằng chuột.

Mái có 98 đối tượng con `Camera collision — roof geometry (excluded from navigation)` được lưu sẵn. Collider khớp với hình mái và được loại khỏi quá trình bake NavMesh bằng `NavMeshModifier`. Chúng đi theo mái khi chỉnh Transform; khi chủ động ẩn mái bằng UI, va chạm của phần mái đó cũng được tắt.

## Danh mục và vị trí lắp

| STT | ID / góc nhìn | Khu vực quan sát | Điểm gắn vật lý |
| --- | --- | --- | --- |
| 01 | Hall — camera libre | Tổng quan sảnh và các hoạt động xung quanh tâm sảnh. | Rig quan sát riêng, tâm cố định trong sảnh. |
| 02 | `EXT-01` — Parvis / entrées | Quảng trường, lối vào và luồng người trước nhà ga. | Trụ đá mặt tiền `Limestone pier`. |
| 03 | `QUAI-1-A` — Quai 1 / accès | Lối vào và đoạn đầu ke 1, giữa đường ray 1–2. | Cột `Gallery post` tại đầu ke. |
| 04 | `HALL-01` — Accueil / file | Nhân viên accueil, khách được phục vụ và hàng chờ. | Cột sắt `Iron column` trong sảnh. |
| 05 | `SHOP-01` — Café / comptoir | Quầy café, nhân viên, khách mua hàng và cửa vào. | Tường `Cloison_droite` bên trong café. |
| 06 | `HALL-02` — Concourse / correspondances | Sảnh nối ke và các luồng đi ngang. | Cột `Gallery post` ở đầu sảnh nối ke. |
| 07 | `SHOP-02` — Relay / caisse | Quầy thanh toán, hàng mua đồ và lối đi của Relay. | Tường `Cloison_gauche` bên trong Relay. |
| 08 | `SHOP-03` — Billetterie / guichets | Nhân viên, khách và hàng chờ mua vé. | Tường `Cloison_gauche` của khu bán vé. |
| 09 | `QUAI-1-B` — Quai 1 / retour | Phần xa và vùng chờ của ke 1, nhìn về đầu ke. | Xà mái che `Canopy crossbeam 5`. |
| 10 | `QUAI-2-A` — Quai 2 / accès | Lối vào và đoạn đầu ke 2, giữa đường ray 3–4. | Cột `Gallery post` tại đầu ke. |
| 11 | `QUAI-2-B` — Quai 2 / retour | Phần xa và vùng chờ của ke 2, nhìn về đầu ke. | Xà mái che `Canopy crossbeam 5`. |
| 12 | `QUAI-3-A` — Quai 3 / accès | Lối vào và đoạn đầu ke 3, giữa đường ray 5–6. | Cột `Gallery post` tại đầu ke. |
| 13 | `QUAI-3-B` — Quai 3 / retour | Phần xa và vùng chờ của ke 3, nhìn về đầu ke. | Xà mái che `Canopy crossbeam 5`. |
| 14 | `QUAI-4-A` — Quai 4 / accès | Lối vào và đoạn đầu ke 4, giữa đường ray 7–8. | Cột `Gallery post` tại đầu ke. |
| 15 | `QUAI-4-B` — Quai 4 / retour | Phần xa và vùng chờ của ke 4, nhìn về đầu ke. | Xà mái che `Canopy crossbeam 5`. |

Mỗi ke có hai hướng quan sát nhằm phủ cả đoạn gần và đoạn xa. Người, tàu hoặc đồ nội thất vẫn có thể che nhau trong ảnh như với một camera cố định thông thường. Các điểm kiểm tra bao phủ hỗ trợ đánh giá vị trí lắp; chúng không bảo đảm thấy được mọi người ở mọi thời điểm.

## Tìm và chỉnh trong Editor

Chọn **Commandes de présentation — références sérialisées**. Inspector liệt kê đủ 15 góc nhìn và các nút chuyển camera, trở về camera sảnh, mở/đóng danh mục, bật/tắt mái hoặc UI. Các nút hoạt động trong Edit mode và hỗ trợ Undo. Mở tab **Game** để xem kết quả.

Camera sảnh nằm trong **08 Cameras — hall orbit and fixed surveillance → 01 Hall game camera — mouse orbit, pan and zoom**. Chỉnh component `TrainStationOrbitCamera` để thay góc ban đầu, khoảng cách, độ nhạy chuột và giới hạn dịch tâm. `Hall observation focus` là điểm tâm có Transform riêng. Sau khi sửa cấu hình góc ban đầu, dùng **Recentrer le hall** trong Inspector để áp dụng rồi lưu scene.

Các camera CCTV nằm **dưới chính cột, tường hoặc xà đang đỡ chúng** trong Hierarchy, vì vậy chúng đi cùng kết cấu khi kết cấu được di chuyển. Có thể tìm bằng `CCTV` hoặc chọn camera qua tham chiếu `Viewpoints` của `TrainStationViewControls`. Mỗi rig có bản đế, tay đỡ, hộp camera, thấu kính và đối tượng Camera quang học.

Chỉnh hướng ở **Camera head — fixed surveyed orientation**, chỉnh FOV/clip planes tại Camera quang học. Component `StationSurveillanceCamera` lưu ID, tên, mục đích, `mountAnchor`, `aimTarget` và các `coveragePoints`. Chọn rig để thấy các đường Gizmo đến mục tiêu. Các mục tiêu là tham chiếu kiểm tra; dịch `aimTarget` không tự quay camera trong Play. Khi thay góc lắp, cập nhật điểm mục tiêu và các điểm bao phủ tương ứng.

Chỉ Camera và AudioListener của góc đang chọn được bật. Các thân camera và rig vẫn hiện diện trong cảnh khi chuyển sang góc khác. Khung đỡ camera ke nằm cùng kết cấu mái, tách khỏi phần mái có thể ẩn bằng nút UI.

Những phần mái trước đây chỉ có Renderer được bổ sung đối tượng con **Camera collision — roof geometry (excluded from navigation)**. Collider đi theo đúng hình mái và ẩn cùng nhóm mái, giúp camera sảnh dừng trước kính vòm hoặc trần. Các đối tượng này không tham gia bake NavMesh; chúng không làm thay đổi đường đi của hành khách.

## Prefab và giao diện tái sử dụng

| Asset | Công dụng |
| --- | --- |
| `Assets/TrainStation/Cameras/Prefabs/Station_Hall_GameCamera.prefab` | Rig camera sảnh và điểm tâm, dùng lại cho góc quan sát bằng chuột. |
| `Assets/TrainStation/Cameras/Prefabs/Station_CCTV_Mount.prefab` | Hộp camera cố định, bản đế, tay đỡ, Camera và metadata. |
| `Assets/TrainStation/Cameras/Runtime/TrainStationOrbitCamera.cs` | Điều khiển camera sảnh và tránh kiến trúc. |
| `Assets/TrainStation/Cameras/Runtime/StationSurveillanceCamera.cs` | Thông tin vị trí lắp và mục đích bao phủ của CCTV. |
| `Assets/TrainStation/TrainStationViewControls.cs` | Danh sách camera, chuyển góc, giao diện và các phím tắt. |

Trong **Interface — Canvas éditable**, danh mục **Camera catalogue — all saved observation views** chứa sẵn 15 nút. Thanh **04 · Navigation entre points de vue → Camera controls — saved mouse and CCTV navigation** chứa các nút chuyển góc. Có thể bật danh mục trong Hierarchy để chỉnh bố cục, tên và màu mà không chạy Play. Các callback Button, nhãn tên và tham chiếu camera được lưu cùng scene.

Khi dùng prefab CCTV ở một vị trí mới, đặt bản đế sát kết cấu, gán `mountAnchor`, hướng đầu camera về vùng cần theo dõi và kiểm tra thang đo quang học sau khi parent vào kết cấu có scale khác 1. Thêm camera vào `Viewpoints`, thêm tên vào `View Names` và gán nút chọn tương ứng. Runtime không tự sinh camera hoặc nút chọn mới.

## Kiểm tra

Lượt kiểm tra ngày 28/09/2026 đã đạt, gồm chuyển đủ 15 camera, sự kiện con lăn qua Input System, các hàm xử lý orbit/pan/zoom, UI, va chạm mái và hoạt động liên tục của hành khách/tàu. Xem [báo cáo nghiệm thu](../../StationFlowLab_Archive_20261020/Reports/TrainStationCameras/Acceptance.vi.md) để biết số liệu và phạm vi kiểm tra.

Menu **Efrei Research Lab → Train Station → Cameras → Validate saved mountings and coverage** kiểm tra tham chiếu, neo vật lý, tỷ lệ, các điểm trong khung và đường nhìn. Kết quả ghi vào `Reports/TrainStationCameras/authoring-validation.json`.

Menu **Efrei Research Lab → Train Station Cameras → Validate saved cameras** kiểm tra danh mục, UI và tham chiếu cùng các hệ thống khác trong scene. Kết quả ghi vào `Reports/TrainStationCameras/saved-scene-check.json`.

Menu **Efrei Research Lab → Train Station Cameras → Check actual Play mode camera controls** kiểm tra chuyển góc, camera cố định, orbit/pan/zoom, giới hạn, reset, duy trì hoạt động nhân vật/tàu và số đối tượng trong Play. Kết quả ghi vào `Reports/TrainStationCameras/play-mode-check.json`. Đọc `success`, thời điểm hoàn tất và các trường chi tiết của lần chạy; sự có mặt của công cụ kiểm tra không tự xác nhận kết quả đã đạt.
