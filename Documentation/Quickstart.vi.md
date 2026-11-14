# Chạy và ghi dữ liệu — Junction Lab

Demo kỹ thuật Unity mô phỏng ngã tư đô thị châu Âu, giao thông bên phải. Nhân vật, xe và động tác được tạo bằng mã; vật liệu đường, vỉa hè và gạch dùng texture CC0. Đây là cảnh minh họa có kiểm soát, chưa phải bộ nhân vật photoreal hoặc dữ liệu đã chứng minh hiệu quả huấn luyện trên video thật.

## Chạy trong Unity

1. Mở thư mục dự án `StationFlowLab` bằng Unity **6000.5.9f1**. Dự án sử dụng URP; để build Windows cần module Windows Build Support của phiên bản này.
2. Chờ Unity import và compile. Chọn **StationFlow → Junction Lab → Open demo scene**. Menu sẽ tạo scene nếu chưa có rồi mở `Assets/StationFlowDemo/Scenes/JunctionLab.unity`.
3. Nhấn **Play**, chọn thẻ Game. Cảnh và camera được dựng khi chạy; Edit mode không hiển thị toàn bộ thành phố được sinh ở runtime.
4. Mặc định: seed 42, 24 người, 12 xe, simulation 60 Hz, ghi 10 fps. Đèn người đi bộ bắt đầu sau hai pha xe; hãy cho cảnh chạy qua ít nhất khoảng 54 giây mô phỏng để thấy người bắt đầu qua đường. Khoảng clearance có thể kéo dài để chờ đối tượng hoàn tất.

Các trường `Seed`, `Pedestrians`, `Vehicles`, `CaptureFps` nằm trên component `DemoController` của scene. Chỉnh trước khi Play; nếu chỉnh seed/số lượng khi đang chạy, dùng **REPLAY SEED** để khởi tạo lại dân số. FPS ghi phải là ước của 60: 1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30 hoặc 60.

## Chạy bản Windows

Bản Windows bàn giao nằm tại `Builds/JunctionLab/JunctionLab.exe`. Giữ nguyên toàn bộ thư mục `Builds/JunctionLab` khi sao chép sang máy khác; không chỉ sao chép file EXE. Để tự build lại từ Editor, chọn **StationFlow → Junction Lab → Build Windows demo**; menu này ghi bản mới vào `Builds/Windows/JunctionLab.exe`. Kết quả build từ menu nằm trong `BuildRequests/build-report.json`.

Ba video **90 giây / 900 frame / 10 fps** có sẵn trong `Presentation`: `JunctionLab-pinhole.mp4`, `JunctionLab-fisheye.mp4` và `JunctionLab-comparison.mp4`. Video comparison tiện mở đầu buổi thuyết trình; hai video riêng dùng để xem từng sensor. `video-manifest.json` ghi nguồn, codec H.264/yuv420p, thời lượng và kích thước file.

## Điều khiển

| Phím hoặc nút | Chức năng |
|---|---|
| `1` / OVERVIEW | Toàn cảnh ngã tư |
| `2` / PINHOLE | Xem toàn khung ảnh camera phối cảnh |
| `3` / FISHEYE | Xem toàn khung ảnh fisheye |
| `Space` / PAUSE–RESUME | Dừng/tiếp tục simulation; khi pause thì không ghi frame mới |
| `R` / RECORD SEQUENCE–STOP & SAVE | Bắt đầu hoặc dừng ghi đồng bộ hai camera |
| `F1` | Ẩn/hiện giao diện, vẫn giữ hình camera đang chọn |
| `F12` | Lưu ảnh màn hình hiện tại thành `Presentation/JunctionLab.png`; trong player thư mục Presentation nằm cạnh EXE, trong Editor nằm ở gốc dự án |
| SPEED 1× / 3× | Đổi tốc độ xem trước; lúc ghi, bước thời gian do FPS ghi quyết định |
| REPLAY SEED | Chạy lại từ đầu với seed hiện tại; dừng lượt ghi nếu đang mở |
| NEW SEED | Tăng seed rồi chạy lại từ đầu |
| `Esc` | Thoát bản Windows; trong Editor dùng nút Play để dừng |

`Vehicle red entries` hiển thị audit xe vào khi bị cấm; báo cáo có riêng `redLightViolations` cho xe và `pedestrianRedLightViolations` cho người đi bộ. `Body overlaps` đếm lần bắt đầu chồng hình kiểm tra 2D của các đối tượng, không phải tiếp xúc mesh hoặc va chạm vật lý Unity. `Completed trips` cộng các lượt xe/người hoàn tất qua nút. Các chỉ số live được tính từ lần reset simulation gần nhất; bộ đếm trong `run-report.json` chỉ tính **khoảng capture**, đã trừ các sự kiện trước khi bắt đầu ghi/warmup.

## Ghi một lượt mới

Trong Unity hoặc player, chọn **RECORD SEQUENCE**; để chạy qua các pha mong muốn; chọn **STOP & SAVE**. Ảnh RGB không chứa HUD hoặc hộp nhãn dù giao diện vẫn hiển thị khi xem demo.

- Trong Editor: dữ liệu được đặt dưới `StationFlowLab/Captures/<timestamp>_seed<seed>/`.
- Trong player bàn giao: dữ liệu được đặt dưới thư mục `Captures` cạnh EXE, tức `Builds/JunctionLab/Captures/<timestamp>_seed<seed>/`.
- Khi dùng tham số `-stationflowCapture`, thư mục do tham số chỉ định. Ví dụ dưới đây tạo lượt **mới** ở `Captures/Presentation_New`; không ghi đè một bộ đã tồn tại.

Mở PowerShell tại thư mục dự án, sau khi có bản build:

```powershell
& '.\Builds\JunctionLab\JunctionLab.exe' `
  -stationflowCapture (Join-Path (Get-Location) 'Captures\Presentation_New') `
  -captureFps 10 -frames 900 -warmup 40 `
  -validateSensors true -exitAfterCapture true
```

Lệnh dùng cấu hình của bộ mẫu: 900 cặp frame, tương ứng 90 giây video ở 10 fps, warmup 40 giây mô phỏng. Frame đầu ở t≈40,0 và frame cuối t≈129,9; khoảng cách hai timestamp là 89,9 giây. Camera và gait được cập nhật ở cùng trạng thái 60 Hz trước mỗi lần lấy mẫu. Thời gian ghi trên máy có thể dài hoặc ngắn hơn thời gian mô phỏng. `-warmup` tính bằng giây mô phỏng nguyên trước frame đầu. Lượt tự động chụp trạng thái đầu ngay khi mở; lượt ghi bằng nút bắt đầu ở bước lấy mẫu kế tiếp.

Bộ minh họa bàn giao nằm tại `Captures/Presentation`: **900 cặp frame, 1.800 PNG**, seed 42, 24 người và 12 xe. Báo cáo đã ghi nhận **41 lượt qua nút hoàn tất** trong khoảng capture, 0 xe vào khi bị cấm, 0 người vào khi bị cấm và 0 sự kiện chồng hình kiểm tra. Bộ kiểm dữ liệu đã giải mã đủ 1.800 PNG, xác nhận đủ chín pha tín hiệu và kết luận PASS với 0 lỗi/0 cảnh báo. Chi tiết phạm vi đo: `Dataset-Card.vi.md`, `Reports/capture-validation.json` và `run-report.json` của lượt mẫu.

Không dùng `-nographics` để lấy RGB. Không dùng lại thư mục đã chứa `frames.jsonl` hoặc `calibration.json`; bộ ghi chủ động từ chối để giữ dữ liệu trước đó. `failure.txt` trong thư mục đầu ra ghi lỗi nếu lượt tự động thất bại.

## Các file trong một lượt

```text
<capture>/
  calibration.json
  build-info.json
  frames.jsonl
  capture_summary.json
  run-report.json
  pinhole/frame_000000.png ...
  fisheye/frame_000000.png ...
  sensor-validation/                 # nếu dùng -validateSensors true
    calibration_markers_pinhole.png
    calibration_markers_fisheye.png
    sensor_projection_checks.txt
```

| File | Nội dung và cách dùng |
|---|---|
| `pinhole/*.png` | RGB PNG không mất dữ liệu, 960×540, FOV dọc 70° |
| `fisheye/*.png` | RGB PNG 640×640, equidistant 180°, phần ngoài hình tròn màu đen |
| `frames.jsonl` | Một object JSON mỗi dòng; mỗi dòng chứa cả hai camera và cùng timestamp |
| `calibration.json` | Mô hình camera, kích thước, nội/ngoại tại, hệ trục, phép chiếu, miền hợp lệ và giới hạn nhãn |
| `capture_summary.json` | Tổng frame đã ghi, seed, FPS, timestamp cuối, thời điểm hoàn tất UTC |
| `run-report.json` | Cấu hình demo, GPU/Unity, khoảng thời gian và bộ đếm audit riêng cho khoảng capture |
| `build-info.json` | Thông tin phiên bản/source của build đã tạo dữ liệu, khi được đóng gói cùng player |
| `sensor-validation/*` | Kiểm toán học và kiểm marker màu qua GPU; không thay thế hiệu chỉnh một ống kính thật |

Lượt tự động có thể tạo thêm `presentation_2.png` và `presentation_240.png`: đó là ảnh màn hình phục vụ thuyết trình, có thể chứa HUD, tách biệt với RGB cảm biến.

## Đọc nhãn và calibration

Schema mỗi frame là `stationflow.synthetic_traffic.frame.v1`. Các trường chính: `frame`, `timestamp_seconds`, `seed`, `traffic_phase`, `pinhole_rgb`, `fisheye_rgb`, pose camera và `objects`. Mỗi đối tượng có `track_id`, `category` (`pedestrian` hoặc `car`), `behavior_state`, `position_world`, quaternion `rotation_xyzw`, `velocity_world_m_s`, AABB thế giới và phần chiếu `pinhole`/`fisheye`.

`track_id` nhận diện một vòng đời. Xe tái xuất hiện ở đầu tuyến nhận ID mới, tránh nối vị trí cuối tuyến và đầu tuyến thành một track dịch chuyển tức thời. Nhóm người giữ ID khi đi qua đường rồi trở lại ở chu kỳ sau. Danh sách `objects` có cả đối tượng ngoài FOV; phải đọc phần chiếu cho camera được chọn.

Mỗi phần chiếu cung cấp `in_sensor_fov`, `sampled_amodal_bbox_xyxy`, `clipped_bbox_xyxy`, `center_px`, số mẫu chiếu được và `clipped_by_projection_domain`. Hộp dùng thứ tự `[xmin,ymin,xmax,ymax]`, tọa độ gốc biên trên trái ảnh `(0,0)`, tâm pixel `(cột+0,5; hàng+0,5)`. Hộp clipped giới hạn vào khung ảnh; không có hộp hợp lệ khi ngoài trường nhìn. `in_sensor_fov=true` **không có nghĩa đối tượng nhìn thấy**: tòa nhà hoặc người khác vẫn có thể che nó.

Nhãn hiện tại chiếu 98 mẫu trên AABB thế giới bao các renderer của đối tượng. Đây là hộp **amodal xấp xỉ**, không phải hộp tight theo các pixel còn nhìn thấy. `visibility="not_measured"`; không có segmentation mask, tỷ lệ occlusion hoặc depth ground truth. Không diễn giải số mẫu AABB chiếu được thành tỷ lệ nhìn thấy.

Thế giới Unity dùng mét, X phải/Y lên/Z trước. Ma trận `world_to_camera_optical_row_major` có 16 số theo hàng, đưa tọa độ thế giới homogeneous sang hệ camera X phải/Y xuống/Z trước. Quaternion Unity lưu thứ tự x,y,z,w. Với pinhole dùng `K_row_major`; với fisheye dùng `focal_pixels`, tâm ảnh và phương trình `r=f*theta`, không coi fisheye như ảnh pinhole chỉ có FOV lớn hơn. Hai sensor cùng tâm quang học `(12,5; 8; −12,5)` mét trong demo, khác mô hình chiếu. Camera OVERVIEW phục vụ giao diện đặt riêng tại `(13,5; 25; −13,5)` mét và không phải một luồng dataset. Fisheye được ghép từ sáu camera 90° đồng vị trí, không phải hiệu ứng làm cong từ ảnh perspective hẹp.

Có thể đọc nhanh dòng đầu bằng PowerShell:

```powershell
$frame = Get-Content -LiteralPath '.\Captures\Presentation\frames.jsonl' -TotalCount 1 | ConvertFrom-Json
$frame | Select-Object frame, timestamp_seconds, traffic_phase
$frame.objects | Select-Object track_id, category, behavior_state
```

## Kịch bản trình diễn và phạm vi

Cho xem toàn cảnh xe chờ/đi, sau đó đến pha người đi bộ; chuyển `2` rồi `3` để so sánh camera; dùng `F1` để xem ảnh sạch; mở một JSONL và một cặp PNG để giải thích nhãn và timestamp. Dùng file mẫu để dự phòng tốc độ ghi trực tiếp của máy trình bày.

Xe chỉ đi thẳng trên bốn tuyến, chưa có rẽ hoặc nhường xe khi rẽ. Người đi theo tám làn qua đường với khác biệt tốc độ, chờ theo đèn, tránh nhau cục bộ; cùng nhóm người quay lại ở chu kỳ sau. Chuyển động dùng các khớp thủ tục, chưa có mocap, foot IK hoặc character scan. Sensor chưa có noise, motion blur hay rolling shutter của camera thật. Cấu hình môi trường là một cảnh ban ngày cố định; đổi seed chủ yếu đổi đối tượng/hành vi, không tạo một địa điểm hoặc điều kiện ánh sáng mới.

Các kiểm tra đã thực hiện gồm 4.086.453 assertion bất biến simulation, 211 kiểm toán học camera, 14 kiểm marker qua GPU và kiểm đủ 1.800 ảnh mẫu. Đọc các log trong `Reports` và `sensor-validation` cùng `Requirements-and-Evaluation.vi.md` để hiểu phạm vi; những kết quả này không chứng minh chất lượng photoreal, occlusion theo pixel hoặc hiệu quả mô hình AI trên video thật.
