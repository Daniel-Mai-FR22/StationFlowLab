# StationFlowLab — GareFlowDemo

Dự án Unity mang tên **StationFlowLab**. Bản chạy Windows của mô phỏng nhà ga mang tên **GareFlowDemo**; phiên bản và số build được ghi trong tên thư mục bàn giao cùng `build-info.json`. Tên dự án nguồn và tên ứng dụng được tách riêng; scene và định danh dữ liệu `TrainStationDaily` được giữ ổn định.

## Mở bản mô phỏng nhà ga hiện tại

1. Trong Unity Hub, thêm thư mục `StationFlowLab` bằng Unity **6000.5.9f1**, rồi mở scene đã lưu `Assets/TrainStation/Scenes/TrainStationDaily.unity` và nhấn Play.
2. Với bản Windows, mở `GareFlowDemo.exe` trong thư mục bàn giao mới trên Desktop. Khi sao chép sang máy khác, giữ toàn bộ thư mục, gồm EXE, `GareFlowDemo_Data`, `UnityPlayer.dll` và các thư viện đi kèm.
3. Bảng **Collecte de données** cho phép chọn video, camera, giờ tàu, profile, quỹ đạo, animation, quyết định và sự kiện trước **Démarrer la collecte**. Chọn **Observer sans enregistrer** để chỉ quan sát. Kết thúc bằng **Arrêter et finaliser les fichiers** để hoàn tất ghi dữ liệu.

Scene Daily có **16 camera CCTV cố định**, gồm bốn camera **HALL-01–HALL-04** nhìn về đài phun nước từ bốn góc khác nhau, cùng một camera orbit để quan sát. Khi cấu hình thu thập lần đầu, bốn camera Hall được chọn sẵn; vẫn có thể chọn riêng các camera khác hoặc **Toutes**.

Dropdown video chọn **480p (854 × 480), 720p (1280 × 720) hoặc HD (1920 × 1080)**, mặc định **720p**. Không còn lựa chọn FPS cố định: hệ thống thử ghi ở mỗi frame Unity thực sự chạy, còn FPS ghi được thay đổi theo thời gian. Giao diện hiển thị FPS game và FPS video thực tế của từng camera trên cửa sổ trượt cố định một giây. `sync.jsonl` lưu các mẫu `measured_frame_rates`; mỗi dòng `frames.jsonl` lưu khoảng cách thời gian thực, FPS tức thời và FPS trượt của ảnh thực sự ghi được. Các luồng dữ liệu khác giữ horodatage riêng; đo nhịp dữ liệu theo timestamp, không suy từ FPS video. AVI/MJPEG vẫn có tốc độ phát danh nghĩa cố định (`nominalPlaybackFps`, trường cũ `videoFps`); timestamp đi kèm mới mô tả thời điểm thu thập biến thiên, không phải video VFR.

Mỗi lần chạy dùng ngày địa phương hiện tại và một graine mới để sinh lịch tàu cùng hồ sơ hành khách theo quy tắc. Các tùy chọn thu thập được giữ nguyên trong một lượt ghi; pause và tốc độ mô phỏng được lưu cùng horodatage để đồng bộ khi phân tích. Khi bắt đầu thu thập, tốc độ mô phỏng được đưa về **×1**; các nút **×4 và ×8 bị khóa trong suốt lượt ghi, kể cả khi pause**. Vẫn có thể pause và tiếp tục ở ×1. Các điều khiển tốc độ được mở lại sau **Arrêter et finaliser les fichiers**.

Dữ liệu nằm trong `Captures/TrainStationDaily/<runId>/` cạnh EXE; trong Editor, đường dẫn này nằm ở gốc `StationFlowLab`. `manifest.json` ghi tên sản phẩm, phiên bản và Build GUID, còn `captureStamp.runId` nhận diện lượt thu thập. Tài liệu cấu trúc dữ liệu bằng tiếng Pháp nằm trong `GareFlowDemo_Data/StreamingAssets/TrainStationData/DataSchema.fr.html` và được sao chép vào từng lượt ghi. [Bản tài liệu trong dự án](Assets/StreamingAssets/TrainStationData/DataSchema.fr.html).

Các lượt ghi mới dùng manifest **`stationflow.station_capture.v2`**, layout **`per_agent.v1`**. `agents/index.json` ánh xạ từng `agentId` của chuyến hành khách đã lên kế hoạch sang thư mục an toàn tương đối với gốc lượt ghi. Dữ liệu cá nhân nằm trong `agents/<safeDir>/`: `profile.json` chứa một profile và một journey; các file `trajectories.jsonl`, `animations.jsonl`, `decisions.jsonl`, `events.jsonl` được tạo khi có dữ liệu tương ứng. Khi bật profile, mọi chuyến đã lên kế hoạch đều có `profile.json`; nếu không, một mục index có thể chưa có thư mục. Tắt tất cả tùy chọn dữ liệu avatar thì không tạo `agents/` hay index. `actorSlotId` là slot hiển thị tái sử dụng, **không dùng để gộp danh tính**; luôn đọc `agentId` gốc trong index. Horloge, scenario, giờ tàu, trạng thái tàu và video vẫn dùng chung ở gốc; `events.jsonl` gốc chỉ giữ sự kiện nhà ga có `agentId` rỗng. Scenario Daily v4 không lặp lại các mảng profile/journey. Các jointure `(runId, syncSampleId)`, FPS đo thực và khóa tốc độ ×1 giữ nguyên. Các lượt ghi cũ và log ngoài Daily giữ cấu trúc lịch sử của chúng.


---

Các phần dưới đây là tài liệu lịch sử của những scene và bản demo trước trong cùng dự án; các đường dẫn và kết quả nghiệm thu tại đó mô tả đúng phiên bản được nêu, không thay thế hướng dẫn GareFlowDemo ở trên.

# StationFlow — Junction Lab

## Human Animation — thư viện Mixamo

**Efrei Research Lab → Human Animation → Open animation lab** mở `MixamoAnimationLab.unity`: 19 clip Mixamo dùng chung trên X Bot và một Humanoid có tỷ lệ khác. Có FBX gốc, Resources, Animator Controller, Avatar, prefab nhân vật/workbench và preview trong Inspector ở Edit mode. TrainStation dùng Male Standard Walk làm dáng đi mặc định. [Hướng dẫn và cách tái sử dụng](Documentation/HumanAnimation.vi.md).

## TrainStation — nhà ga và tương tác xã hội

Scene mới: `Assets/TrainStation/Scenes/TrainStation.unity`. Mở bằng **Efrei Research Lab → Train Station → Open TrainStation**. Nhà ga Pháp hư cấu Gare de Valmont gồm sảnh, cửa hàng/dịch vụ, 4 ke và 8 đường ray, quảng trường và hạ tầng đón trả khách. Toàn bộ cảnh và Canvas UI được lưu sẵn, có thể chỉnh trong Hierarchy/Inspector mà không cần Play. [Hướng dẫn TrainStation](Documentation/TrainStation.vi.md).

Cấu hình quần thể mới có **300 hành khách**: 18 người ngồi, 72 người trong 24 nhóm trò chuyện, 90 người trong 30 nhóm đi bộ và 120 người đi một mình, cùng 7 nhân viên accueil, bán hàng và vệ sinh. Mọi loại hành khách thử sự kiện ngẫu nhiên sau 12–35 giây khi rảnh, ghé accueil/cửa tiệm, xếp hàng theo lúc thực sự đến rồi tiếp tục hoạt động. Mỗi quầy điều tiết lượt ghé tự động ở mức sức chứa hàng cộng thêm 4 người, không giữ vị trí trước cho người ở xa. Toàn bộ hành khách không mang hành lý; vali đã được xóa khỏi scene và các prefab. Prefab, nhân vật và các tuyến đều lưu sẵn trong Editor. NavMesh chỉ cho đi trên sàn, loại trừ toàn bộ 71 băng ghế và 21 bồn cây; chính sách này được giữ khi bake lại. [Hướng dẫn hành vi, Prefabs và sự kiện](Documentation/TrainStation-Social.vi.md).

[Báo cáo nghiệm thu 300 hành khách](../StationFlowLab_Archive_20261020/Reports/TrainStationSocial/Dense-Crowd-Acceptance.vi.md) ghi kết quả kiểm thử và các giới hạn còn lại. Báo cáo quần thể 60 người, gỡ hành lý và thay clip đi bộ trước đó là bằng chứng lịch sử; không dùng chúng để kết luận lỗi chân hoặc khả năng chen tránh của quần thể mới đã được giải quyết.

Các đoàn tàu đến/đi theo lịch mô phỏng, đồng bộ bảng giờ trong sảnh, tại ke và đồng hồ. [Hướng dẫn tàu và lịch trình](Documentation/TrainStation-Rail.vi.md).

**Thí nghiệm quyết định hành khách:** mở `Assets/TrainStation/Scenes/TrainStationDecisionLab.unity` hoặc **Efrei Research Lab → Train Station → Decision Lab → Open experiment** rồi Play. Bản sao riêng dùng 24 hành khách có nhu cầu, thông tin chuyến tàu và ngân sách thời gian để thử chính sách Markov có điều kiện với thời lượng hoạt động; có sự kiện tàu trễ/hủy, bảng theo dõi và log quyết định/quỹ đạo. [Hướng dẫn Decision Lab](Documentation/TrainStation-DecisionLab.vi.md).

Quan sát bằng một camera sảnh điều khiển bằng chuột và 14 camera CCTV gắn vào tường, cột hoặc xà, theo dõi accueil, cửa tiệm, sảnh nối ke, quảng trường và bốn ke ga. Toàn bộ 15 camera, thân gá và UI chọn góc được lưu sẵn trong Editor. [Hướng dẫn camera và điều khiển](Documentation/TrainStation-Cameras.vi.md).

Demo Unity mô phỏng ngã tư châu Âu để trình bày quy trình sinh dữ liệu chuyển động: cảnh 3D → hành vi theo tín hiệu → camera pinhole/fisheye → PNG và nhãn đồng bộ → kiểm thử → video trình diễn.

## Mở nhanh

- Bản bàn giao Windows: **`Builds/JunctionLab/JunctionLab.exe`**. Giữ toàn bộ thư mục cạnh EXE khi sao chép.
- Video đặt hai camera cạnh nhau: **`Presentation/JunctionLab-comparison.mp4`**.
- Trong Unity **6000.5.9f1**: **StationFlow → Junction Lab → Open demo scene**, rồi **Play**. Cảnh được dựng bằng mã khi chạy.
- `Space`: pause; `1/2/3`: chọn camera; `R`: ghi/dừng ghi; `F1`: ẩn HUD; `F12`: lưu ảnh trình diễn; `Esc`: thoát player.

## Đầu ra

| Tài liệu / thư mục | Nội dung |
|---|---|
| [Phân tích, tiêu chí, kỹ năng và quy trình](Documentation/Requirements-and-Evaluation.vi.md) | Bài toán, ma trận nghiệm thu demo/sản xuất, năng lực kỹ thuật, phát triển và QA |
| [Hướng dẫn sử dụng](Documentation/Quickstart.vi.md) | Chạy, cấu hình, điều khiển, ghi dữ liệu và đọc nhãn |
| [Bản tóm tắt cho StationFlow](Documentation/Demo-Brief.vi.md) | Kịch bản trình diễn và kế hoạch nâng cấp |
| [Dataset card](Documentation/Dataset-Card.vi.md) | Phạm vi dữ liệu, cấu trúc, giới hạn và cách sử dụng |
| [Báo cáo nghiệm thu](../StationFlowLab_Archive_20261020/Reports/Acceptance.vi.md) | Kết quả đã đo, bằng chứng, những mục chưa đạt/chưa kiểm |
| `Captures/Presentation/` | 900 cặp ảnh trong 90 giây, JSONL, calibration, phiên bản mã và báo cáo lần chạy |
| `Presentation/` | Ba MP4 H.264, manifest video và ảnh trình diễn |
| `Reports/` | Kiểm thử simulation, validation dữ liệu, overlay nhãn và build report |
| [Nguồn tài nguyên](Documentation/ThirdParty-Assets.md) | CC0 Poly Haven, nguồn và checksum |

## Thiết kế

24 người, 12 xe; làn xe đi thẳng bên phải; pha Bắc–Nam, Đông–Tây, pha riêng cho người đi bộ và clearance có kiểm tra nút trống. Lõi C# độc lập chạy ở 60 Hz, seed tái lập. Người có động tác theo quãng đường và hàng chờ; xe tăng/giảm tốc, giữ khoảng cách. Xe tái xuất hiện nhận ID track mới.

Pinhole 960×540, FOV dọc 70°; fisheye 640×640, equidistant 180°, dựng từ sáu camera 90° cùng tâm quang học. Ghi 10 fps theo thời gian mô phỏng cố định. Ảnh gốc không chứa HUD. Nhãn gồm lớp, ID, vị trí/vận tốc, trạng thái và hộp chiếu xấp xỉ. **Nhãn không đo che khuất hay cung cấp mask segmentation.**

Mã chính nằm trong `Assets/StationFlowDemo/`: `Simulation` (hành vi), `Runtime` (cảnh, actor, UI), `Sensors` (camera/nhãn), `Editor` (scene/build), `Art` (texture CC0). `Resources/build-info.json` ghi SHA256 nguồn của bản build; được sao chép vào từng lượt ghi.

## Kiểm thử và tái tạo

```powershell
dotnet run --project Tests/Simulation/SimulationTests.csproj -c Release
python Tests/validate_capture.py Captures/Presentation --require-sensor-self-test
python Tools/make_videos.py Captures/Presentation --ffmpeg <duong-dan-ffmpeg.exe>
```

Python cần Pillow. `Tools/PythonPackages` chứa FFmpeg tải cho máy phát triển này; player không phụ thuộc Python hoặc FFmpeg. Menu build Unity tạo bản phát triển ở `Builds/Windows`; bản bàn giao được kiểm thử riêng nằm ở `Builds/JunctionLab`.

## Giới hạn chất lượng

Đây là demo kỹ thuật hoàn chỉnh cho luồng sinh và kiểm tra dữ liệu. **Nhân vật, xe và animation vẫn ở mức minh họa, chưa photoreal**. Chưa có rẽ xe, xe đạp, nhóm người phức tạp, thời tiết đa dạng, mô hình ống kính đo từ camera thật hoặc huấn luyện/đánh giá trên dữ liệu thật. Các tiêu chí sản xuất và cách kiểm chứng được ghi riêng trong tài liệu, không suy ra hiệu quả AI từ việc demo chạy đúng.
