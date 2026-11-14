# TrainStation DecisionLab — quyết định hành khách theo ngữ cảnh

## Phạm vi dữ liệu hiện tại và lịch sử

Các phần mô tả scene `TrainStationDecisionLab.unity` bên dưới giữ nguyên cách ghi log của thí nghiệm ngoài Daily. Với **các lượt thu thập mới của `TrainStationDaily.unity`**, manifest là `stationflow.station_capture.v2` và `storageLayout` là `per_agent.v1`: đọc `agents/index.json` để ánh xạ ID logic của từng chuyến hành khách sang thư mục `agents/<safeDir>/`. Mỗi thư mục giữ một `profile.json` khi bật profile, rồi quỹ đạo, animation, quyết định và sự kiện của đúng `agentId` theo các tùy chọn. Index liệt kê mọi chuyến đã lập kế hoạch; thư mục/luồng được tạo khi cần, ngoại trừ profile được tạo cho mọi chuyến khi bật tùy chọn này. Không có `agents/` khi tắt mọi tùy chọn dữ liệu avatar.

Đồng hồ, scenario, giờ tàu, trạng thái tàu, video và sự kiện chung của ga ở gốc lượt ghi. `events.jsonl` gốc chỉ có sự kiện `agentId` rỗng. Scenario Daily v4 để trống/không lưu các mảng `profiles` và `scheduledJourneys` để tránh lặp dữ liệu cá nhân. Mọi luồng vẫn nối qua `(runId, syncSampleId)`. **`agentId` nhận diện một chuyến; `actorSlotId` là vị trí trong pool hiển thị được tái sử dụng, không phải danh tính để gộp dữ liệu.** Các capture v1 và log ngoài Daily tiếp tục dùng tài liệu đi kèm chúng; không đổi cấu trúc dữ liệu cũ. [Sơ đồ dữ liệu hiện tại bằng tiếng Pháp](../Assets/StreamingAssets/TrainStationData/DataSchema.fr.html).


DecisionLab là một thí nghiệm tách riêng trong `Assets/TrainStation/Scenes/TrainStationDecisionLab.unity`. Mỗi hành khách thí nghiệm có chuyến tàu, nhu cầu và thời hạn lên tàu; họ đọc bảng, chọn hoạt động, đi tới nơi và đánh giá lại tình huống. Xác suất lựa chọn thay đổi theo thời gian còn lại, nhu cầu, thông tin đã biết và khả năng hoàn thành hành trình.

Đây là **chính sách xác suất do dự án đặt tham số**, chưa được học từ dữ liệu hành khách và chưa phải MDP được tối ưu hoặc huấn luyện bằng reinforcement learning. Các thời lượng ngắn, mức nhu cầu và trọng số dùng để quan sát rõ cơ chế trong demo; không đại diện cho phân bố hành vi ngoài đời.

## Mở và quan sát

Trong Unity, chọn **Efrei Research Lab → Train Station → Decision Lab → Create isolated experiment** để tạo bản thí nghiệm nếu chưa có; dùng **Open experiment** để mở bản đã lưu. Scene thí nghiệm và bản sao lịch tàu nằm riêng; scene `TrainStation.unity`, lịch tàu nguồn và các tài nguyên dùng chung được giữ lại. Khi đã có scene thí nghiệm, lệnh tạo mở lại và kiểm tra bản đã lưu thay vì tự dựng lại cấu hình của người dùng.

Play để chạy; góc nhìn ban đầu là toàn cảnh sảnh. Thanh trên cùng cho biết thời gian, seed, số người còn trong ga, đã lên tàu, đã kết thúc ngoài tàu và số lần đánh giá quyết định. Các bảng giới thiệu lớn và bảng điều khiển tàu cũ được ẩn riêng trong scene thí nghiệm để tránh trùng lặp.

Danh sách **VOYAGEURS** bên trái có ô tìm theo ID hoặc mã chuyến, cùng bộ lọc **Tous / En gare / Terminés**. Bấm một hàng hoặc mũi tên cạnh ID bên phải sẽ chọn và **tự chuyển camera sang theo dõi người đó**. Bấm lại cùng một người vẫn đưa camera về chế độ theo dõi nếu trước đó đang xem CCTV. Dấu **− / +** thu gọn hoặc mở lại từng bảng để dành thêm chỗ cho cảnh ga.

Bảng bên phải có ba thẻ:

- **Observation**: hoạt động hiện tại, trạng thái thực hiện, chuyến được gán, ke/thông tin người đó đã biết, giờ rời ga/hạn lên tàu và ngân sách thời gian ước tính; năm nhu cầu hiển thị theo phần trăm. Khi ghé chỗ ngồi, bảng thêm loại hoạt động, ID ghế và thời gian lưu lại.
- **Décision**: lý do lựa chọn, đích tới, số lần đánh giá, hoạt động đã hoàn tất, trạng thái thật của tàu, lịch sử gần nhất và sự cố điều hướng đã ghi nhận. Sự cố lịch sử không tự có nghĩa người đó còn đang kẹt.
- **Journaux**: trạng thái ghi dữ liệu, nút sao chép đường dẫn, sự kiện gần nhất và thông tin kịch bản.

Đồng hồ ở thanh trên và các mốc **t +** của chuyến dùng thời gian mô phỏng kể từ đầu lượt, không phải giờ đồng hồ trên bảng ga. Ví dụ **t + 03:00** nghĩa là phút thứ ba của lượt chạy. Ngân sách thời gian là phần còn dư sau ước tính đi tới ke và dự phòng, nên khác thời gian đếm ngược đơn thuần tới giờ tàu chạy.

| Điều khiển | Tác dụng |
| --- | --- |
| **Pause / Reprendre** | Tạm dừng/tiếp tục toàn bộ thí nghiệm theo đồng hồ chung, gồm hành khách và tàu. |
| **Recommencer** | Tải lại scene thí nghiệm; bắt đầu lượt mới với cấu hình và seed đã lưu. |
| **+ 75 s retard** | Chậm thêm 75 giây cho chuyến đủ điều kiện đầu tiên còn chưa tới hạn đóng cửa. |
| **Annuler train** | Hủy chuyến đủ điều kiện đầu tiên còn hành khách chưa kết thúc, để quan sát chuyển kế hoạch. |
| **F7** | Thu gọn/hiện lại giao diện quan sát; hành vi, camera và ghi log vẫn tiếp tục. |
| **Hall** | Trở lại góc nhìn toàn cảnh sảnh, dừng theo dõi hành khách. |
| **Suivre** | Theo dõi hành khách đang được chọn. |
| **Recentrer / Home** | Khôi phục khung hình theo dõi mặc định; khi ở góc nhìn sảnh, khôi phục khung hình sảnh. |
| **Caméras CCTV** | Mở danh mục góc nhìn; chọn camera cố định sẽ dừng theo dõi hành khách. |
| **Toitures** | Ẩn/hiện mái để quan sát bên trong ga. |
| **Copier le chemin des journaux** | Sao chép đường dẫn lượt chạy từ thẻ Journaux. |

Trong vùng cảnh, giữ **chuột phải** và kéo để xoay quanh người đang theo dõi; dùng **con lăn** để tiến gần/lùi xa. Camera đi theo vị trí của người, còn góc xoay và khoảng cách được giữ khi đổi sang người khác; nút Recentrer đưa về khung hình mặc định. Khi người lên tàu hoặc rời ga và actor được ẩn, camera giữ vị trí cuối và báo hành trình đã kết thúc. Camera không dịch chuyển hành khách hay thay đổi quyết định của họ.

Thanh camera dưới cùng hiển thị đang ở sảnh, theo dõi hay một camera cố định. Vẫn có camera sảnh tự do và 14 CCTV. Khi mở danh mục camera, các bảng quan sát tạm ẩn để tránh che lựa chọn. Con lăn trên danh sách/thẻ thông tin cuộn nội dung, không zoom xuyên qua bảng; gõ số trong ô tìm không đổi camera. Giao diện tự đổi tỷ lệ theo kích thước Game view, với các bảng có thể thu gọn.

## Quần thể và kịch bản ban đầu

Bản cấu hình ban đầu dùng **24 người đi riêng làm hành khách thí nghiệm**, **60 hành khách nền** và **7 nhân viên**. Những hành khách còn lại vẫn có trong bản sao scene nhưng được tắt; không bị xóa khỏi scene nguồn. Nhóm nền giữ đủ thành viên: 18 người ngồi, 24 người trong tám nhóm đứng và 18 người trong sáu nhóm đi. Các lượt ghé dịch vụ tự động của dân số nền được tắt để giảm nhiễu khi quan sát quyết định của 24 người.

Mỗi người thí nghiệm được gán một trong bốn chuyến. Cấu hình ban đầu đặt các thời điểm rời ga ở giây 180, 240, 330 và 420; cửa đóng trước giờ rời ga. Các đoàn tàu đã ở ke khi bắt đầu. Kịch bản có một sự kiện **chậm 75 giây tại t = 55 giây** cho chuyến đầu tiên, với thông báo ngay. Có thể bổ sung sự kiện hoặc đổi các tham số trong Inspector của world và lịch tàu riêng.

Đồng hồ của bản này dùng **1 giây Unity = 1 giây nhà ga**: `StationRailOperations.simulationRate = 1`. Thời gian đi bộ, hàng chờ, hoạt động và hạn lên tàu được tính theo cùng đơn vị. Không dùng cách quy đổi 3× của scene ga gốc để trừ trực tiếp thời gian đi bộ. Các nút tạm dừng/khởi động lại của bảng tàu trong scene thí nghiệm phải gọi world chung.

## Ba lớp hoạt động

1. **Chính sách quyết định** — `StationDecisionPolicy` nhận trạng thái đã biết, nhu cầu và danh sách lựa chọn. Nó loại lựa chọn không khả thi, cho điểm những lựa chọn còn lại, chuẩn hóa thành xác suất rồi lấy mẫu theo seed.
2. **Thực thi hoạt động** — `StationDecisionPassenger` giữ ý định, mục tiêu và trạng thái thực hiện; điều phối đi tới bảng, ghé dịch vụ, chờ, tới ke, lên tàu hoặc rời ga. Các kết quả hoặc sự kiện mới có thể kích hoạt lần quyết định kế tiếp.
3. **Chuyển động và tư thế** — `StationActorMotion`, NavMeshAgent, ghế/quầy có sẵn và thư viện animation thực hiện đường đi và trạng thái cơ thể. Chính sách chọn mục tiêu; NavMeshAgent tiếp tục sở hữu tìm đường, vận tốc và né tránh.

Scene thí nghiệm dùng bản sao clip đi/đứng được nhập theo Avatar **XBot** để giữ hướng gập gối đúng; mỗi model vẫn giữ Avatar của chính nó. Thư viện/controller riêng của Lab giữ các ID chuyển động, Foot IK và các hoạt động khác. Tốc độ bước chuẩn được đo từ clip mới là **1,08776343 m/s**, rồi playback tiếp tục được điều chỉnh theo vận tốc NavMesh thực tế.

Khi **Pause**, world giữ graph animation Humanoid trước khi dừng đồng hồ, tránh tư thế chân biến dạng rồi còn lưu lại sau **Reprendre**. Camera orbit vẫn xoay/zoom được; báo trễ và hủy chuyến lúc pause vẫn nhận lệnh mà không làm đồng hồ chạy. Các bản FBX, clip, prefab, controller và scene nguồn được giữ nguyên. Xem [bằng chứng chẩn đoán và kiểm tra](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/Animation-Diagnosis.vi.md).

Lỗi dáng chân đã được quan sát, tái hiện và sửa trong phạm vi kiểm tra đó. Nếu thấy lại, ghi ID hành khách, thời gian, hoạt động, trạng thái Pause/Resume và góc nhìn để kiểm tra đúng tình huống; không thiết lập theo dõi định kỳ.

`Display`, `Cafe`, `Toilet`, `Shop`, `Rest`, `Info`, `Platform`, `Exit` và `Board` là các hoạt động cấp cao. Chúng khác với trạng thái thực thi như đang di chuyển, đợi hàng hoặc đang trao đổi. Đi nhanh là tham số vận động để thực hiện ý định, không phải một đích đến độc lập.

Ví dụ về chuỗi có thể xuất hiện là **bảng giờ → café → bảng giờ → ke → lên tàu**, hoặc **bảng giờ → thông tin → ra ngoài** khi chuyến không còn thực hiện được. Không phải mỗi người đều phải đi qua cùng một chuỗi.

## Ngân sách thời gian và xác suất

Hạn dùng để quyết định là **hạn còn được lên tàu**, có tính đến pha đóng cửa, không chỉ giờ đoàn tàu bắt đầu chạy. Với một hoạt động tùy chọn, ngân sách còn lại được đánh giá sau khi trừ:

- Thời gian đi từ vị trí hiện tại tới hoạt động.
- Thời gian chờ ước tính và thời lượng hoạt động.
- Với hoạt động ngồi: thời gian rời quầy, đi tới ghế, ngồi xuống/đứng dậy và lưu lại trên ghế.
- Thời gian từ hoạt động tới ke của chuyến đã chọn.
- Khoảng dự phòng của người đó.

Hoạt động thiếu thời gian, không có đường tới hoặc không còn phục vụ nhận xác suất bằng 0. Khi cần đi ngay tới tàu, chính sách ưu tiên tới ke/lên tàu; đường trực tiếp còn khả thi có thể dùng phần dự phòng. Khi chuyến bị hủy, đã đóng cửa hoặc đã lỡ hạn, các lựa chọn chuyển sang thông tin/rời ga thay vì tiếp tục tiêu ngân sách của chuyến cũ.

Các nhu cầu gồm **khát, vệ sinh, mệt, mua sắm và thông tin**, nằm trong khoảng 0–1. Quen ga, nhu cầu đọc thông tin và hoạt động vừa thực hiện cũng ảnh hưởng trọng số. Nhu cầu cao không bảo đảm hoạt động đó được chọn: điều kiện thời gian, tính khả thi và lấy mẫu xác suất vẫn áp dụng. Thời lượng dừng có giá trị dương; các giá trị trung bình và độ lệch chuẩn là tham số giả định của kịch bản.

Ngân sách hiển thị là một **ước tính**, không bảo đảm bắt kịp tàu. Né tránh, hàng chờ và người đứng chặn có thể làm thời gian thực khác ước tính. Cần đọc lý do và kết quả thực hiện trong log để phân biệt quyết định thiếu thời gian với hành trình bị kẹt.

Thông tin mới chỉ được giới hạn một phần: hành trình bình thường phải chọn `Display` trước khi chọn hoạt động khác, nhưng phép ước tính thời gian vẫn dùng tuyến tới **ke thật của chuyến được gán**, kể cả khi `PlatformKnown = false`. Vì vậy đây chưa phải mô hình quan sát không đầy đủ hoặc POMDP hoàn chỉnh. Trước khi dùng cho thí nghiệm dự đoán hành vi, cần thay đầu vào này bằng niềm tin hoặc ước tính lối tiếp cận mà hành khách thực sự có thể biết; không coi phép tính hiện tại là bằng chứng rằng hệ thống không lộ ground truth.

## Những hoạt động được biểu diễn tới đâu

| Hoạt động | Cách thể hiện trong thí nghiệm |
| --- | --- |
| Đọc bảng | Đi tới vùng quan sát bảng và dừng; thông tin đọc được tham gia quyết định. |
| Café | Đi tới quầy FIFO, được phục vụ, rời khu vực quầy rồi tìm ghế trống để ngồi uống café theo thời lượng. |
| Relay | Ghé quầy FIFO, rời quầy rồi tìm ghế ở bàn đọc để ngồi theo thời lượng. |
| Accueil | Đi tới quầy thông tin và phục vụ theo FIFO; không gắn chỗ ngồi. |
| Toilet | Dừng theo thời lượng tại vùng lối vào có thể tiếp cận; không mô phỏng bên trong buồng vệ sinh. |
| Rest | Đi tới một trong bốn ghế nghỉ được gán, ngồi xuống, lưu lại rồi đứng dậy. |
| Platform | Đi tới điểm chờ an toàn trên ke gắn với chuyến được phân công. |
| Board | Ghi nhận **lên tàu ở mức logic** tại điểm an toàn, chỉ khi đúng đoàn tàu/lượt chuyến đang ở pha mở cửa cho lên tàu và chưa quá hạn; sau đó ẩn hành khách. Không có animation bước qua cửa hay nội thất toa. |
| Exit | Đi tới điểm rời ga, kết thúc hành trình chưa lên tàu. |

Chưa có mua/đổi vé, thang cuốn/thang máy, đi ra ngoài rồi quay lại, hành lý, trẻ em, ghép/chia nhóm theo chuyến hoặc tối ưu kế hoạch nhiều chuyến. Người nền có tương tác nhóm riêng; 24 người thí nghiệm được chọn từ loại đi một mình.

## Lối đi, quầy và ghế

Bản nâng cấp riêng của Lab bố trí lại quầy/hàng đợi để tách điểm vào và điểm rời quầy, mở cửa ba cửa hàng tới 4 m và giữ mặt cắt lối trước quầy ít nhất 3 m. Quầy thông tin có lối tránh 2,4 m. Đây là các mặt cắt vật lý đã kiểm tra; vùng NavMesh cho tâm agent ở cửa ba cửa hàng đo được 2,88 m sau khi trừ khoảng tránh tường.

Nhóm **14 Decision Lab — accessible amenities** chứa mặt bằng bổ sung và các điểm kiểm tra lối đi. Có bốn chỗ café, hai chỗ đọc ở Relay và bốn ghế nghỉ. Ghế chỉ được giữ khi còn khả dụng và có đường đi; một ghế chỉ có một chủ. Nhân vật thực sự tới điểm đứng cạnh ghế trước khi ngồi, và trở lại vùng đi được trước khi nhả ghế. Sau phục vụ, nếu hết ghế hoặc không đủ thời gian, chuyến ghé quầy vẫn được ghi nhận và phần ngồi được bỏ qua có lý do.

Khi hủy chuyến hoặc hết thời gian trong lúc ngồi, người đó đứng dậy rồi chuyển kế hoạch. Lệnh nhận lúc Pause chỉ ghi ý định; tư thế giữ nguyên tới khi tiếp tục. Các trạng thái quan sát được gồm rời quầy, đi tới ghế, ngồi xuống, đang ngồi và đứng dậy. Clip ngồi hiện có được tái sử dụng; đạo cụ café/sách xác định bối cảnh, chưa có animation tay uống/lật sách hoặc tiếp xúc chính xác với đạo cụ.

[Báo cáo kiểm tra lối đi và ghế](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/Amenities-Verification.vi.md) ghi các tuyến đã kiểm và giới hạn. Lượt cuối **180,007 giây ở 1×** quan sát đủ café/đọc/nghỉ, nhả ghế khi hủy/hết hạn/ghế bị tắt, Pause giữ nguyên tư thế và camera mặc định nhìn rõ người đang ngồi. Sau sửa bộ đếm khi NavMesh trả Infinity, không còn cảnh báo tiến triển 25 giây trong lượt này; cảnh báo duy nhất do cố ý tắt ghế. Lượt dùng nhu cầu/độ trễ tạm để tăng độ phủ, không suy ra tỷ lệ lựa chọn mặc định. Receipt đầu tiên và chẩn đoán vẫn được giữ riêng.

## Dữ liệu đầu ra

Ghi dữ liệu được bật mặc định. Mỗi lượt tạo thư mục riêng dưới **`Captures/TrainStationDecisionLab/run-.../`**, gồm:

| File | Nội dung |
| --- | --- |
| `manifest.json` | Cấu hình và thông tin lượt chạy để đối chiếu seed, kịch bản, thời gian và phạm vi dữ liệu. |
| `decisions.jsonl` | Các lần đánh giá/lựa chọn hoạt động và ngữ cảnh tương ứng. |
| `trajectories.jsonl` | Mẫu vị trí, chuyển động và trạng thái của hành khách thí nghiệm; tần số cấu hình ban đầu 5 Hz. |
| `events.jsonl` | Sự kiện kịch bản và các thay đổi được ghi trong lượt chạy. |

JSONL chứa một đối tượng JSON trên mỗi dòng. Trường `time` tính bằng giây mô phỏng kể từ đầu lượt; `agentId` định danh người trong thí nghiệm. `serviceId` và `occurrence` định danh chuyến/lượt được phân công. Log quyết định ghi xác suất và lý do của từng lựa chọn, gồm cả lựa chọn bị loại. Log quỹ đạo lưu vị trí/vận tốc thế giới, hoạt động, trạng thái thực thi và trạng thái kết thúc; `visibleActor` chỉ biểu thị GameObject còn hoạt động, không phải visibility trong camera.

Log mới bổ sung `seatId`, `seatedActivity`, `isSeated`, `seatedSecondsRemaining`; log lựa chọn có chi phí đi tới ghế, chuyển tư thế và lưu lại. Sự kiện ghi hoàn tất quầy, bắt đầu rời quầy, giữ/nhả ghế, đổi trạng thái ngồi và lý do bỏ qua phần ngồi. Validator vẫn đọc được capture cũ chưa có các trường này; khi có, nó kiểm quyền giữ ghế không trùng và ngân sách không bỏ sót chi phí ngồi.

5 Hz là tần số lấy mẫu mục tiêu: đọc timestamp thực tế trong từng bản ghi, không suy thời gian chỉ từ số dòng. **Đây là log thế giới và hành vi, chưa phải dữ liệu RGB CCTV đồng bộ**, chưa có nhãn visibility, segmentation hoặc hiệu chỉnh camera xuất cùng lượt. Việc đã có 15 góc quan sát không tự tạo bộ dữ liệu 15 camera.

Seed giúp tái lập việc lấy mẫu của chính sách khi đầu vào và thứ tự quyết định giống nhau. NavMeshAgent và lịch cập nhật trong Unity có thể làm vị trí, thời điểm tới quầy và chuỗi ngữ cảnh khác nhau giữa các lần chạy; không hứa quỹ đạo hay ảnh giống từng bit chỉ nhờ cùng seed.

Nếu dùng log cho bài toán dự đoán, cần tách **thông tin mô hình được phép quan sát** khỏi **ground truth nội bộ**. Nhu cầu, ý định hoặc thông tin sự kiện tương lai không tự động trở thành đầu vào hợp lệ. Chia tập theo lượt chạy/kịch bản/seed để tránh các đoạn gần như trùng nhau xuất hiện ở cả train và test.

## Chỉnh cấu hình và đọc kết quả kiểm tra

Trong Hierarchy, nhóm **13 Decision Lab — conditional passenger intentions** chứa world và các điểm hoạt động. Chỉnh danh sách người tham gia, seed, sự kiện và tần số log tại world; chỉnh nhu cầu, chuyến, khoảng dự phòng và tốc độ đi tại từng `StationDecisionPassenger`. Lịch thí nghiệm nằm tại `Assets/TrainStation/DecisionLab/Resources/StationDecisionLabSchedule.asset`.

Báo cáo của phần này nằm dưới `Reports/TrainStationDecisionLab`. Đọc kết quả thực tế của từng bài; việc mã có công cụ kiểm tra không có nghĩa bài đó đã đạt. Các kiểm tra cần phân biệt tính đúng của chính sách xác suất, tính khả thi và hoàn thành chuyển động, điều kiện lên tàu, đồng bộ thời gian, chất lượng log và bảo toàn scene nguồn.

Hệ điều hướng gốc dùng NavMeshAgent có giới hạn đã biết khi đi ngang hàng đông; kết quả nghiệm thu lịch sử được mô tả trong [AgentOnly](../../StationFlowLab_Archive_20261020/Reports/TrainStationSocial/AgentOnly/Acceptance.vi.md). Giảm dân số nền cho thí nghiệm giúp cô lập quyết định nhưng không chứng minh mọi điểm nghẽn đã được giải quyết. Cần ghi rõ trường hợp bị kẹt, bỏ hoạt động hoặc trễ tàu thay vì xem mọi lần kết thúc là thành công.

## Kết quả bản nâng cấp hiện tại

[Lượt mặc định cuối](../../StationFlowLab_Archive_20261020/Captures/TrainStationDecisionLab/run-20261005-232014-933-527a25/play-check.json) đạt **600,071 giây mô phỏng ở 4×**, cả 24 người lên tàu và kết thúc; 79 lần đánh giá và 21 hoạt động tùy chọn hoàn tất. Không tăng nhu cầu để ép độ phủ: từ cấu hình đã lưu có **6 lượt ngồi café, 4 lượt đọc sách và 5 lượt nghỉ**, cả 15 đều hoàn tất và trả ghế. Hai lần ghé quầy bỏ phần ngồi có lý do thiếu ghế hoặc thiếu ngân sách. Không có sự kiện gián đoạn điều hướng trong lượt này.

[Validator](../../StationFlowLab_Archive_20261020/Captures/TrainStationDecisionLab/run-20261005-232014-933-527a25/validation.json) đạt 1.489.485 điều kiện, 60.072 dòng trajectory, 24 kiểm lên tàu theo trạng thái/hạn thật, không lỗi hoặc cảnh báo vận tốc. Kiểm lõi hiện tại có [23 bài chính sách/điều hướng đạt](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/amenities-policy-tests.txt); lượt tăng độ phủ 180 giây kiểm riêng việc nhả ghế khi bị gián đoạn và camera ngồi. Xem [báo cáo amenities](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/Amenities-Verification.vi.md) để đọc phạm vi và dữ liệu đầy đủ. Các kết quả hữu hạn này không cam kết mọi mật độ đám đông đều không kẹt.

## Bằng chứng hành trình ban đầu ngày 05/10/2026

[Báo cáo nghiệm thu](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/Acceptance.vi.md) tập hợp bằng chứng, phạm vi đạt và các giới hạn còn lại. [Kiểm tra scene đã lưu](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/saved-scene-check.json) đạt: 24 hành khách thí nghiệm, 60 người nền, 11 điểm hoạt động và 264 tuyến có đường đi. Scene ga nguồn và lịch tàu nguồn giữ nguyên; bản thí nghiệm dùng lịch riêng và đã đăng ký để nút khởi động lại hoạt động. [18 bài kiểm tra lõi chính sách](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/policy-tests.txt) đều đạt.

Các số dưới đây là bằng chứng trước nâng cấp mặt bằng/ghế; được giữ để đối chiếu, không thay thế kiểm tra của bản nâng cấp.

| Lượt lịch sử | Kết quả chức năng | Kiểm tra dữ liệu |
| --- | --- | --- |
| [Mặc định 600,085 s, toàn mô phỏng 4×](../../StationFlowLab_Archive_20261020/Captures/TrainStationDecisionLab/run-20261005-203514-844-6fac5d/play-check.json) | 20 lên tàu logic, 4 rời ga; cả 24 hoàn tất đọc bảng và kết thúc; 413 lần đánh giá quyết định, 20 hoạt động tùy chọn hoàn tất. | [PASS](../../StationFlowLab_Archive_20261020/Captures/TrainStationDecisionLab/run-20261005-203514-844-6fac5d/validation.json), 0 lỗi; 1 cảnh báo gồm 4 mẫu vận tốc native. |
| [Hủy sớm tại 2,041 s, quan sát 300,046 s ở 1×](../../StationFlowLab_Archive_20261020/Captures/TrainStationDecisionLab/run-20261005-202722-040-852743/play-check.json) | Cả 6 người của chuyến hủy rời ga dù chưa hoàn tất đọc bảng; 17 người lên tàu. Một người còn ở dịch vụ thông tin khi dừng quan sát. | [PASS](../../StationFlowLab_Archive_20261020/Captures/TrainStationDecisionLab/run-20261005-202722-040-852743/validation.json), 0 lỗi; 1 cảnh báo gồm 2 mẫu vận tốc native. Không yêu cầu mọi hành trình kết thúc ở mốc 300 s. |

Lượt mặc định có 60.624 dòng quỹ đạo trong 2.526 nhóm timestamp; mỗi nhóm chứa đủ 24 ID kể cả người đã kết thúc. Đã đối chiếu 20 sự kiện lên tàu với trạng thái mở cửa và hạn thực tế. Khoảng mẫu trung vị là **0,240 s**, lớn nhất **1,333 s**; lượt 1× tương ứng **0,212 s** và **0,271 s**. Phải dùng timestamp thật, không xem dữ liệu này là dãy đều mỗi 0,2 s.

Các vận tốc native thô được giữ nguyên: giá trị lớn nhất là **3,975 m/s** ở lượt mặc định và **7,840 m/s** ở lượt 1×. Hai lượt cuối đều qua ngưỡng kiểm liên tục vị trí giữa mẫu, nhưng kết quả không xác nhận giới hạn tốc độ từng frame hoặc giải thích nguyên nhân đỉnh vận tốc. Một lượt hủy sớm 4× trước sửa profile dịch vụ đã **FAIL kiểm liên tục vị trí**; [bằng chứng FAIL](../../StationFlowLab_Archive_20261020/Captures/TrainStationDecisionLab/run-20261005-201954-156-4a49b3/validation.json) vẫn được lưu, ngưỡng không được nới. So sánh lượt 1× mới với lượt cũ đồng thời thay profile nên không chứng minh 4× là nguyên nhân.

Các kết quả này vẫn bao gồm đình trệ và thử lại: lượt mặc định có 62 lần ngắt vì không tiến triển trong 25 giây, và 250/413 lần đánh giá trả về `None` để chờ/đánh giá lại. Trường `NavigationIssue` có thể giữ lỗi lịch sử dù hành khách đã phục hồi. Xem sự kiện theo thời gian để phân biệt người đang kẹt với người đã vượt qua vấn đề.

Validator dùng Python 3 và thư viện chuẩn, không cần Pillow:

```powershell
python Tests/validate_station_decision_capture.py Captures/TrainStationDecisionLab/run-20261005-232014-933-527a25 --require-all-finished --output Reports/TrainStationDecisionLab/capture-validation.json
```

Chạy sau khi đã dừng Play và log được ghi hết. Bỏ `--require-all-finished` nếu chủ động kết thúc quan sát trước khi mọi hành trình xong; các điều kiện tính toàn vẹn còn lại vẫn giữ nguyên. Validator báo đỉnh vận tốc tức thời dưới dạng cảnh báo, trong khi vi phạm liên tục vị trí giữa mẫu, mất ID, xác suất sai, lên tàu quá hạn hoặc trạng thái cuối thay đổi vẫn là lỗi. [Tự kiểm bằng dữ liệu giả](../../StationFlowLab_Archive_20261020/Reports/TrainStationDecisionLab/validator-self-tests.json) xác nhận bắt được 14 kiểu hỏng, gồm bước nhảy vị trí và thiếu bằng chứng thời hạn lên tàu; mẫu chỉ có đỉnh vận tốc vẫn được giữ và cảnh báo.

Các kết quả mô phỏng chỉ kiểm chứng cơ chế trong phạm vi kịch bản đã chạy. Để đánh giá độ tự nhiên hoặc cải thiện mô hình dự đoán quỹ đạo ngoài đời cần thiết kế thí nghiệm riêng, đối chiếu dữ liệu thực và báo cáo sai số/thời gian hành trình/độ đa dạng có điều kiện.
