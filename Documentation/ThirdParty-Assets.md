# Third-party assets

Downloaded 2026-09-21 directly from Poly Haven's public API/CDN for the StationFlow Unity demonstration. Asset data are CC0-1.0. Original file hashes and download URLs are recorded in `asset-manifest.json`; each source MD5 was verified after download. No Unity Asset Store or Mixamo content was downloaded or integrated.

| Resource basename | Poly Haven asset | Author | Tile width | Files |
|---|---|---|---|---|
| asphalt | [Aerial Asphalt 01](https://polyhaven.com/a/aerial_asphalt_01) | Rob Tuytel | 30 m | diffuse JPG, normal GL PNG, roughness JPG; 1K |
| pavement | [Pavement 01](https://polyhaven.com/a/pavement_01) | Rob Tuytel | 1.5 m | diffuse JPG, normal GL PNG, roughness JPG; 1K |
| brick | [Brick Wall 001](https://polyhaven.com/a/brick_wall_001) | Dimitrios Savva (photography), Rob Tuytel (processing) | 3 m | diffuse JPG, normal GL PNG, roughness JPG; 1K |

Files live in `Assets/StationFlowDemo/Art/Resources/StationFlowArt`. Unity resource names are `StationFlowArt/asphalt_diffuse`, `StationFlowArt/asphalt_normal`, `StationFlowArt/asphalt_roughness`, and the equivalent `pavement_*` / `brick_*` names.

Diffuse uses sRGB. Normal maps are tangent-space OpenGL convention, imported as normal maps with sRGB disabled and green-channel flip disabled. Roughness maps use linear import; Unity URP Lit expects smoothness, so roughness must be inverted/packed appropriately if used. All source maps have mipmaps and 8× anisotropic sampling. Actual material assignment should be verified in the final scene. These photographic textures improve surface detail; they do not make procedural people or vehicles photorealistic.

[Poly Haven asset license](https://polyhaven.com/license) and [CC0-1.0 dedication](https://creativecommons.org/publicdomain/zero/1.0/) permit reuse and redistribution of the downloaded assets. The [API terms](https://github.com/Poly-Haven/Public-API/blob/master/ToS.md) govern the API separately. Download requests used the identifying User-Agent `StationFlowUnityDemo/1.0 (CC0 research asset download)`. The application uses bundled files and does not call the live API. Credit is retained for provenance without implying endorsement.

Candidates investigated but **not bundled**: [Quaternius Universal Base Characters](https://quaternius.com/packs/universalbasecharacters.html), [Quaternius Universal Animation Library](https://quaternius.com/packs/universalanimationlibrary.html), and [MakeHuman core exports](https://static.makehumancommunity.org/makehuman/faq/can_i_sell_models_created_with_makehuman.html). Their public pages identify CC0 use. Quaternius's download flow required an itch.io browser challenge; no archive was obtained. Future community clothing, textures and animation downloads must have their own provenance recorded.

Engine notice: StationFlow Intersection Lab was made with Unity®. Unity is a trademark or registered trademark of Unity Technologies. Copyright © 2005-2026 Unity Technologies. All rights reserved.
