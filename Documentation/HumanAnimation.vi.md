# Human Animation — thư viện Mixamo dùng chung

## Mở và kiểm tra

Chọn **Efrei Research Lab → Human Animation → Open animation lab**. Scene chính là `Assets/HumanAnimation/Scenes/MixamoAnimationLab.unity`: X Bot của Mixamo và mannequin Tall có hai bộ xương/Avatar riêng, cùng tham chiếu một thư viện và Animator Controller. Camera, đèn, UI, prefab nhân vật và các tham chiếu đều được lưu trong scene, nhìn thấy trong Hierarchy ở Edit mode.

- **Không cần Play:** chọn root `Human_XBot_Mixamo` hoặc `Human_Tall_Mixamo`; trong component `HumanAnimationPlayer`, chọn **Motion**, kéo thanh thời gian hoặc chạy **Play preview**. **Stop / restore pose** trả model về tư thế đã lưu. Cũng có thể mở prefab trong Prefab Mode để preview.
- **Trong Play:** chọn Previous/Next rồi Play on both models; Pause, Resume, Rewind và 0.5x/1x/2x dùng các callback đã lưu trên Button. Scene không tự chọn hành vi; controller bắt đầu ở trạng thái `Ready`.
- Scene `HumanAnimationLab.unity` là phòng kiểm thử riêng với hai mannequin Standard/Tall và ba clip diagnostic do dự án tạo. Các clip đó được ghi rõ nguồn Diagnostic, không phải Mixamo.

## Tài nguyên bàn giao

| Vị trí trong `Assets/HumanAnimation` | Nội dung |
| --- | --- |
| `Source/Mixamo/Animations` | FBX gốc tải từ Mixamo; gồm gói Locomotion và các clip bổ sung. |
| `Source/Mixamo/Models/XBot.fbx` | Model X Bot ở T-pose, Avatar tạo từ chính model; không dùng T-pose như một motion trong thư viện. |
| `Source/Mixamo/mixamo-import-manifest.json` | ID, nguồn, SHA-256, đường dẫn, thời lượng, loại clip, loop và thiết lập root cho từng motion. |
| `Resources/HumanAnimation/Mixamo/MixamoLibrary.asset` | Catalog 19 clip Mixamo với tham chiếu asset trực tiếp; có thể load bằng `Resources.Load<HumanAnimationLibrary>("HumanAnimation/Mixamo/MixamoLibrary")`. |
| `Resources/HumanAnimation/Mixamo/MixamoLibrary.controller` | Animator graph lưu sẵn: Ready và các state tương ứng clip; không có script tạo graph lúc chạy. |
| `Resources/HumanAnimation/Mixamo/Clips` | Các `.anim` Humanoid độc lập được trích từ FBX, tái sử dụng được. |
| `Resources/HumanAnimation/Masks/UpperBody.mask` | Mask thân trên để dùng khi bổ sung layer sau này; chưa tự gán lớp hành vi. |
| `Prefabs/Human_XBot_Mixamo.prefab` | Root playback + child model X Bot/Animator/Avatar riêng, vật liệu URP. |
| `Prefabs/Human_Tall_Mixamo.prefab` | Cùng controller/library trên mannequin có tỷ lệ và tên xương khác. |
| `Prefabs/MixamoRetargetWorkbench.prefab` | Toàn bộ bàn kiểm tra: hai nhân vật, UI, camera, ánh sáng và sân thử. |
| `Prefabs/Diagnostics`, `Prefabs/Human_*_Diagnostic.prefab` | Model/prefab kiểm thử riêng và thư viện diagnostic. |
| `Runtime`, `Editor` | Playback API, preview Inspector, importer, authoring và công cụ kiểm tra. |

19 clip hiện tại: Idle, Neutral Idle, Walking, **Male Standard Walk**, Running, Left/Right Strafe, Left/Right Strafe Walking, Left/Right Turn, Left/Right Turn 90, Jump, Sitting Idle, Sit Down, Stand Up, Talking, Waving. Danh mục và ID chính xác được lưu trong `MixamoLibrary.asset` và manifest nguồn. Clip Walking cũ được giữ lại để tái sử dụng và so sánh; TrainStation hiện dùng Male Standard Walk, ID `mixamo_male_standard_walking_ef7424170f`. Thư viện riêng của nhà ga có thêm Station Sweeping, tổng cộng 20 clip.

## Dùng với model khác

1. Import model vào Unity. Trong Rig, chọn **Humanoid**, **Create From This Model**, kiểm tra mapping và T-pose trong Configure. Mỗi model phải có Avatar riêng hợp lệ; không gán Avatar của X Bot cho một bộ xương khác.
2. Chọn model/prefab trong Project, rồi **Efrei Research Lab → Human Animation → Create reusable prefab from selected Humanoid**. Công cụ tạo một prefab mới, nối Animator của model với thư viện Mixamo và giữ nguyên Avatar của model.
3. Đặt prefab vào scene kiểm thử và preview các clip. Tỷ lệ thân, quần áo, vị trí ghế/đạo cụ hoặc độ dài tay có thể cần hiệu chỉnh theo model và tương tác; thư viện không tự giải quyết những yêu cầu đó.

Ví dụ gọi playback từ hệ thống kịch bản sau này:

```csharp
using StationFlow.HumanAnimation;

// player là component đã lưu trên prefab; ID lấy từ catalog.
player.Play("mixamo_male_standard_walking_ef7424170f");
player.SetPlaybackSpeed(0.5f);
player.Pause();
player.Resume();
player.Rewind();
```

`Play` trả về `false` và điền `LastError` khi thiếu motion/Avatar/state, thay vì dùng một animation thay thế. Chuyển clip dùng crossfade với `blendIn` trong catalog; không có NavMesh, input điều khiển người, lịch hành vi hay tự sinh nhân vật.

## Thêm hoặc điều chỉnh clip Mixamo

Tải **FBX Binary**, **Without Skin**, **30 fps**, **Keyframe Reduction: none**. Với clip di chuyển, dùng tùy chọn In Place của Mixamo khi có; thư viện hiện mặc định preview tại chỗ. Cho model T-pose vào thư mục con `Models`, các motion vào `Animations`.

Chọn **Import Mixamo FBX folder** và thư mục nguồn. Công cụ sao chép FBX vào dự án, tạo Avatar từ từng nguồn, kiểm tra Humanoid, trích clip và cập nhật catalog/controller. Không có AssetPostprocessor tự sửa các model ngoài thư mục này. File trùng tên nhưng khác nội dung được giữ dưới tên khác.

Có thể chỉnh `loop`, `loopPose`, `category`, `blendIn`, `inPlace`, `bakeRootRotation`, `bakeRootY` trong manifest rồi dùng **Reapply manifest options** ở cửa sổ import. Các file `.anim` được cập nhật giữ GUID. `kind: model` hoặc thư mục `Models` loại source T-pose khỏi catalog motion. Khi rebuild, state/metadata do importer quản lý được đồng bộ; các scene/prefab đã có không bị tái tạo.

`applyRootMotion` trên prefab mặc định tắt. `HumanAnimationPlayer` lưu tùy chọn `stabilizeFeet` và áp dụng vào Animator để ổn định chân khi chuyển state; state đi bộ mặc định của nhà ga đã bật Foot IK. Clip Jump giữ dữ liệu root Y trong nguồn; việc áp dụng chuyển vị, tốc độ thực, xoay nhân vật, khớp ghế và tương tác đạo cụ do hệ thống hành vi điều khiển. Mask thân trên đã có asset nhưng chưa có lớp gesture tự động.

Male Standard Walk được tải dưới tiêu đề **Walking**, mô tả **Male Standard Walk**, dùng X Bot, 30 fps, Without Skin, không giảm keyframe và không bật In Place khi tải. Chuyển vị nguồn được giữ để đo độ dài chu kỳ; clip dùng với NavMesh được bake chuyển vị ngang để phát tại chỗ. [Bản ghi nguồn tải và SHA-256](../../StationFlowLab_Archive_20261020/Reports/TrainStationSocial/GaitAudit/download-source.json) được lưu cùng [báo cáo đánh giá tư thế](../../StationFlowLab_Archive_20261020/Reports/TrainStationSocial/GaitAudit/gait-pose-audit.json). Vận tốc tham chiếu trên X Bot là 1,091139078 m/s tại `humanScale = 1,05003643`, với chu kỳ 1,23333335 s. [Hướng dẫn TrainStation](TrainStation-Social.vi.md) mô tả cách đồng bộ vận tốc NavMesh trước khi Animator tính tư thế.

## Kiểm tra và nguồn

Các menu **Validate saved resources**, **Check actual Play mode retargeting** và **Check Inspector preview and pose restoration** kiểm tra asset, chạy Play thật và kiểm tra preview Inspector. Lưu scene đang chỉnh trước khi chạy; công cụ không tự lưu hoặc bỏ thay đổi của người dùng. Kết quả trong `Reports/HumanAnimation`.

FBX được tải trực tiếp từ phiên Mixamo người dùng đã đăng nhập ngày 28/09/2026. Mannequin Standard/Tall và ba diagnostic clip là tài nguyên gốc của dự án. [Mixamo](https://www.mixamo.com/) cung cấp nguồn nhân vật/chuyển động; [tài liệu Unity về Humanoid retargeting](https://docs.unity.com/en-us/engine/6000.0/manual/animation-section/animation-mecanim/avatar-creationand-setup/retargeting) mô tả việc dùng cùng clip/controller với Avatar riêng của từng model. Thông tin dịch vụ và sử dụng nguồn nằm tại [Mixamo FAQ của Adobe](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html).
