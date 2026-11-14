#if UNITY_EDITOR
using UnityEngine;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Editor-authored, static terminus infrastructure and public forecourt.</summary>
    public static partial class TrainStationBuilder
    {
        static void BuildRail(Transform root)
        {
            root = Group("03 Rail infrastructure — quais et voies", root);
            Transform ground = Group("01 - Rail corridor / Terrassement", root);
            Box("Rail corridor foundation", ground, new Vector3(0, -1.35f, 133), new Vector3(98, .55f, 178), Mat("ballast"));
            Box("West service walkway", ground, new Vector3(-49.5f, -.06f, 132), new Vector3(3, .12f, 178), Mat("floor"));
            Box("East service walkway", ground, new Vector3(49.5f, -.06f, 132), new Vector3(3, .12f, 178), Mat("floor"));
            Box("Rear service crossing", ground, new Vector3(0, -.06f, 218), new Vector3(102, .12f, 8), Mat("floor"));
            for (int side = -1; side <= 1; side += 2)
            {
                Box((side < 0 ? "West" : "East") + " railway boundary wall", ground, new Vector3(side * 52.1f, .55f, 133), new Vector3(.35f, 1.1f, 178), Mat("stone"));
                for (int z = 46; z <= 218; z += 8)
                {
                    Box("Boundary pier " + side + " / " + z, ground, new Vector3(side * 52.1f, 1.3f, z), new Vector3(.5f, 2.6f, .5f), Mat("stone"));
                    Box("Boundary fence " + side + " / " + z, ground, new Vector3(side * 52.1f, 1.75f, z + 3.9f), new Vector3(.07f, 1.25f, 7.7f), Mat("steel"), false);
                }
            }

            Transform tracks = Group("02 - Eight terminus tracks / Voies 1 a 8", root);
            // 1.7 m from each platform edge to the nearest track centre; 1.435 m gauge.
            float[] trackCentres = { -41.7f, -30.3f, -17.7f, -6.3f, 6.3f, 17.7f, 30.3f, 41.7f };
            for (int track = 0; track < trackCentres.Length; track++)
            {
                float x = trackCentres[track];
                Transform line = Group("Voie " + (track + 1).ToString("00") + " - Static rail infrastructure", tracks);
                Box("Ballast bed", line, new Vector3(x, -.99f, 130), new Vector3(4.8f, .38f, 168), Mat("ballast"));
                Transform sleepers = Group("Concrete sleepers / Traverses", line);
                for (int s = 0; s < 122; s++)
                    Box("Sleeper " + s.ToString("000"), sleepers, new Vector3(x, -.795f, 48 + s * 1.36f), new Vector3(2.6f, .18f, .24f), Mat("concrete"), false);
                for (int side = -1; side <= 1; side += 2)
                {
                    // Inner rail-head faces are 1.435 m apart (heads are 75 mm wide).
                    float railX = x + side * .755f;
                    Box("Running rail " + side, line, new Vector3(railX, -.65f, 130), new Vector3(.075f, .13f, 168), Mat("rail"));
                    Box("Rail foot " + side, line, new Vector3(railX, -.737f, 130), new Vector3(.15f, .035f, 168), Mat("steel"), false);
                }
                Transform buffer = Group("Buffer stop / Heurtoir", line);
                Box("Buffer beam", buffer, new Vector3(x, .15f, 48.35f), new Vector3(2.7f, .48f, .5f), Mat("red"));
                Box("White reflector", buffer, new Vector3(x, .17f, 48.08f), new Vector3(1.65f, .13f, .02f), Mat("white"), false);
                for (int side = -1; side <= 1; side += 2)
                {
                    Box("Buffer support " + side, buffer, new Vector3(x + side * .8f, -.17f, 48.45f), new Vector3(.23f, 1.0f, 1.35f), Mat("steel"));
                    Beam("Braced stop " + side, buffer, new Vector3(x + side * .8f, -.62f, 50.1f), new Vector3(x + side * .8f, .3f, 48.4f), .2f, Mat("steel"));
                    Box("Buffer face " + side, buffer, new Vector3(x + side * .78f, .15f, 48.02f), new Vector3(.38f, .38f, .17f), Mat("dark"));
                }
                Box("Track number plaque", line, new Vector3(x, 1.45f, 46.4f), new Vector3(1.8f, .9f, .13f), Mat("blue"));
                Label("Track number", line, "VOIE " + (track + 1), new Vector3(x, 1.48f, 46.31f), .45f, Color.white);
                Box("Track plaque support", line, new Vector3(x, .37f, 46.45f), new Vector3(.12f, 1.35f, .12f), Mat("steel"));
            }

            Transform platforms = Group("03 - Four island platforms / Quais", root);
            float[] platformCentres = { -36, -12, 12, 36 };
            for (int p = 0; p < platformCentres.Length; p++)
            {
                float x = platformCentres[p];
                Transform platform = Group("Quai " + (p + 1) + " - Voies " + (2 * p + 1) + " et " + (2 * p + 2), platforms);
                Box("Platform structural slab", platform, new Vector3(x, -.435f, 129), new Vector3(8, .73f, 170), Mat("concrete"));
                Box("Walking surface", platform, new Vector3(x, -.035f, 129), new Vector3(7.9f, .07f, 170), Mat("floor"));
                Transform edge = Group("Platform edges and tactile guidance", platform);
                for (int side = -1; side <= 1; side += 2)
                {
                    Box("Edge coping " + side, edge, new Vector3(x + side * 3.84f, .018f, 129), new Vector3(.32f, .065f, 170), Mat("white"));
                    Box("Tactile warning strip " + side, edge, new Vector3(x + side * 3.28f, .017f, 129), new Vector3(.55f, .038f, 170), Mat("yellow"), false);
                    for (int rib = 0; rib < 3; rib++)
                        Box("Tactile continuous rib " + side + " / " + rib, edge, new Vector3(x + side * (3.07f + rib * .18f), .042f, 129), new Vector3(.035f, .012f, 170), Mat("stone"), false);
                    for (int j = 0; j < 42; j++)
                        Box("Coping joint " + side + " / " + j, edge, new Vector3(x + side * 3.84f, .054f, 46 + j * 4), new Vector3(.32f, .006f, .018f), Mat("dark"), false);
                }
                Box("Central tactile route", edge, new Vector3(x, .015f, 129), new Vector3(.4f, .03f, 167), Mat("stone"), false);

                Transform roofs = Group("Roofs - Platform canopies / Hide for top view", platform);
                Transform structure = Group("Canopy steel structure", platform);
                for (int bay = 0; bay < 6; bay++)
                {
                    float z = 68 + bay * 25;
                    Box("Canopy column " + bay, structure, new Vector3(x, 2.72f, z), new Vector3(.22f, 5.44f, .28f), Mat("steel"));
                    Box("Column plinth " + bay, structure, new Vector3(x, .22f, z), new Vector3(.46f, .44f, .52f), Mat("stone"));
                    Box("Canopy crossbeam " + bay, structure, new Vector3(x, 5.36f, z), new Vector3(8.7f, .22f, .18f), Mat("steel"));
                    Beam("Canopy brace L " + bay, structure, new Vector3(x, 3.9f, z), new Vector3(x - 3.1f, 5.37f, z), .12f, Mat("steel"));
                    Beam("Canopy brace R " + bay, structure, new Vector3(x, 3.9f, z), new Vector3(x + 3.1f, 5.37f, z), .12f, Mat("steel"));
                    Box("Warm platform light " + bay, structure, new Vector3(x + 2.4f, 5.15f, z + 6), new Vector3(.2f, .1f, 3.2f), Mat("light"), false);
                    Box("Warm platform light opposite " + bay, structure, new Vector3(x - 2.4f, 5.15f, z + 6), new Vector3(.2f, .1f, 3.2f), Mat("light"), false);
                }
                for (int bay = 0; bay < 7; bay++)
                {
                    float z = 64 + bay * 22;
                    Box("Canopy roof panel " + bay, roofs, new Vector3(x, 5.59f, z), new Vector3(8.65f, .18f, 21.7f), Mat("roof"));
                    Box("Roof centre skylight " + bay, roofs, new Vector3(x, 5.705f, z), new Vector3(1.3f, .055f, 19.5f), Mat("glass"), false);
                }
                for (int side = -1; side <= 1; side += 2)
                {
                    Box("Roof fascia " + side, roofs, new Vector3(x + side * 4.3f, 5.56f, 130), new Vector3(.09f, .34f, 154), Mat("steel"));
                    Box("Roof gutter " + side, roofs, new Vector3(x + side * 4.37f, 5.4f, 130), new Vector3(.15f, .1f, 154), Mat("dark"));
                }

                Transform furniture = Group("Platform furniture and passenger information", platform);
                for (int zone = 0; zone < 5; zone++)
                {
                    float z = 70 + zone * 29;
                    RailBench(furniture, x - 1.05f, z, true);
                    RailBench(furniture, x + 1.05f, z, false);
                    RailBin(furniture, new Vector3(x + 1.3f, 0, z + 4.3f));
                    RailPlatformSign(furniture, x, z + 8, p * 2 + 1, p * 2 + 2, ((char)('A' + zone)).ToString());
                }
                Transform equipment = Group("Help points and equipment", platform);
                Box("Information totem body", equipment, new Vector3(x - 1.3f, 1.2f, 53), new Vector3(1.15f, 2.4f, .35f), Mat("steel"));
                Box("Information map panel", equipment, new Vector3(x - 1.3f, 1.42f, 52.81f), new Vector3(.98f, 1.48f, .025f), Mat("white"), false);
                Label("Platform map heading", equipment, "PLAN DU QUAI", new Vector3(x - 1.3f, 2.02f, 52.78f), .115f, new Color(.04f, .12f, .28f));
                for (int r = 0; r < 7; r++)
                    Box("Timetable line " + r, equipment, new Vector3(x - 1.3f, 1.73f - r * .135f, 52.78f), new Vector3(.71f, .032f, .01f), Mat(r % 3 == 0 ? "blue" : "stone"), false);
                Box("Emergency call point", equipment, new Vector3(x + 1.4f, 1.05f, 56), new Vector3(.5f, 2.1f, .3f), Mat("blue"));
                Box("Help point face", equipment, new Vector3(x + 1.4f, 1.43f, 55.835f), new Vector3(.36f, .55f, .03f), Mat("white"), false);
                Label("Help point label", equipment, "SOS", new Vector3(x + 1.4f, 1.46f, 55.805f), .18f, new Color(.7f, .04f, .16f));
                Cylinder("Help button", equipment, new Vector3(x + 1.4f, 1.0f, 55.81f), .055f, .04f, Mat("red"), false).transform.localRotation = Quaternion.Euler(90, 0, 0);
                Box("Platform utility cabinet", equipment, new Vector3(x + 1.6f, .6f, 209), new Vector3(.8f, 1.2f, .6f), Mat("steel"));
                Box("End safety rail", equipment, new Vector3(x, .68f, 212), new Vector3(6, .08f, .08f), Mat("steel"));
                for (int k = -1; k <= 1; k++)
                    Box("End safety rail post " + k, equipment, new Vector3(x + k * 3, .36f, 212), new Vector3(.08f, .72f, .08f), Mat("steel"));
            }

            Transform power = Group("04 - Overhead line equipment / Catenaires", root);
            for (int bay = 0; bay < 7; bay++)
            {
                float z = 59 + bay * 25;
                for (int side = -1; side <= 1; side += 2)
                {
                    Box("Catenary mast " + bay + " / " + side, power, new Vector3(side * 47.3f, 3.35f, z), new Vector3(.27f, 8.3f, .3f), Mat("steel"));
                    Box("Mast footing " + bay + " / " + side, power, new Vector3(side * 47.3f, -.45f, z), new Vector3(.9f, .7f, .9f), Mat("concrete"));
                }
                Box("Portal gantry " + bay, power, new Vector3(0, 7.5f, z), new Vector3(95, .18f, .2f), Mat("steel"));
                foreach (float trackX in trackCentres)
                {
                    Box("Overhead dropper " + bay + " / " + trackX, power, new Vector3(trackX, 6.45f, z), new Vector3(.04f, 2.1f, .04f), Mat("dark"), false);
                    Box("Insulator " + bay + " / " + trackX, power, new Vector3(trackX, 7.21f, z), new Vector3(.16f, .32f, .16f), Mat("white"), false);
                }
            }
            foreach (float trackX in trackCentres)
                Box("Contact wire / " + trackX, power, new Vector3(trackX, 5.4f, 130), new Vector3(.026f, .026f, 168), Mat("dark"), false);

            Transform safety = Group("05 - Service access and emergency routes", root);
            for (int side = -1; side <= 1; side += 2)
            {
                for (int z = 75; z <= 210; z += 45)
                {
                    Box("Exit sign post " + side + " / " + z, safety, new Vector3(side * 50, 1.2f, z), new Vector3(.1f, 2.4f, .1f), Mat("steel"));
                    Box("Emergency direction sign " + side + " / " + z, safety, new Vector3(side * 50, 2.15f, z), new Vector3(1.8f, .55f, .08f), Mat("green"));
                    Label("Emergency exit legend " + side + " / " + z, safety, "SORTIE  >", new Vector3(side * 50, 2.17f, z - .05f), .21f, Color.white);
                }
                Box("Rear access gate post " + side, safety, new Vector3(side * 51.8f, 1.3f, 222), new Vector3(.45f, 2.6f, .45f), Mat("stone"));
            }
            Box("Rear service gate left", safety, new Vector3(-29, 1.05f, 222), new Vector3(44, 2.1f, .12f), Mat("steel"));
            Box("Rear service gate right", safety, new Vector3(29, 1.05f, 222), new Vector3(44, 2.1f, .12f), Mat("steel"));
            Box("Rear staff exit marker", safety, new Vector3(0, 2.9f, 221), new Vector3(7, .8f, .12f), Mat("green"));
            Label("Rear staff exit text", safety, "ACCES TECHNIQUE / SECOURS", new Vector3(0, 2.92f, 220.91f), .29f, Color.white);
        }

        static void BuildExterior(Transform root)
        {
            root = Group("04 Exterior — parvis et ville", root);
            Transform plaza = Group("01 - Station forecourt / Parvis de la gare", root);
            Box("District ground / Continuous base 350 x 450 m", plaza, new Vector3(0, -1.6f, 70), new Vector3(350, .6f, 450), Mat("stone"));
            Box("Urban ground foundation", plaza, new Vector3(0, -.34f, -56), new Vector3(144, .6f, 58), Mat("concrete"));
            Box("Pedestrian forecourt", plaza, new Vector3(0, -.075f, -45), new Vector3(112, .15f, 34), Mat("floor"));
            for (int x = -54; x <= 54; x += 6)
                Box("Paving joint X " + x, plaza, new Vector3(x, .006f, -45), new Vector3(.025f, .012f, 33.6f), Mat("stone"), false);
            for (int z = -60; z <= -30; z += 6)
                Box("Paving joint Z " + z, plaza, new Vector3(0, .006f, z), new Vector3(111, .012f, .025f), Mat("stone"), false);
            Box("Main accessible tactile route", plaza, new Vector3(0, .016f, -44), new Vector3(.55f, .032f, 31), Mat("stone"), false);
            Box("West accessible route", plaza, new Vector3(-25, .016f, -51), new Vector3(49.5f, .032f, .55f), Mat("stone"), false);
            Box("East accessible route", plaza, new Vector3(25, .016f, -51), new Vector3(49.5f, .032f, .55f), Mat("stone"), false);
            Box("Forecourt curb west", plaza, new Vector3(-32, .085f, -62.2f), new Vector3(45, .17f, .35f), Mat("stone"));
            Box("Forecourt curb east", plaza, new Vector3(32, .085f, -62.2f), new Vector3(45, .17f, .35f), Mat("stone"));
            Box("Pedestrian lowered curb", plaza, new Vector3(0, .025f, -62.2f), new Vector3(12, .05f, .35f), Mat("stone"));
            for (int side = -1; side <= 1; side += 2)
            {
                Transform court = Group((side < 0 ? "West" : "East") + " service court and accessible side route", plaza);
                // These adjoining slabs stop at the main hall/gallery footprints; no interior overlap.
                Box("Paved side court", court, new Vector3(side * 49.5f, -.1f, 0), new Vector3(27, .2f, 56), Mat("floor"));
                Box("Gallery emergency exit apron", court, new Vector3(side * 55.5f, -.1f, 36), new Vector3(15, .2f, 16), Mat("floor"));
                Box("Rail-side public path", court, new Vector3(side * 57.5f, -.1f, 56.5f), new Vector3(11, .2f, 25), Mat("floor"));
                Box("Forecourt outer path link", court, new Vector3(side * 59.5f, -.1f, -45), new Vector3(7, .2f, 34), Mat("floor"));
                Box("Side service lane surface", court, new Vector3(side * 42.25f, .004f, 0), new Vector3(7.5f, .008f, 49), Mat("concrete"), false);
                Box("Continuous accessible path to emergency exit", court, new Vector3(side * 49.5f, .008f, 8), new Vector3(3, .016f, 72), Mat("stone"), false);
                for (int joint = -24; joint <= 24; joint += 8)
                {
                    Box("Court paving joint " + joint, court, new Vector3(side * 55, .008f, joint), new Vector3(15.6f, .016f, .025f), Mat("concrete"), false);
                    Box("Service lane marking " + joint, court, new Vector3(side * 42.25f, .011f, joint), new Vector3(.12f, .006f, 2.7f), Mat("white"), false);
                }
                for (int n = 0; n < 4; n++)
                {
                    float z = -38 - n * 6;
                    Cylinder("Forecourt bollard " + side + " / " + n, plaza, new Vector3(side * 12, .49f, z), .1f, .98f, Mat("steel"));
                    Cylinder("Bollard reflector " + side + " / " + n, plaza, new Vector3(side * 12, .85f, z), .107f, .1f, Mat("white"), false);
                }
            }

            Transform landscape = Group("02 - Landscape / Arbres et mobilier", root);
            for (int side = -1; side <= 1; side += 2)
            {
                for (int row = 0; row < 3; row++)
                {
                    float x = side * (22 + row * 13);
                    RailPlanterTree(landscape, x, -43, 1.0f + row * .08f);
                    RailBench(landscape, x, -48.5f, false);
                }
                RailPlanterTree(landscape, side * 59, -17, 1.2f);
                RailPlanterTree(landscape, side * 59, 12, 1.15f);
                RailPlanterTree(landscape, side * 59, 38, 1.2f);
                for (int j = 0; j < 4; j++)
                    RailStreetlight(landscape, side * (18 + j * 12), -59);
                RailBin(landscape, new Vector3(side * 17, 0, -49));
                RailBin(landscape, new Vector3(side * 50, 0, -49));
            }
            Transform identity = Group("03 - SNCF station identity and local information", root);
            Box("SNCF monument plinth", identity, new Vector3(-8, .14f, -52), new Vector3(2.7f, .28f, 1.2f), Mat("stone"));
            Box("SNCF monument blade", identity, new Vector3(-8, 3.2f, -52), new Vector3(2.2f, 6.15f, .36f), Mat("blue"));
            Box("SNCF brand field", identity, new Vector3(-8, 5.5f, -52.2f), new Vector3(2.05f, 1.45f, .055f), Mat("magenta"), false);
            Label("SNCF exterior wordmark", identity, "SNCF", new Vector3(-8, 5.52f, -52.24f), .6f, Color.white);
            Label("Station identity text", identity, "GARE\nDE VALMONT", new Vector3(-8, 3.1f, -52.22f), .27f, Color.white);
            Label("Modes identity", identity, "TGV INOUI\nTER\nINTERCITES", new Vector3(-8, 1.5f, -52.22f), .19f, Color.white);
            Box("Area map case", identity, new Vector3(8, 1.42f, -52), new Vector3(2.3f, 2.84f, .27f), Mat("steel"));
            Box("Area map paper", identity, new Vector3(8, 1.54f, -52.15f), new Vector3(2.05f, 2.3f, .035f), Mat("white"), false);
            Label("Area map title", identity, "BIENVENUE\nPlan du quartier", new Vector3(8, 2.33f, -52.18f), .24f, new Color(.035f, .1f, .25f));
            for (int i = 0; i < 4; i++)
            {
                Box("Map street horizontal " + i, identity, new Vector3(8, 1.0f + i * .23f, -52.182f), new Vector3(1.65f, .055f, .012f), Mat("stone"), false);
                Box("Map street vertical " + i, identity, new Vector3(7.38f + i * .4f, 1.34f, -52.19f), new Vector3(.055f, 1, .012f), Mat("stone"), false);
            }
            Box("Map station highlight", identity, new Vector3(8, 1.34f, -52.21f), new Vector3(.5f, .25f, .014f), Mat("magenta"), false);

            Transform transport = Group("04 - Intermodal street / Bus taxis velos", root);
            Box("Drop-off lane", transport, new Vector3(0, -.04f, -68), new Vector3(144, .08f, 11.4f), Mat("asphalt"));
            Box("Outer pavement", transport, new Vector3(0, .0f, -80), new Vector3(144, .16f, 12), Mat("floor"));
            Box("Outer curb", transport, new Vector3(0, .09f, -74.1f), new Vector3(144, .18f, .28f), Mat("stone"));
            for (int x = -65; x <= 65; x += 7)
                Box("Lane dash " + x, transport, new Vector3(x, .009f, -69.2f), new Vector3(3, .018f, .12f), Mat("white"), false);
            for (int cross = -2; cross <= 2; cross++)
                Box("Pedestrian crossing stripe " + cross, transport, new Vector3(cross * 1.5f, .014f, -68), new Vector3(.85f, .02f, 11), Mat("white"), false);
            for (int side = -1; side <= 1; side += 2)
            {
                Box("Crossing tactile band " + side, transport, new Vector3(0, .027f, side < 0 ? -61.8f : -74.5f), new Vector3(7.2f, .038f, .6f), Mat("yellow"), false);
                for (int stall = 0; stall < 4; stall++)
                {
                    float x = side * (17 + stall * 7);
                    Box("Drop off stall front " + side + " / " + stall, transport, new Vector3(x, .012f, -63.8f), new Vector3(.09f, .02f, 2.6f), Mat("white"), false);
                    Box("Drop off stall rear " + side + " / " + stall, transport, new Vector3(x + side * 6.8f, .012f, -63.8f), new Vector3(.09f, .02f, 2.6f), Mat("white"), false);
                }
            }
            RailBusShelter(transport, -29, -78);
            RailBusShelter(transport, 29, -78);
            RailRoadSign(transport, -46, -61, "TAXIS", "blue");
            RailRoadSign(transport, 46, -61, "DEPOSE\nMINUTE", "blue");
            RailRoadSign(transport, -39, -75, "BUS", "magenta");
            RailRoadSign(transport, 39, -75, "BUS", "magenta");
            Transform cycling = Group("Bicycle parking / Stationnement velos", transport);
            Box("Cycle parking pad", cycling, new Vector3(-48, .04f, -55), new Vector3(15, .08f, 6), Mat("stone"));
            for (int rack = 0; rack < 9; rack++)
            {
                float x = -54 + rack * 1.5f;
                Box("Cycle rack front " + rack, cycling, new Vector3(x, .45f, -55.6f), new Vector3(.065f, .9f, .065f), Mat("steel"));
                Box("Cycle rack rear " + rack, cycling, new Vector3(x, .45f, -54.4f), new Vector3(.065f, .9f, .065f), Mat("steel"));
                Box("Cycle rack rail " + rack, cycling, new Vector3(x, .9f, -55), new Vector3(.065f, .065f, 1.26f), Mat("steel"));
            }
            RailRoadSign(cycling, -56, -53, "P\nVELOS", "blue");

            Transform urban = Group("05 - French streetscape / Contexte urbain", root);
            RailFacade(urban, -77, -6, 17, 25, 31, "HOTEL DE LA GARE", "stone");
            RailFacade(urban, 78, 0, 18, 20, 38, "BRASSERIE DU PARVIS", "stone");
            RailFacade(urban, -77, 42, 17, 17, 29, "BUREAUX", "concrete");
            RailFacade(urban, 78, 49, 18, 22, 30, "MAISON DE LA MOBILITE", "stone");
            Box("West urban footpath", urban, new Vector3(-65.25f, -.1f, 18), new Vector3(4.5f, .2f, 111), Mat("floor"));
            Box("East urban footpath", urban, new Vector3(65.5f, -.1f, 18), new Vector3(5, .2f, 111), Mat("floor"));
        }

        static void RailBench(Transform parent, float x, float z, bool faceWest)
        {
            Transform bench = Group("Bench / Banc " + x + " / " + z, parent);
            float back = faceWest ? .31f : -.31f;
            for (int slat = 0; slat < 4; slat++)
                Box("Timber seat slat " + slat, bench, new Vector3(x + (slat - 1.5f) * .145f, .48f, z), new Vector3(.125f, .08f, 2.4f), Mat("wood"));
            for (int slat = 0; slat < 3; slat++)
                Box("Timber back slat " + slat, bench, new Vector3(x + back, .69f + slat * .15f, z), new Vector3(.08f, .12f, 2.4f), Mat("wood"));
            for (int leg = -1; leg <= 1; leg += 2)
            {
                Box("Bench foot " + leg, bench, new Vector3(x, .23f, z + leg * .82f), new Vector3(.46f, .46f, .075f), Mat("steel"));
                Box("Bench arm " + leg, bench, new Vector3(x, .71f, z + leg * 1.04f), new Vector3(.65f, .06f, .065f), Mat("steel"));
            }
        }

        static void RailBin(Transform parent, Vector3 pos)
        {
            Transform bin = Group("Waste sorting bin " + pos.x + " / " + pos.z, parent);
            Box("Bin body", bin, pos + new Vector3(0, .48f, 0), new Vector3(.48f, .96f, .48f), Mat("steel"));
            Box("Bin cover", bin, pos + new Vector3(0, 1.01f, 0), new Vector3(.55f, .08f, .55f), Mat("dark"));
            Box("Recycling marker", bin, pos + new Vector3(0, .65f, -.249f), new Vector3(.29f, .32f, .014f), Mat("yellow"), false);
        }

        static void RailPlatformSign(Transform parent, float x, float z, int left, int right, string zone)
        {
            Transform sign = Group("Passenger information / Secteur " + zone, parent);
            Box("Suspended information board", sign, new Vector3(x, 4.3f, z), new Vector3(4.8f, 1.1f, .18f), Mat("blue"));
            Box("Departure display", sign, new Vector3(x, 4.16f, z - .105f), new Vector3(3.1f, .55f, .03f), Mat("dark"), false);
            Label("Voie left", sign, left.ToString(), new Vector3(x - 1.98f, 4.34f, z - .12f), .58f, Color.white);
            Label("Voie right", sign, right.ToString(), new Vector3(x + 1.98f, 4.34f, z - .12f), .58f, Color.white);
            Label("Static departure legend", sign, "INFORMATION VOYAGEURS", new Vector3(x, 4.18f, z - .135f), .15f, new Color(1, .78f, .18f));
            Label("Sectors heading", sign, "SECTEUR " + zone, new Vector3(x, 4.66f, z - .115f), .18f, Color.white);
            for (int side = -1; side <= 1; side += 2)
                Box("Sign hanger " + side, sign, new Vector3(x + side * 1.8f, 5.02f, z), new Vector3(.04f, .5f, .04f), Mat("steel"), false);
            Box("Sector marker", sign, new Vector3(x, 2.9f, z), new Vector3(.85f, .85f, .16f), Mat("magenta"));
            Label("Sector letter", sign, zone, new Vector3(x, 2.94f, z - .095f), .6f, Color.white);
            Box("Sector marker pole", sign, new Vector3(x, 1.4f, z + .05f), new Vector3(.085f, 2.8f, .085f), Mat("steel"));
        }

        static void RailPlanterTree(Transform parent, float x, float z, float scale)
        {
            Transform tree = Group("Tree planter / Arbre " + x + " / " + z, parent);
            Box("Stone raised planter", tree, new Vector3(x, .3f, z), new Vector3(4.3f, .6f, 4.3f), Mat("stone"));
            Box("Planter soil", tree, new Vector3(x, .615f, z), new Vector3(3.8f, .04f, 3.8f), Mat("ballast"), false);
            Cylinder("Tree trunk", tree, new Vector3(x, 2.2f * scale, z), .21f * scale, 4.1f * scale, Mat("wood"));
            Beam("Branch left", tree, new Vector3(x, 3 * scale, z), new Vector3(x - .85f * scale, 4.5f * scale, z + .25f), .19f * scale, Mat("wood"));
            Beam("Branch right", tree, new Vector3(x, 3.1f * scale, z), new Vector3(x + .75f * scale, 4.4f * scale, z - .3f), .16f * scale, Mat("wood"));
            // Native sphere primitives remain editable and have shared leaf material.
            for (int crown = 0; crown < 3; crown++)
            {
                GameObject foliage = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                foliage.name = "Leaf crown " + crown;
                foliage.transform.SetParent(tree, false);
                foliage.transform.localPosition = new Vector3(x + (crown - 1) * .9f * scale, (4.85f + (crown == 1 ? .55f : 0)) * scale, z + (crown == 1 ? .3f : -.15f));
                foliage.transform.localScale = new Vector3(2.8f, 3.7f, 3.1f) * scale;
                foliage.GetComponent<Renderer>().sharedMaterial = Mat("green");
                Object.DestroyImmediate(foliage.GetComponent<Collider>());
                foliage.isStatic = true;
            }
            for (int s = -1; s <= 1; s += 2)
                Box("Planter seating edge " + s, tree, new Vector3(x + s * 2.0f, .68f, z), new Vector3(.5f, .12f, 4.5f), Mat("wood"));
        }

        static void RailStreetlight(Transform parent, float x, float z)
        {
            Transform lamp = Group("Forecourt light " + x + " / " + z, parent);
            Cylinder("Lamp column", lamp, new Vector3(x, 2.8f, z), .095f, 5.6f, Mat("steel"));
            Box("Lamp head", lamp, new Vector3(x, 5.62f, z), new Vector3(1.05f, .14f, .5f), Mat("steel"));
            Box("Lamp diffuser", lamp, new Vector3(x, 5.53f, z), new Vector3(.87f, .06f, .4f), Mat("light"), false);
            Cylinder("Lamp footing", lamp, new Vector3(x, .12f, z), .23f, .24f, Mat("stone"));
        }

        static void RailRoadSign(Transform parent, float x, float z, string text, string color)
        {
            Transform sign = Group("Transport sign / " + text.Replace('\n', ' '), parent);
            Cylinder("Sign pole", sign, new Vector3(x, 1.5f, z), .055f, 3, Mat("steel"));
            Box("Sign face", sign, new Vector3(x, 2.65f, z), new Vector3(1.45f, .95f, .1f), Mat(color));
            Label("Sign legend", sign, text, new Vector3(x, 2.69f, z - .065f), .24f, Color.white);
        }

        static void RailBusShelter(Transform parent, float x, float z)
        {
            Transform shelter = Group("Bus shelter / Abri voyageurs " + x, parent);
            Box("Shelter foundation", shelter, new Vector3(x, .12f, z), new Vector3(10, .18f, 3.7f), Mat("stone"));
            Box("Shelter roof", shelter, new Vector3(x, 3.15f, z), new Vector3(10.4f, .2f, 3.9f), Mat("steel"));
            Box("Shelter rear glass", shelter, new Vector3(x, 1.65f, z - 1.65f), new Vector3(9.7f, 2.7f, .06f), Mat("glass"));
            for (int side = -1; side <= 1; side += 2)
            {
                Box("Shelter pillar " + side, shelter, new Vector3(x + side * 4.8f, 1.63f, z), new Vector3(.13f, 3.06f, .16f), Mat("steel"));
                Box("Shelter side glazing " + side, shelter, new Vector3(x + side * 4.8f, 1.64f, z), new Vector3(.055f, 2.7f, 3.3f), Mat("glass"));
            }
            Box("Shelter bench", shelter, new Vector3(x - 1, .65f, z - 1.0f), new Vector3(5.5f, .14f, .6f), Mat("wood"));
            for (int i = -2; i <= 1; i++)
                Box("Shelter bench support " + i, shelter, new Vector3(x + i * 1.4f, .38f, z - 1), new Vector3(.12f, .56f, .45f), Mat("steel"));
            Box("Bus network panel", shelter, new Vector3(x + 3.4f, 1.8f, z - 1.57f), new Vector3(1.55f, 1.75f, .065f), Mat("white"), false);
            Label("Bus network heading", shelter, "RESEAU\nBUS", new Vector3(x + 3.4f, 2.07f, z - 1.52f), .26f, new Color(.03f, .12f, .3f), 180);
            Box("Bus shelter fascia", shelter, new Vector3(x, 3.12f, z + 1.99f), new Vector3(9.9f, .48f, .055f), Mat("blue"), false);
            Label("Bus shelter label", shelter, "GARE SNCF  |  BUS", new Vector3(x, 3.15f, z + 2.03f), .29f, Color.white, 180);
        }

        static void RailFacade(Transform parent, float x, float z, float width, float height, float depth, string name, string material)
        {
            Transform building = Group("Context building / " + name, parent);
            Box("Building foundation and pavement", building, new Vector3(x, -.65f, z), new Vector3(width + 2, 1.3f, depth + 2), Mat("concrete"));
            Box("Masonry building volume", building, new Vector3(x, height / 2, z), new Vector3(width, height, depth), Mat(material));
            Box("Slate mansard roof", building, new Vector3(x, height + 1.2f, z), new Vector3(width + .7f, 2.4f, depth + .7f), Mat("roof"));
            Box("Roof cap", building, new Vector3(x, height + 2.5f, z), new Vector3(width - 2, .2f, depth - 2), Mat("steel"));
            float faceX = x < 0 ? x + width / 2 + .04f : x - width / 2 - .04f;
            for (int floor = 0; floor < (int)(height / 3.7f); floor++)
            {
                float y = 2.2f + floor * 3.7f;
                Box("Stone floor belt " + floor, building, new Vector3(x, y - 1.43f, z), new Vector3(width + .3f, .19f, depth + .3f), Mat("white"));
                for (int col = 0; col < (int)(depth / 4.4f); col++)
                {
                    float wz = z - depth / 2 + 2.3f + col * 4.4f;
                    Box("Window stone surround " + floor + " / " + col, building, new Vector3(faceX, y, wz), new Vector3(.13f, 2.24f, 1.73f), Mat("white"));
                    Box("Recessed dark window backing " + floor + " / " + col, building, new Vector3(faceX + (x < 0 ? .079f : -.079f), y, wz), new Vector3(.022f, 1.96f, 1.45f), Mat("dark"), false);
                    Box("Window glass " + floor + " / " + col, building, new Vector3(faceX + (x < 0 ? .109f : -.109f), y, wz), new Vector3(.025f, 1.96f, 1.45f), Mat("glass"), false);
                    Box("Window transom " + floor + " / " + col, building, new Vector3(faceX + (x < 0 ? .133f : -.133f), y, wz), new Vector3(.025f, .07f, 1.48f), Mat("dark"), false);
                }
            }
            Box("Ground floor shop fascia", building, new Vector3(faceX + (x < 0 ? .14f : -.14f), 3.1f, z), new Vector3(.14f, .9f, depth - 3), Mat("blue"));
            Label("Building sign", building, name, new Vector3(faceX + (x < 0 ? .23f : -.23f), 3.12f, z), .48f, Color.white, x < 0 ? -90 : 90);
        }
    }
}
#endif
