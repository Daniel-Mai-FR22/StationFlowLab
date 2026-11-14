# Junction Lab — Bản tóm tắt cho StationFlow

**Đề xuất đóng góp:** xây dựng bộ sinh dữ liệu hình ảnh có kiểm soát trong Unity, từ cấu hình bối cảnh và hành vi đến camera, nhãn, kiểm thử và lượt chạy tái lập. Ngã tư châu Âu đi bên phải là ví dụ đầu tiên để chứng minh quy trình này.

## Demo thể hiện điều gì?

| Hạng mục | Phạm vi triển khai trong mã demo | Bằng chứng cần mở khi trình bày |
|---|---|---|
| Không gian 3D | Ngã tư bốn nhánh, vỉa hè, kiến trúc phố, vạch đường/đèn; texture mặt đường–vỉa hè–gạch CC0 | Scene chạy trong Unity hoặc player Windows |
| Giao thông | Xe đi thẳng bên phải; pha riêng Bắc–Nam, Đông–Tây và người đi bộ; hàng chờ/clearance | Video qua đủ các pha và bộ đếm audit |
| Chuyển động | Người có khác biệt hình dáng/màu trang phục/tốc độ, khớp bước thủ tục; xe phanh và xếp hàng | Xem clip gần người bắt đầu/dừng qua đường |
| Camera | Pinhole 960×540 và fisheye equidistant 180° 640×640 cùng timestamp | 900 cặp PNG và calibration; ba video 90 giây |
| Nhãn | ID, lớp, trạng thái, pose/vận tốc, hộp amodal xấp xỉ cho cả hai sensor | Dòng JSONL và overlay QA nếu có |
| Khả năng lặp | Seed, simulation 60 Hz, ghi theo bước cố định, metadata | Báo cáo kiểm thử lặp và run-report của lượt mẫu |
| Tài nguyên | Ba bộ vật liệu CC0, manifest chín file có nguồn và SHA256 | `ThirdParty-Assets.md`, `asset-manifest.json` |

Bản bàn giao: scene `Assets/StationFlowDemo/Scenes/JunctionLab.unity`, player `Builds/JunctionLab/JunctionLab.exe`, dữ liệu `Captures/Presentation` và ba video `Presentation/JunctionLab-{pinhole,fisheye,comparison}.mp4`. Hướng dẫn thao tác: `Quickstart.vi.md`; mô tả dữ liệu: `Dataset-Card.vi.md`.

Kết quả kiểm tra đã thực hiện: **900 cặp frame / 1.800 PNG / ba video 90 giây**, seed 42, 24 người và 12 xe; **41 lượt qua nút hoàn tất** trong khoảng ghi t≈40,0–129,9 giây, 0 sự kiện xe/người vào khi bị cấm và 0 chồng hình kiểm tra. Bộ validator giải mã đủ ảnh, xác nhận đủ chín pha tín hiệu và kết luận PASS với 0 lỗi/0 cảnh báo. Kiểm thử bổ sung gồm 4.086.453 assertion simulation, 211 kiểm toán học camera và 14 kiểm marker qua GPU. Phạm vi và log được lưu trong `Reports` và thư mục sensor-validation; chưa đo hiệu quả ML hoặc chứng nhận chất lượng photoreal.

## Giá trị kỹ thuật

Nhóm có thể thay đổi seed, số người/xe, tốc độ lấy mẫu và mô hình camera, đồng thời giữ được dữ liệu trạng thái làm nguồn nhãn. Điều này giảm phụ thuộc vào gán nhãn video bằng tay và tạo ra tình huống có thể chạy lại để sửa lỗi. Phần Unity developer đảm nhận gồm lập trình cảnh, điều khiển tác tử, rendering sensor, đồng bộ thời gian, xuất nhãn và công cụ kiểm tra.

Tính đúng của nhãn, hành vi và camera cần được đánh giá ngang với chất lượng hình ảnh. Một video nhìn đẹp nhưng có ID nhảy, camera sai phép chiếu hoặc hành vi không tự nhiên có thể tạo dữ liệu sai cho AI. Bộ tiêu chí chi tiết nằm trong `Requirements-and-Evaluation.vi.md`.

## Giới hạn cần nói rõ

Nhân vật và xe hiện là mô hình thủ tục có phong cách minh họa. Chưa đạt yêu cầu photoreal ở cự ly gần; động tác chưa có mocap/foot IK và phản ứng xã hội phong phú. Xe chưa rẽ; ánh sáng và kiến trúc là một cấu hình chính. Fisheye là mô hình lý tưởng, chưa khớp một ống kính thực. Hộp nhãn không đo pixel bị che; chưa có instance mask hay occlusion ground truth. Chưa huấn luyện hoặc đánh giá mô hình trên video thật.

Đây là **demo kỹ thuật và dữ liệu mẫu để quyết định giai đoạn tiếp theo**. Cần vượt qua các cổng về character/motion, nhãn pixel, calibration thật và hiệu quả synthetic→real trước khi gọi đó là pipeline dữ liệu sản xuất.

## Giai đoạn tiếp theo và nguồn lực

Ước lượng dưới đây là giả định lập kế hoạch cho **một ngã tư, hai camera, một tác vụ detection/tracking**, tận dụng nền demo và có quyền dùng asset phù hợp. Đây không phải báo giá hay cam kết tiến độ; sai số dự kiến ±50% do mức chân thực, nguồn nhân vật và dữ liệu camera thật chưa được khóa.

| Nhánh công việc | Kỹ năng/người phụ trách | Ước lượng công |
|---|---|---|
| Khóa tiêu chuẩn camera, taxonomy, tình huống và video tham chiếu | Unity developer + kỹ sư CV/dữ liệu | 3–5 người-ngày |
| Thay nhân vật/xe, retarget động tác, blend/foot IK, kiểm cận cảnh | Character/technical artist + Unity developer | 8–15 người-ngày |
| Bổ sung luồng rẽ, hành vi nhóm, arrival/disappearance có tracking đúng | Unity/simulation developer | 6–10 người-ngày |
| Instance/depth pass, bbox từ mask, kiểm che khuất và camera thật | Graphics/CV engineer | 6–10 người-ngày |
| Ma trận randomization, kiểm thử batch, đóng gói dataset/version | Unity developer + data engineer | 4–7 người-ngày |
| Thí nghiệm real-only/synthetic/mixed, test thật tách biệt | ML engineer + QA | 5–8 người-ngày |
| **Tổng tham khảo** | Các nhánh có thể chồng thời gian một phần | **32–55 người-ngày** |

Với một người phát triển chính và hỗ trợ art/CV bán thời gian, kế hoạch lịch sơ bộ khoảng 6–10 tuần; thời gian thu/mua quyền dữ liệu hoặc asset có thể làm lịch dài thêm. Ngân sách lao động = số người-ngày thực phân bổ × đơn giá từng vai trò. Chi phí nhân vật/animation, GPU và lưu trữ tính riêng sau một lượt pilot; không giả định tất cả tài nguyên photoreal đều miễn phí.

Nguồn lực tối thiểu: một máy Windows có GPU hỗ trợ Unity URP; Unity developer; hỗ trợ artist rig/animation; một kỹ sư CV chốt schema và phép đo trên dữ liệu thật. Mốc throughput phải benchmark trên máy đích. Để dự tính dung lượng: đo tổng byte PNG trung bình của hai camera trong lượt pilot, nhân với FPS × tổng giây, cộng metadata/QA và phần dự phòng; không dự toán từ số pixel chưa nén rồi gọi đó là dung lượng PNG thực tế.

## Điều kiện ra quyết định

Đầu tiên nghiệm thu pipeline kỹ thuật: chạy được, hai camera đồng bộ, nhãn có định nghĩa, kiểm thử luật và dữ liệu có bằng chứng. Tiếp theo kiểm tra chất lượng cảm nhận trên crop người/xe và video hành vi. Cuối cùng thử nghiệm với video thật từ camera mục tiêu: cùng mô hình và ngân sách, so real-only với real + synthetic; tách train/validation/test theo địa điểm/phiên quay/clip, không chia ngẫu nhiên frame liền nhau.

Tài nguyên miễn phí được chọn theo quyền dùng dữ liệu ML, không chỉ theo giá tải. Demo đã dùng texture CC0 và hình học/động tác của dự án. Mixamo có hạn chế ML với cả đầu ra suy ra từ dịch vụ; Asset Store có điều khoản ML riêng. [Mixamo terms](https://wwwimages2.adobe.com/content/dam/cc/en/legal/servicetou/Mixamo-Addl-Terms-en_US-20210623.pdf), [Asset Store terms](https://unity.com/legal/as-terms), [Poly Haven CC0](https://polyhaven.com/license)

Ngày lập: 21/09/2026. Các ước lượng trên là đề xuất của dự án; không phải số liệu từ nhà cung cấp hoặc kết quả đã đo của bản demo.
