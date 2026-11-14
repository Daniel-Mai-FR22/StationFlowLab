# StationFlow — Demo dữ liệu chuyển động tại ngã tư

Ngày lập: 21/09/2026. Bối cảnh đã chốt: đô thị châu Âu, giao thông bên phải. Đầu ra ưu tiên: demo kỹ thuật chạy được và dữ liệu mẫu để thuyết trình. Các số ở cột **mục tiêu** dưới đây là ngưỡng nghiệm thu đề xuất, không phải kết quả đã đo. Kết quả chạy thực tế phải lấy từ báo cáo kiểm thử và tập xuất dữ liệu của bản bàn giao.

## 1. Bài toán và phạm vi

Demo minh họa một quy trình sinh dữ liệu hình ảnh có kiểm soát: dựng cảnh có kích thước thực → điều khiển người/xe theo tín hiệu → quan sát đồng thời bằng camera phối cảnh và fisheye → xuất ảnh cùng nhãn, thời gian và thông số camera → kiểm tra dữ liệu. Nó chứng minh khả năng lập trình Unity phục vụ nhóm dữ liệu; hiệu quả huấn luyện trên video thật là một bước nghiệm thu riêng.

Ngã tư hư cấu có bốn nhánh, làn xe đi bên phải, vạch dừng, vạch qua đường, vỉa hè và mặt tiền kiến trúc đô thị. Đơn vị thế giới: mét; trục Y hướng lên. Camera giám sát đặt cố định, tránh dùng chuyển động điện ảnh làm thay đổi phân bố đầu vào. Cảnh cần nhìn rõ vùng chờ, người bước xuống đường, xe dừng và xe đi qua. Những xe, người và công trình do mã dự án tạo ra là mô hình minh họa kỹ thuật; vật liệu bề mặt chụp thực không đồng nghĩa nhân vật đã đạt độ chân thực sản xuất.

Các lớp cần nhận diện trước hết là `pedestrian` và `car` (xe con thuộc nhóm phương tiện). Thuộc tính hữu ích gồm mã đối tượng ổn định, trạng thái đi/chờ, vị trí, hướng, vận tốc, pha tín hiệu, camera và thời điểm. Không suy diễn danh tính, dân tộc hoặc thuộc tính nhạy cảm từ màu da/mô hình. Chưa cần huấn luyện hay tích hợp một AI mới để chứng minh bộ sinh dữ liệu.

Để kiểm chứng dễ và tránh xung đột khó quan sát, cấu hình đầu tiên dùng pha xe Bắc–Nam, pha xe Đông–Tây và pha riêng cho người đi bộ, có khoảng tất cả đèn đỏ giữa các nhóm. Đây là **quy ước của cảnh**, không phải mô tả rằng mọi ngã tư châu Âu có cùng chương trình tín hiệu. Khi chuyển sang một địa điểm thật, phải đưa hình học, vạch đường và chương trình đèn của địa điểm đó vào cấu hình.

## 2. Yêu cầu hành vi

- Xe đi đúng làn và chiều. Dừng trước vạch khi không được phép vào nút; xe đã vào vùng nút được thoát nút. Không đổi pha sang một luồng xung đột khi nút chưa được giải phóng. Kiểm thử phải phân biệt thời điểm **vào** nút với việc đang tồn tại trong nút khi đèn đổi.
- Người đi trên vỉa hè và qua đường trên vạch. Đèn đi bộ quyết định thời điểm bắt đầu băng qua; người đã bước xuống đường phải có thời gian hoàn tất, không bị đóng băng giữa đường khi đèn đổi.
- Tốc độ mong muốn, khoảng cách xuất hiện và pha động tác khác nhau giữa các cá thể. Hàng chờ có khoảng cách, không chồng người hoặc xe. Người bắt đầu đi lệch nhau một khoảng nhỏ, không đồng loạt như một khối.
- Gia tốc, phanh, quay người và bước chân liên tục. Gait phải theo quãng đường/vận tốc, tránh trượt chân khi đứng chờ và tránh cắt đột ngột từ đi sang đứng.
- Không sinh/hủy hoặc dịch chuyển tức thời trong vùng camera nhìn thấy. Mã theo dõi gắn với một lần tồn tại của đối tượng; tái sử dụng object pool không có nghĩa tái sử dụng danh tính của người/xe mới.

Điểm quan trọng về kỹ thuật: pathfinding chỉ tìm đường. Quyền đi qua nút phải do bộ điều khiển hành vi và tín hiệu quyết định. Với ngã tư nhỏ, đồ thị lối đi/làn xe định trước có lợi thế dễ kiểm thử, dễ tái lập. Với vỉa hè lớn và chướng ngại vật biến đổi, có thể dùng NavMesh hoặc A* để tìm đường, cộng cơ chế tránh va chạm cục bộ. Xe nên chạy trên spline/lane graph có vạch dừng và khoảng cách theo xe trước; dùng NavMeshAgent cho xe không tự tạo ra quy tắc giao thông.

## 3. Camera, nhãn và hợp đồng dữ liệu

Camera phối cảnh cần lưu kích thước ảnh, FOV hoặc ma trận nội tại K, vị trí/quaternion và quy ước đổi hệ trục. Fisheye cần lưu mô hình phép chiếu, FOV đầy đủ, tâm ảnh, bán kính miền hợp lệ và các hệ số méo nếu có. Phép chiếu equidistant lý tưởng dùng bán kính tỷ lệ với góc tới. Bộ fisheye có hệ số thực cần được hiệu chỉnh từ camera mục tiêu; tài liệu OpenCV mô tả phép chiếu và hệ số k1…k4 để đối chiếu. [OpenCV fisheye](https://docs.opencv.org/4.13.0/db/d58/group__calib3d__fisheye.html)

Một bộ ảnh chỉ bị uốn bằng hiệu ứng màn hình từ camera FOV hẹp không tự tái tạo được cảnh bên ngoài FOV gốc. Fisheye cần thu đủ tia nhìn trước khi chiếu; nhãn phải dùng đúng phép chiếu của ảnh cuối, không dùng hộp từ ảnh perspective đem sang.

Mỗi lượt xuất phải lưu tối thiểu:

| Cấp | Trường thông tin bắt buộc |
|---|---|
| Lượt chạy | `run_id`, seed, phiên bản mã/cấu hình, Unity/render pipeline, FPS lấy mẫu, số khung hình, điều kiện cảnh, thời gian bắt đầu, manifest tài nguyên |
| Camera | `camera_id`, kích thước ảnh, projection/model, nội tại, ngoại tại, clipping, hệ trục và đơn vị, miền pixel hợp lệ |
| Khung hình | chỉ số, thời gian mô phỏng, đường dẫn ảnh, pha đèn, danh sách đối tượng |
| Đối tượng | `instance_id`, lớp, trạng thái, vị trí/hướng/vận tốc thế giới, hộp ảnh và quy ước hộp, độ nhìn thấy/che khuất nếu đo được |

Hộp 2D chiếu từ AABB/OBB 3D là **hộp chiếu xấp xỉ**, không phải nhãn pixel nhìn thấy chính xác. Raycast vài điểm chỉ là ước lượng mức thấy được, không phải tỷ lệ che khuất theo diện tích pixel. Các giới hạn này phải ghi trong dataset card. Nhãn dành cho segmentation cần render instance mask qua depth test bằng đúng camera, cùng phép méo, không AA/lọc màu làm lẫn ID. Bounding box nhìn thấy phải lấy min/max từ mask, đồng thời có thể lưu riêng hộp amodal. Hộp ở mép ảnh cần `truncated`; đối tượng ngoài miền fisheye phải được loại hoặc gắn cờ rõ ràng.

RGB xuất ra không được chứa HUD, tên lớp, hộp bao hay seed. Ảnh overlay là đầu ra QA riêng. Hai camera chụp từ cùng trạng thái mô phỏng; dùng số bước cố định, không để tốc độ ghi ổ đĩa thay đổi hành vi. Video MP4 giúp thuyết trình; chuỗi ảnh và metadata là nguồn gốc để kiểm tra vì video có thể nén hoặc thay đổi frame.

## 4. Ma trận nghiệm thu

Ký hiệu: **D** là điều kiện bàn giao demo; **P** là cổng chất lượng cần đạt trước khi dùng dữ liệu sản xuất. “Chưa đo” không được tự chuyển thành “đạt”. Ngưỡng P cần điều chỉnh cùng nhóm ML theo camera và nhiệm vụ thật.

| ID | Cấp | Mục tiêu nghiệm thu | Cách đo và bằng chứng |
|---|---|---|---|
| D01 | D | Mở scene/chạy player, không lỗi biên dịch hoặc exception trong lượt mẫu | Log Unity, build và lần chạy player |
| D02 | D | Đủ hai luồng camera; ít nhất một chu kỳ tín hiệu có người/xe chờ rồi di chuyển | Ảnh/video liên tục và timeline pha; ghi đúng thời lượng thực tế đã xuất |
| D03 | D | 0 sự kiện vào nút khi bị cấm; 0 pha cấp quyền cho luồng xung đột | Event log vạch dừng/vạch qua đường, kiểm tra ở các thời điểm đổi pha; báo cáo số lượt và thời gian đã kiểm |
| D04 | D | 0 va chạm người–xe; 0 chồng xe rõ rệt trong bộ tình huống kiểm | Kiểm khoảng cách/hình học và video QA; nêu kiểm bằng sphere/OBB hay collider thực |
| D05 | D | 100% ảnh có metadata đúng frame; timestamp tăng đều theo FPS đã cấu hình; hai camera cùng timestamp | Script duyệt toàn bộ tập mẫu; số file thiếu/trùng = 0 |
| D06 | D | 100% ID hợp lệ và ổn định trong cùng lần tồn tại; hộp hữu hạn, nằm trong ảnh, kích thước dương | Schema validator, kiểm track theo frame, overlay mẫu ở cả trung tâm và rìa fisheye |
| D07 | D | Có calibration và định nghĩa hệ trục; test điểm chuẩn tâm/rìa đúng hướng | Render vật kiểm tra và so vị trí pixel với hàm chiếu độc lập; ghi sai số thực tế |
| D08 | D | Cùng seed/cấu hình cho cùng chuỗi trạng thái đối tượng trong cùng build | Chạy hai lượt, so hash trạng thái/metadata đã chuẩn hóa; không hứa RGB bit-identical giữa GPU/driver |
| D09 | D | Không vật liệu hồng, ảnh đen/trống, texture thiếu, nhấp nháy z-fighting nổi bật; người và xe phân biệt được | Contact sheet nhiều pha, xem video toàn bộ, giữ ảnh minh chứng |
| D10 | D | Có build/scene, dữ liệu mẫu, hướng dẫn chạy và báo cáo kết quả cùng giới hạn | Checklist bàn giao có đường dẫn, mở thử từ đầu |
| P01 | P | Sai số chiếu điểm chuẩn RMS ≤ 1 pixel trong miền sử dụng; sai số mép ≤ 2 pixel | Ít nhất 30 pose bảng chuẩn phủ trung tâm/rìa, kiểm chéo OpenCV; đo lại cho mỗi profile camera |
| P02 | P | Hộp nhìn thấy khớp mask IoU ≥ 0,98; 0 ID trộn màu; mẫu che khuất và cắt biên đúng | So mask/hộp tự động ≥ 1.000 frame/camera; review ít nhất 100 mẫu được phân tầng |
| P03 | P | Không xuyên vật, trượt chân; tỷ lệ cảnh tự nhiên được 3 người đánh giá độc lập cho ≥ 4/5 ở ≥ 90% clip | Rubric: bước chân, chuyển trạng thái, khoảng cách xã hội, phản ứng đèn, hướng nhìn; không chỉ chấm ảnh tĩnh |
| P04 | P | Photoreal đủ ở kích thước người nhỏ nhất cần phát hiện, không lộ khớp hình học hay nhân vật lặp dễ nhận ra | So crop synthetic và crop camera thật cùng pixels/person; art review da/tóc/quần áo, silhouette, bóng, exposure |
| P05 | P | Phủ ma trận biến thiên; không confound màu áo/xe, camera hoặc thời tiết với lớp/hành vi | Histogram/cross-tab theo scenario, asset, seed, mật độ, ánh sáng, tốc độ và che khuất |
| P06 | P | Mốc đề xuất chạy tương tác p95 frame time ≤ 33,3 ms ở 1080p trên GPU đã ghi tên; chế độ ghi không mất frame | Profile player sau warm-up ≥ 5 phút; báo FPS realtime và số frame/giây ghi offline riêng |
| P07 | P | Chạy stress ≥ 30 phút × 20 seed không deadlock, NaN, exception; không có tăng bộ nhớ không giới hạn | Monitor hàng chờ, đối tượng hoàn tất tuyến, RSS/VRAM, log lỗi; seed gây lỗi trở thành regression case |
| P08 | P | Hiệu quả ML tăng trên test video thật chưa dùng để chỉnh mô phỏng, không suy giảm đáng kể nhóm khó | Thí nghiệm có baseline, cùng mô hình/compute, ≥ 3 seed huấn luyện, confidence interval theo clip |

## 5. Kỹ năng và trách nhiệm cần có

| Nhóm kỹ năng | Việc cụ thể trong demo | Khi mở rộng chất lượng |
|---|---|---|
| Unity/C# | Kiến trúc component, lifecycle, serializable config, scene/bootstrap, coroutine, object pooling, build/debug | Profiler CPU/GPU, job system khi cần, bộ sinh batch và version dữ liệu |
| Hình học và mô phỏng | Hệ trục, vector/quaternion, lane/path, state machine tín hiệu, khoảng cách và gia tốc | Spline rẽ, right-of-way, tránh va chạm người, nhóm người và tương tác |
| Character/animation | Pivot/rig, chu kỳ bước, đồng bộ tốc độ–pose, chuyển đi/chờ | Humanoid retargeting, blend tree, foot IK, mocap được phép dùng ML, motion matching nếu cần |
| Environment/technical art | Tỷ lệ thực, modular building, UV/tiling, PBR, ánh sáng, bóng, LOD | Photogrammetry, nhân vật scan có quyền phù hợp, tóc/quần áo, địa điểm thật |
| Camera/rendering | RenderTexture, phối cảnh/fisheye, shader, capture đồng bộ | Lens calibration thật, exposure/noise/motion blur/rolling shutter có tham số |
| Data engineering/CV | JSON/CSV, ID, box/mask, validator, schema, SHA256, dataset card | COCO/MOT tùy tác vụ, kho lưu trữ, theo dõi provenance, quản lý partition |
| QA/ML evaluation | Test pha/đường/chiếu/capture, visual review, báo cáo bằng chứng | Thiết kế ablation synthetic→real, kiểm bias, đo chi phí trên mỗi frame hữu ích |

Một Unity developer có thể làm bản kỹ thuật này. Chất lượng dữ liệu sản xuất cần phối hợp ít nhất với người phụ trách CV/dữ liệu và character/environment artist; chỉ tăng polygon không tự cải thiện hành vi hoặc hiệu quả ML.

## 6. Quy trình phát triển và kiểm thử

1. **Khóa định nghĩa dữ liệu.** Chọn nhiệm vụ detection/tracking, camera mục tiêu, class taxonomy, occlusion policy, FPS/độ phân giải và điều kiện cảnh. Lập bảng mục tiêu trước khi dựng cảnh.
2. **Graybox và luật.** Dựng bằng mét; đánh dấu làn, vùng xung đột, vạch dừng, tuyến đi bộ. Viết simulation tách render. Test chuyển pha ngay tại ranh giới và trong tình huống hàng chờ đông.
3. **Tạo pipeline ảnh/nhãn sớm.** Dùng vật chuẩn dễ kiểm, bật cả hai camera, kiểm hệ trục và thời gian. Lưu raw metadata trước khi dựng đẹp để phát hiện lỗi hình học sớm.
4. **Tích hợp art có nguồn rõ.** Lưu license, nguồn tải, tác giả, ngày và hash. Cài texture diffuse sRGB; normal/roughness linear; đúng convention normal. Định tile theo mét, không kéo giãn lên toàn con đường/tòa nhà.
5. **Hoàn thiện chuyển động.** Thêm khác biệt tốc độ/chiều cao/trang phục, chuyển đi–chờ, phanh và khoảng cách. Xem video, không chỉ screenshot. Kiểm spawn/respawn và vòng đời ID.
6. **Kiểm chức năng có chủ đích.** Hàng chờ tại mọi hướng, đổi đèn lúc xe sắp chạm vạch, người mới vào/vẫn trong crossing, mật độ cao, capture khi pause, mất thư mục đích/thiếu dung lượng, đối tượng ngoài khung/che nhau.
7. **Kiểm dữ liệu tự động.** Parse tất cả metadata; kiểm số ảnh, schema, frame gap, kích thước, hộp, track, timestamp, calibration và hash. Chọn contact sheet phân tầng theo pha/camera/seed, xuất overlay vào thư mục QA riêng.
8. **Chạy lặp và đánh giá.** So trạng thái cùng seed; benchmark player và ghi dữ liệu riêng; giữ log ngay cả khi có lỗi. Chỉ ghi “đạt” cho tình huống đã chạy. Mỗi sửa lỗi phải chạy lại case liên quan.
9. **Đóng gói thuyết trình.** Có player/scene, clip hai camera, một cặp ảnh + nhãn xem được, metadata/calibration, hướng dẫn tạo lượt mới, bản kết quả và danh sách giới hạn. Phiên bản cấu hình trong dữ liệu phải khớp bản chạy.

## 7. Đánh giá giá trị đối với AI

Độ đẹp chỉ là một chỉ báo. Bài thử cần kiểm tra trên video thật từ camera mục tiêu được thu thập có quyền phù hợp. Chia dữ liệu theo **địa điểm, phiên quay và clip**; cả hai camera của cùng lượt mô phỏng nằm cùng partition. Không chia train/test bằng chọn ngẫu nhiên các frame liền kề: nền, người, quỹ đạo và điều kiện ánh sáng gần như giống nhau sẽ làm kết quả trông tốt giả tạo.

Đề xuất so bốn nhánh với cùng mô hình, lịch huấn luyện và ngân sách: chỉ real; chỉ synthetic; real + synthetic; real + synthetic có randomization. Nếu thử nhiều cấu hình bằng real-validation, giữ real-test tách biệt đến lần cuối. Domain randomization là giả thuyết cần đo, không phải bảo đảm transfer; nghiên cứu gốc chứng minh ích lợi trong những thiết lập cụ thể. [Tobin và cộng sự, 2017](https://arxiv.org/abs/1703.06907)

Với detection: báo mAP, precision/recall theo kích thước đối tượng, độ che khuất và khoảng cách. Với tracking: thêm HOTA/IDF1, ID switches và track fragmentation. Tính khoảng tin cậy theo clip, không coi hàng nghìn frame tương quan là hàng nghìn quan sát độc lập. Xem riêng rìa fisheye, người nhỏ, người che nhau, xe dừng và thời khắc tín hiệu đổi.

Randomization cần giữ quan hệ vật lý: đổi giờ nắng phải đổi hướng/bóng; đổi tốc độ phải đổi nhịp bước; đổi mật độ phải đổi tương tác. Ma trận sản xuất đề xuất gồm nhiều asset người/xe, mật độ thấp–vừa–cao, nhiều seed, góc lắp camera, ánh sáng/độ phơi sáng và thời tiết. Không gán một loại áo hay màu xe duy nhất cho một hành vi. Vắng chuyển động hiếm hoặc vi phạm trong cảnh chuẩn cần được ghi rõ; nếu sau này mô phỏng vi phạm, tách scenario và nhãn ý định để tránh lẫn với kiểm thử tuân thủ.

## 8. Tài nguyên, giấy phép và giới hạn hiện tại

Đã tải ba bộ texture CC0 từ Poly Haven: asphalt, pavement và brick; mỗi bộ diffuse 1K, normal GL 1K và roughness 1K. `asset-manifest.json` lưu chín file, URL nguồn, tác giả, kích thước và hash, đã đối chiếu MD5 do API công bố. `ThirdParty-Assets.md` mô tả cách dùng. [Poly Haven license](https://polyhaven.com/license)

Mixamo không phù hợp với mục tiêu dữ liệu huấn luyện này: điều khoản bổ sung bao gồm cả dữ liệu/đầu ra suy ra từ dịch vụ khi dùng để tạo, huấn luyện, kiểm thử hoặc cải thiện AI/ML. Unity Asset Store có hạn chế riêng đối với việc dùng store/asset cho AI/ML nếu chưa có chấp thuận; “miễn phí tải” không đồng nghĩa có quyền đó. Vì vậy demo dùng hình học/animation của dự án và texture CC0 tải trực tiếp, không đưa tài nguyên Mixamo hoặc Asset Store vào tập mẫu. [Mixamo Additional Terms, 23/06/2021](https://wwwimages2.adobe.com/content/dam/cc/en/legal/servicetou/Mixamo-Addl-Terms-en_US-20210623.pdf), [Unity Asset Store §3.8, 04/12/2024](https://unity.com/legal/as-terms)

Ứng viên cho vòng character tiếp theo: MakeHuman có chính sách CC0 cho export từ core; asset cộng đồng bổ sung cần kiểm tra từng license. Quaternius công bố bộ nhân vật và animation CC0, hợp làm bước retargeting trước photoreal. Chưa tích hợp các bộ này trong bản hiện tại; truy cập tải itch.io đã gặp trang kiểm tra trình duyệt. [MakeHuman export FAQ](https://static.makehumancommunity.org/makehuman/faq/can_i_sell_models_created_with_makehuman.html), [Quaternius Base Characters](https://quaternius.com/packs/universalbasecharacters.html), [Quaternius Animation Library](https://quaternius.com/packs/universalanimationlibrary.html)

Giấy phép texture không thay thế điều khoản của công cụ. Unity ToS cập nhật 30/06/2026 có mục 17.2(dd) về dữ liệu từ Offerings dùng cho ML và định nghĩa riêng User Content/Project. Mike Geig, Principal Advocate của Unity, đã giải thích trên diễn đàn rằng huấn luyện với nội dung của nhà phát triển, gồm dữ liệu tổng hợp từ cảnh tự làm, được phép; ông phân biệt điều đó với huấn luyện trên tài sản của Unity và nói rõ bài đăng là giải thích không chính thức. Vì vậy không suy ra một lệnh cấm chung đối với dữ liệu từ cảnh tự làm, cũng không coi CC0 của texture là quyền cho mọi thành phần khác. Khi vận hành sản xuất, đối chiếu thỏa thuận Unity và giấy phép từng thành phần áp dụng cho dự án. Demo này chưa thực hiện huấn luyện AI. [Unity ToS](https://unity.com/legal/terms-of-service), [Giải thích của Unity, bài 9 và 12, 02/07/2026](https://discussions.unity.com/t/new-terms-of-service-is-unity-restricting-local-ai-tools-and-ai-training/1724661)

Các khoảng trống chất lượng cần ghi trong buổi trình bày: nhân vật/xe còn là mô hình kỹ thuật; chưa có hiệu chỉnh với ống kính fisheye thật, benchmark ML trên dữ liệu thật, hoặc nghiệm thu tự nhiên bởi hội đồng đánh giá. Hộp chiếu và ước lượng visibility chỉ phù hợp mức minh họa nếu chưa có segmentation chính xác. Những giới hạn đó không làm mất giá trị kiểm chứng pipeline nhưng giới hạn việc gọi bộ mẫu là dữ liệu huấn luyện đã được xác thực.

## 9. Khung thuyết trình cho StationFlow

Trong khoảng 5 phút: trình bày bài toán và các biến có thể kiểm soát; chạy cảnh để thấy một lượt xe chờ/đi và người qua đường; chuyển hai camera để thấy khác biệt phối cảnh/fisheye; mở một ảnh với metadata và overlay kiểm tra; kết thúc bằng kết quả kiểm thử thực tế, các cổng chất lượng sản xuất và thí nghiệm synthetic→real tiếp theo. Tránh trình bày chỉ video đẹp: giá trị đóng góp kỹ thuật nằm ở tính kiểm soát, đồng bộ, nhãn có nguồn và khả năng tái lập.

Nguồn trực tuyến trong tài liệu được kiểm tra ngày 21/09/2026. Chúng được dẫn để truy xuất thiết kế và lựa chọn tài nguyên; ngưỡng nghiệm thu do dự án đề xuất, không được gán là tiêu chuẩn của những nguồn này.
