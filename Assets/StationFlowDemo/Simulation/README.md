# Lõi mô phỏng giao thông và đám đông

`TrafficSimulation.cs` và `CrowdNavigation.cs` là một lớp C# partial độc lập Unity. Một đơn vị là một mét. `Step(dt)` chia thời gian thành bước tối đa 1/60 s. Cùng seed, quần thể và chuỗi timestep cho cùng kết quả trong cùng môi trường chạy; không dùng đồng hồ render hoặc `UnityEngine.Random`.

## Tích hợp và vòng đời

```csharp
var simulation = new TrafficSimulation(42, 96, 24);
simulation.VisibilityGuard = (x, z, radius) => CameraCanSee(x, z, radius);
simulation.Step(1f / 60f);
foreach (var agent in simulation.Agents)
{
    rendererPool[agent.EntityId].SetActive(agent.Active);
    // Khi Active: đặt vị trí X/Z, heading và animation theo vận tốc thực.
}
```

Danh sách `Agents` và `EntityId` là pool cố định. `Id` là tracking ID tăng đơn điệu, đổi khi một vòng đời mới được kích hoạt. Cả người và xe đều đi một hành trình hữu hạn: xuất phát từ ngoại vi, qua hoặc rẽ ở giao lộ, tiếp tục đi xa rồi kết thúc. Không quay đầu theo chu kỳ hay đặt lại vị trí trong vùng quan sát. `VisibilityGuard` trả về true nếu thay đổi vòng đời tại vị trí đó vẫn bị nhìn thấy; bộ mô phỏng trì hoãn cả xuất hiện và biến mất. Camera nhìn theo một đối tượng có thể giữ nó hoạt động và tiếp tục đi ra ngoài điểm đích. Điểm xuất hiện bắt đầu cách tâm 108 m, thử xa thêm từng 32 m tới 268 m nếu còn bị nhìn thấy. Renderer phải kiểm tra tất cả camera liên quan, gồm fisheye, khi cài guard.

Lúc tạo cảnh, vị trí ban đầu được phân bố trên các đoạn tiếp cận như một ảnh chụp trạng thái giao thông đang hoạt động. Sau đó, tất cả vòng đời mới đều đi qua kiểm tra visibility. Đối tượng nghỉ ngắn giữa các hành trình để dòng người và xe không có chu kỳ đồng loạt.

## Tìm đường và tương tác

Đồ thị gồm 28 nút, tám cổng ngoại vi, các đoạn vỉa hè nối với bốn góc phố và bốn vạch qua đường. A* chọn đường tới cổng đích. Mỗi hành trình có ý định `Corner`, `OneCrossing` hoặc `TwoCrossings`. Người rẽ ở góc phố tiếp tục trên vỉa hè ngay cả khi những người khác đang chờ đèn.

Người đi riêng có `GroupId=0`; cặp và nhóm ba có ID nhóm riêng, `GroupSize`, `GroupSlot` và `IsGroupLeader`. Nhóm đi cùng tốc độ thoải mái, giữ đội hình ngang và điều chỉnh tốc độ theo bạn đồng hành. Họ có thể tách đội hình để tránh vật cản; lúc qua vạch hẹp, nhóm thu thành hàng theo phía phải của chiều đi rồi dàn lại trên vỉa hè. Nếu một người lỡ pha xanh, những người đã qua đợi tại góc quảng trường ngoài lối đi (`Regrouping`) cho tới khi bạn qua xong. `Conversation`, `ConversationLookYaw`, `GestureBlend` điều khiển trò chuyện và cử chỉ theo những nhịp lệch nhau. `Looking`/`LookYaw` dành cho quan sát giao thông và có ưu tiên hơn trò chuyện.

Steering bám điểm nhìn trước trên đường đi, làm tròn các góc và giới hạn tốc độ quay thân 155 độ/s. Bộ chọn vận tốc dự đoán gặp nhau trong 2,2 s, xét các hướng/vận tốc ứng viên, giữ thiên hướng tránh bên phải và nhường nhau. Khi không tiến triển, người có thể bước ngang hoặc lùi để thoát thế kẹt. Lớp phanh liên tục chạy mỗi bước vật lý, xét người lân cận, vật cản và mép vùng đi được. Gia tốc thông thường tối đa 1,7 m/s², giảm tốc thường 2,5 m/s²; tránh khẩn cấp tối đa 4,5 m/s². Chặn hình học theo đường quét tròn là lớp bảo vệ cuối cùng; kiểm thử giám sát vận tốc thực để phát hiện những lần phải chặn đột ngột.

Vật cản tĩnh gồm các thân cây, cột đèn, cột tín hiệu và bollard còn nằm ngoài cửa vạch qua đường. Có thể thêm footprint tròn bằng `AddPedestrianObstacle(x,z,radius)`. Hành lang vỉa hè chính có tâm x hoặc z gần ±11,2 m, mở rộng tại quảng trường góc tới ±14 m. Đồ đạc bổ sung cần đồng bộ footprint giữa world và navigation.

## Tín hiệu và xe

Xe chạy bên phải, đi thẳng hoặc rẽ phải theo cung bán kính 6 m. Vận tốc cua tối đa 4,2 m/s, tăng tốc 1,9 m/s² và phanh 3,6 m/s². Bám xe xét cả tuyến đến, cung rẽ và làn đầu ra chung. Xe có thời gian phản ứng, đèn phanh và xi-nhan.

Pha xe Bắc–Nam và Đông–Tây lần lượt có xanh 22 s, vàng 3 s và tất cả đỏ ít nhất 2 s. Pha người đi bộ riêng có cửa vào 12 s; không nhận người mới ở 3,5 s cuối. Người chỉ cam kết khi đã đến đúng phía vạch và được phép đi. Pha WALK đợi người đã được nhận bước vào đường, rồi chuyển sang clearance tới khi họ ra khỏi toàn bộ vạch. Các pha tất cả đỏ cũng đợi xe cũ thoát vùng xung đột. Xe rẽ không đồng thời có quyền đi cùng người đi bộ.

## Đo lường và kiểm thử

`CompletedPedestrianTrips` đếm hành trình người tới ngoại vi đích; `CompletedPedestrianCrossings` đếm từng lần qua một vạch. `CompletedVehicleTrips` và `CompletedRightTurns` đếm xe thoát giao lộ. Bộ đếm spawn/despawn tách biệt với những lượt đi qua giao lộ. `RedLightViolations` đo đầu xe vượt vạch không có quyền; `PedestrianRedLightViolations` đo người thực sự bước vào đường khi không ở pha WALK.

`Collisions` đếm cặp thân mới chồng lấn: hộp xoay XZ cho xe, đĩa bán kính 0,27 m cho người, SAT và phép kiểm tra đĩa–hộp. Nó không thay thế va chạm mesh hoặc tiếp xúc tay chân. Không sửa trực tiếp `Agents` trong lúc chạy.

```text
dotnet run --project Tests/Simulation/SimulationTests.csproj -c Release
```

Kiểm thử độc lập đo chuyển động và gia tốc thực, tín hiệu, lưu lượng dài hạn, người ban đầu hoàn thành hành trình, thời gian giữ pha, dòng ngược chiều, tránh vật cản, đội nhóm và tái hợp, cổng xuất phát/đích, ID liên tục, xuất hiện/biến mất ngoài camera, và tái lập chính xác. Chất lượng hình ảnh và animation cần thêm GPU QA trong bản Unity.

Đây là mô phỏng hành vi có kiểm soát phục vụ demo. Chưa hiệu chỉnh phân bố hành vi bằng khảo sát thực; không mô phỏng chen lấn có tiếp xúc, người vi phạm đèn, xe rẽ trái, xe đạp hoặc hội thoại ngôn ngữ thật. Giới hạn này không làm thay đổi những kiểm thử về an toàn, liên tục và lưu lượng.
