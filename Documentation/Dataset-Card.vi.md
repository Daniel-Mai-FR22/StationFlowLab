# Dataset card — Junction Lab

**Mục đích:** dữ liệu mẫu cho demo kỹ thuật và thuyết trình về bộ sinh dữ liệu chuyển động bằng Unity. Nhiệm vụ minh họa là detection/tracking người và xe qua ngã tư. Bộ mẫu chưa được dùng để huấn luyện hoặc đánh giá AI trên video thật, và chưa được công nhận là dữ liệu sản xuất.

Ngày lập: 21/09/2026. Tác nhân đều là mô hình thủ tục của dự án; không thu hình người thật hoặc lấy video đường phố. Vật liệu asphalt, pavement và brick dùng texture CC0 từ Poly Haven, với nguồn và hash trong `asset-manifest.json`.

## Bộ dữ liệu và nguồn chuẩn

Thư mục bàn giao là `Captures/Presentation`; video dẫn xuất đặt riêng trong `Presentation`. Lượt mẫu có **900 cặp frame / 1.800 PNG**, lấy mẫu 10 fps, tương ứng 90 giây video. Frame đầu t=40.00000208616257 và frame cuối t=129.90000677481295 giây mô phỏng, sau warmup 40 giây. Có **41 lượt qua nút hoàn tất, 0 xe vào khi bị cấm, 0 người vào khi bị cấm, 0 sự kiện chồng hình kiểm tra** trong khoảng capture.

File `capture_summary.json` ghi tổng số frame và FPS, `run-report.json` ghi khoảng thời gian mô phỏng cùng bộ đếm của khoảng capture, `build-info.json` ghi thông tin build/source đi cùng player. `Reports/capture-validation.json` là báo cáo kiểm toàn bộ dữ liệu. Các giá trị float có sai số tích lũy nhỏ của bước 1/60 giây; ví dụ t=40 giây có thể ghi là 40.000002. Khoảng cách timestamp đầu–cuối là 89,9 giây; thời lượng phát 900 frame ở 10 fps là 90 giây.

Cấu hình cảnh gồm seed 42, 24 người và 12 xe, simulation 60 Hz và lấy mẫu 10 fps. Tất cả camera cùng lượt chạy lấy trạng thái đồng bộ. PNG là nguồn dữ liệu; ba MP4 H.264/yuv420p trong `Presentation/JunctionLab-{pinhole,fisheye,comparison}.mp4` đều có 900 frame và thời lượng 90,0 giây, theo `Presentation/video-manifest.json`. Video comparison có tiêu đề/ghép bố cục nên không phải ảnh đầu vào huấn luyện.

## Phân bố và hành vi

Địa điểm là một ngã tư hư cấu kiểu đô thị châu Âu, xe đi bên phải. Có bốn tuyến xe đi thẳng và tám làn người qua đường. Chu kỳ tín hiệu gồm chín trạng thái: `NorthSouthGreen`, `NorthSouthAmber`, `AllRedBeforeEastWest`, `EastWestGreen`, `EastWestAmber`, `AllRedBeforePedestrians`, `PedestrianWalk`, `PedestrianClearance`, `AllRedBeforeNorthSouth`. Khoảng clearance chờ các đối tượng thoát nút, nên độ dài chu kỳ có thể phụ thuộc trạng thái hàng chờ.

Phân loại hiện có `pedestrian` và `car`; không có xe bus, xe máy, xe đạp, động vật hoặc xe chuyên dụng. Có khác biệt tốc độ và màu/hình dáng minh họa, nhưng số mẫu hình cơ thể/xe nhỏ. Seed đổi phân bố tác tử và vận tốc; không tự tạo thêm địa điểm, thời tiết, giờ nắng hoặc camera. Người quay lại qua đường ở chu kỳ sau; xe được tái sinh ở biên tuyến và nhận track ID mới.

## Cảm biến

| Thuộc tính | Pinhole | Fisheye |
|---|---|---|
| Độ phân giải | 960×540 | 640×640 |
| Model | `pinhole` | `equidistant_180` |
| FOV | Dọc 70°, ngang ≈102,448° | 180° theo đường kính |
| Focal | ≈385,600 pixel | ≈203,718 pixel |
| Tâm ảnh | (480;270) | (320;320) |
| Miền hợp lệ | Khung chữ nhật và clipping camera | Hình tròn bán kính 320 pixel; ngoài vòng tròn màu đen |
| Cách render | Một camera perspective | Sáu mặt perspective 90°, mỗi mặt 512×512, remap equidistant bilinear |

Hai sensor cùng vị trí thế giới `(12,5;8;−12,5)` mét, nhìn về `(0;1;0)`. Camera OVERVIEW ở `(13,5;25;−13,5)` là camera giao diện, không phải luồng dataset. Near clip 0,15 m và far clip 180 m; với fisheye, clipping áp dụng trên mặt perspective có thành phần trội, không phải bán kính cầu. Pose và ma trận thực tế luôn lấy từ `calibration.json`/frame metadata.

RGB PNG8 sRGB, không có motion blur, rolling shutter, nhiễu sensor, đo phơi sáng thật hoặc hậu kỳ. Đây là camera lý tưởng; không có hệ số hiệu chỉnh của một ống kính thương mại. Kiểm marker qua GPU xác nhận tính nhất quán của phép chiếu đã triển khai, không thay thế calibration từ ảnh bảng chuẩn thực.

## Schema và hệ tọa độ

`frames.jsonl` dùng schema **`stationflow.synthetic_traffic.frame.v1`**, một object JSON trên mỗi dòng. Mỗi dòng chứa:

| Trường | Ý nghĩa |
|---|---|
| `frame` | Chỉ số frame của lượt capture, bắt đầu 0 |
| `timestamp_seconds` | Thời gian simulation, không phải thời gian thực/camera thật |
| `seed` | Seed của lượt chạy |
| `traffic_phase` | Một trong chín trạng thái tín hiệu |
| `pinhole_rgb`, `fisheye_rgb` | Đường dẫn PNG tương đối so với thư mục capture |
| `camera_position_world`, `camera_rotation_xyzw` | Pose chung của hai sensor |
| `world_to_camera_optical_row_major` | Ma trận homogeneous 4×4 theo hàng |
| `objects` | Danh sách tất cả đối tượng active, kể cả ngoài FOV hoặc bị che |

Đối tượng gồm `track_id`, `category`, `behavior_state`, `position_world`, `rotation_xyzw`, `velocity_world_m_s`, `world_aabb_center`, `world_aabb_size`, và hai object chiếu `pinhole`/`fisheye`. Trạng thái thực tế có `Waiting`, `Approaching`, `Crossing`, `Walking`; lớp xe có thể giữ trạng thái `Crossing` sau khi qua tâm nút trong tuyến đi thẳng. Không dùng behavior_state làm nhãn intent xã hội hoặc vi phạm ngoài định nghĩa simulation.

`track_id` là ID vòng đời, không phải ID mesh. Tái sinh xe ở đầu tuyến kết thúc ID trước và cấp ID mới; không nối hai đầu tuyến vào cùng track. Số track xuyên thời gian có thể lớn hơn số xe đang active. Người giữ ID khi đi và trở lại. ID chỉ duy nhất trong lượt chạy; khi ghép nhiều lượt, dùng `(run_id,track_id)` và tự lấy run_id từ manifest/thư mục vì schema frame v1 không có trường run_id riêng.

Thế giới Unity: X phải, Y lên, Z trước, đơn vị mét. Hệ optical: X phải, Y xuống, Z trước. Quaternion là `[x,y,z,w]`. Tọa độ pixel lấy gốc **biên** trên trái `(0,0)`; tâm pixel là `(column+0.5,row+0.5)`. Các hộp là `[xmin,ymin,xmax,ymax]` theo tọa độ biên, không phải `[x,y,width,height]`.

Calibration dùng schema **`stationflow.synthetic_traffic.sensor.v1`**. Pinhole cung cấp `K_row_major`; fisheye cung cấp `focal_pixels`, `cx`, `cy`, bán kính hợp lệ và công thức `r=f*theta`. Mảng K trống của fisheye không biểu thị camera thiếu calibration. Sử dụng ma trận world_to_camera để đổi hệ trục trước khi chiếu.

## Ngữ nghĩa hộp và che khuất

Các trường dưới object `pinhole`/`fisheye`:

| Trường | Diễn giải đúng |
|---|---|
| `bounds_method` | `sampled_world_aabb_amodal_approximation` |
| `visibility` | Luôn `not_measured` |
| `sampled_amodal_bbox_xyxy` | Min/max của 98 mẫu trên bề mặt AABB thế giới bao toàn renderer, chưa clip vào ảnh |
| `clipped_bbox_xyxy` | Hộp trên giới hạn vào biên ảnh, có thể trống nếu không giao FOV |
| `center_px` | Phép chiếu tâm AABB, có thể nằm ngoài ảnh hoặc trống khi ngoài miền chiếu |
| `in_sensor_fov` | Hộp mẫu giao khung ảnh; không xác nhận pixel của đối tượng nhìn thấy |
| `clipped_by_projection_domain` | Có mẫu bị loại do miền chiếu/clipping; không phải tỷ lệ occlusion |
| `projected_samples`, `total_samples` | Số điểm hình học chiếu được và số mẫu, không phải số pixel |

Hộp này là **amodal xấp xỉ**, có thể chứa vùng trống, đối tượng bị che hoàn toàn hoặc vùng ngoài vòng tròn fisheye ở góc hộp. Không có instance/semantic mask, depth, visible fraction, skeleton keypoints hoặc hộp tight theo phần còn nhìn thấy. Một object bị tòa nhà che vẫn có thể có `in_sensor_fov=true`. Không đánh giá detector như thể đây là nhãn visibility hoàn chỉnh.

## Kiểm tra đã thực hiện và giới hạn kết luận

Kiểm simulation có **4.086.453 assertion bất biến**: baseline 600 giây, replay 180 giây cùng seed/timestep cho trạng thái bằng nhau, năm seed chạy 900 giây, các cấu hình mật độ/timestep và audit vòng đời track. Chi tiết ở `Reports/simulation-tests.txt`. Số assertion là số phép kiểm trong các bước mô phỏng, không phải số kịch bản độc lập.

Kiểm sensor có **211 kiểm toán học, 14 kiểm marker màu qua GPU** và kiểm vùng đen ngoài vòng fisheye. Thông tin chi tiết nằm trong `sensor-validation/sensor_projection_checks.txt`. Kiểm này không đo RMS hiệu chỉnh ống kính thật và không chứng minh nhãn pixel có IoU chính xác.

Validator dữ liệu của bộ bàn giao kết luận **PASS, 0 lỗi, 0 cảnh báo**; giải mã đủ 900 ảnh pinhole và 900 ảnh fisheye, 900 ảnh riêng biệt ở mỗi sensor. Có 36 object active mỗi frame và 56 track có chuyển động xuyên lượt; số track lớn hơn số đối tượng đồng thời vì xe có vòng đời mới khi tái sinh. Trường `in_fov_object_count_range` là 26–32 ở pinhole, 31–36 ở fisheye; đây không phải số đối tượng nhìn thấy.

Tất cả chín pha xuất hiện trong lượt 900 frame:

| Pha | Số frame |
|---|---:|
| EastWestGreen | 311 |
| EastWestAmber | 41 |
| AllRedBeforePedestrians | 20 |
| PedestrianWalk | 121 |
| PedestrianClearance | 116 |
| AllRedBeforeNorthSouth | 20 |
| NorthSouthGreen | 220 |
| NorthSouthAmber | 30 |
| AllRedBeforeEastWest | 21 |

Validator kiểm số ảnh, giải mã PNG, ảnh không đồng nhất/trống, timestamp, metadata, phép chiếu tâm, chuyển động, ID và miền hộp. Báo cáo không kiểm độ tự nhiên bằng quan sát người thật hoặc chất lượng nhãn nhìn thấy theo pixel. Đọc `Reports/capture-validation.json` để giữ đúng phạm vi kết luận.

Các bộ đếm `redLightViolations`, `pedestrianRedLightViolations`, `collisions`, `completedCrossings` trong run-report là **delta trong khoảng capture**, đã loại warmup. Collision là phát hiện các cặp hình 2D bắt đầu chồng nhau: xe dùng hình chữ nhật trên XZ, người dùng đĩa. Không phải mesh collider, mô phỏng lực va chạm hoặc kiểm toàn bộ chi tiết quần áo/tay/chân.

## Sử dụng phù hợp và phân chia dữ liệu

Phù hợp: trình bày pipeline Unity→RGB+nhãn, thử parser và visualization, kiểm camera, nghiên cứu cách tổ chức dữ liệu tái lập. Chưa phù hợp để khẳng định độ chính xác detector trên đường phố thật hoặc thay thế tập test camera thực.

Bộ mẫu là một lượt liên tục của một cảnh, **chưa chia train/validation/test**. Giữ cả lượt và cả hai camera trong cùng partition. Không chia ngẫu nhiên các frame kề nhau; không dùng ảnh perspective của cùng thời điểm để train rồi coi ảnh fisheye tương ứng là test độc lập. Khi mở rộng, chia theo lượt, asset identity, địa điểm và phiên quay; giữ real-test tách biệt với các vòng chọn cấu hình synthetic.

Rủi ro sai lệch phân bố cụ thể: nhân vật thủ tục và gait chưa photoreal; ít mẫu hình cơ thể; một điều kiện ánh sáng; chỉ xe con đi thẳng; không có tình huống vi phạm được chủ động tạo; camera lý tưởng; quỹ đạo qua lại lặp; biên sinh/hủy phương tiện là quy ước simulation. Những hạn chế này cần được giải quyết bằng asset/animation phù hợp, segmentation chính xác, calibration camera thật và đánh giá synthetic→real.

## Nguồn tài nguyên và phiên bản

`asset-manifest.json` lưu URL, tác giả, CC0, ngày tải, MD5 nguồn đã kiểm và SHA256 của chín texture. Giấy phép này áp dụng cho tài nguyên Poly Haven; không tự mở rộng thành giấy phép cho mọi thành phần phần mềm. Demo không tích hợp Mixamo hoặc Unity Asset Store content. Nguồn/điều khoản được trình bày trong `ThirdParty-Assets.md` và `Requirements-and-Evaluation.vi.md`.

Khi tái xuất, giữ build-info, calibration, seed, FPS và các log cùng dữ liệu; không ghi đè lượt cũ. Chế độ ghi cố định giúp tái lập trạng thái trong cùng build, nhưng không hứa PNG giống từng bit giữa các phiên bản Unity, GPU hoặc driver khác nhau.
