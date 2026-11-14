#if UNITY_EDITOR
using UnityEngine;

namespace StationFlow.TrainStation.Editor
{
    // All objects are authored once by the editor builder and saved into TrainStation.unity.
    public static partial class TrainStationBuilder
    {
        static void BuildInteriors(Transform root)
        {
            var interiors = Group("02 Interior — commerces et services", root);
            var services = Group("01 Services", interiors);
            InteriorTicketOffice(InteriorShop(services, "01_Accueil_et_billetterie", -28.5f, -16f, 15.2f, -90f, "BILLETS  |  ACCUEIL", "blue"));
            InteriorCafe(InteriorShop(services, "02_Cafe_et_boulangerie", -28.5f, 0f, 15.2f, -90f, "LE CAFE DE LA GARE", "wood"));
            InteriorRelay(InteriorShop(services, "03_Relay_presse_et_livres", -28.5f, 16f, 15.2f, -90f, "RELAY  |  PRESSE & LIVRES", "red"));
            InteriorToilets(InteriorShop(services, "04_Sanitaires_accessibles_et_famille", 28.5f, -18f, 11.2f, 90f, "TOILETTES  |  ACCESSIBLES", "blue"));
            InteriorWaiting(InteriorShop(services, "05_Salon_attente", 28.5f, -6f, 11.2f, 90f, "SALON D'ATTENTE", "blue"));
            InteriorLockers(InteriorShop(services, "06_Consignes_et_services", 28.5f, 6f, 11.2f, 90f, "CONSIGNES  |  BAGAGES", "blue"));
            InteriorStaff(InteriorShop(services, "07_Secours_securite_personnel", 28.5f, 18f, 11.2f, 90f, "ASSISTANCE  |  PREMIERS SECOURS", "green"));

            var hall = Group("02 Mobilier", interiors);
            InteriorInfoDesk(hall);
            InteriorDepartures(hall);
            InteriorClock(hall, new Vector3(0, 6.6f, -25.2f));
            for (int side = -1; side <= 1; side += 2)
            {
                for (int i = 0; i < 4; i++)
                    InteriorTicketMachine(hall, new Vector3(side * (12f + i * 1.45f), 0, -21.2f), "Distributeur_" + (side < 0 ? "Ouest_" : "Est_") + (i + 1));
                for (int j = 0; j < 3; j++)
                {
                    var island = Group("Ilot_attente_" + (side < 0 ? "Ouest_" : "Est_") + (j + 1), hall);
                    island.localPosition = new Vector3(side * 12, 0, -9f + 12f * j);
                    island.localRotation = Quaternion.Euler(0, 90, 0);
                    InteriorBench(island, new Vector3(0, 0, -0.72f), 0, 4);
                    InteriorBench(island, new Vector3(0, 0, 0.72f), 180, 4);
                    InteriorPlanter(island, new Vector3(2.6f, 0, 0), 0.65f);
                    InteriorBin(island, new Vector3(-2.65f, 0, 0), "Tri_selectif");
                }
                InteriorVending(hall, new Vector3(side * 19, 0, 24), side < 0 ? -90 : 90, "Boissons_et_encas");
                InteriorWater(hall, new Vector3(side * 17.2f, 0, 25.8f));
                InteriorFirePoint(hall, new Vector3(side * 20, 0, -13), side < 0 ? -90 : 90);
                InteriorFirePoint(hall, new Vector3(side * 20, 0, 13), side < 0 ? -90 : 90);
            }
            InteriorWayfinding(hall);
            InteriorConcourse(hall);
            InteriorTactile(hall);
        }

        static Transform InteriorShop(Transform parent, string name, float x, float z, float width, float yaw, string title, string fascia)
        {
            var bay = Group(name, parent);
            bay.localPosition = new Vector3(x, 0, z);
            bay.localRotation = Quaternion.Euler(0, yaw, 0);
            Box("Sol_du_local", bay, new Vector3(0, -.008f, 0), new Vector3(width, .024f, 13.8f), Mat("floor"));
            Box("Cloison_gauche", bay, new Vector3(-width / 2, 1.65f, 0), new Vector3(.18f, 3.3f, 14), Mat("stone"));
            Box("Cloison_droite", bay, new Vector3(width / 2, 1.65f, 0), new Vector3(.18f, 3.3f, 14), Mat("stone"));
            Box("Imposte_enseigne", bay, new Vector3(0, 3.6f, -6.98f), new Vector3(width, .9f, .25f), Mat(fascia));
            Label("Nom_du_service", bay, title, new Vector3(0, 3.62f, -7.13f), .46f, Color.white);
            float flankWidth = width * .5f - 2.1f;
            for (int side = -1; side <= 1; side += 2)
            {
                float center = side * (2.1f + flankWidth / 2);
                Box("Vitrine_" + side, bay, new Vector3(center, 1.6f, -6.98f), new Vector3(flankWidth, 2.9f, .09f), Mat("glass"));
                Box("Soubassement_vitrine_" + side, bay, new Vector3(center, .22f, -6.98f), new Vector3(flankWidth, .42f, .18f), Mat("dark"));
                Box("Montant_entree_" + side, bay, new Vector3(side * 2.1f, 1.6f, -6.98f), new Vector3(.12f, 3.2f, .18f), Mat("steel"));
                // The four-metre central opening has no invisible collision or closed door.
                Box("Porte_ouverte_" + side, bay, new Vector3(side * 2.1f, 1.55f, -5.93f), new Vector3(.075f, 3, 1.95f), Mat("glass"));
                Box("Plinthe_interieure_" + side, bay, new Vector3(side * (width / 2 - .12f), .12f, 0), new Vector3(.08f, .22f, 13.7f), Mat("dark"), false);
            }
            Box("Seuil_accessible", bay, new Vector3(0, .008f, -6.98f), new Vector3(4, .016f, .25f), Mat("steel"), false);
            var ceiling = Group("Roofs - Ceiling " + name, bay);
            Box("Plafond_local", ceiling, new Vector3(0, 4.12f, 0), new Vector3(width, .15f, 13.9f), Mat("white"), false);
            for (int i = -1; i <= 1; i++)
            {
                Box("Luminaire_local_" + i, bay, new Vector3(i * width * .27f, 3.55f, .6f), new Vector3(.16f, .08f, 9.5f), Mat("light"), false);
                Beam("Suspente_luminaire_" + i, bay, new Vector3(i * width * .27f, 3.59f, .6f), new Vector3(i * width * .27f, 4.04f, .6f), .025f, Mat("steel"));
            }
            return bay;
        }

        static void InteriorTicketOffice(Transform bay)
        {
            var counters = Group("Guichets_accessibles", bay);
            for (int i = 0; i < 3; i++)
            {
                float x = -4.8f + i * 4.8f;
                float height = i == 0 ? .77f : 1.05f;
                Box("Comptoir_" + (i + 1), counters, new Vector3(x, height / 2, 2.1f), new Vector3(3.8f, height, 1.1f), Mat("wood"));
                Box("Plateau_" + (i + 1), counters, new Vector3(x, height, 2.1f), new Vector3(4, .07f, 1.23f), Mat("white"));
                InteriorMonitor(counters, new Vector3(x + .9f, height + .05f, 2.25f), 180);
                Box("Terminal_paiement_" + i, counters, new Vector3(x - .7f, height + .10f, 1.85f), new Vector3(.28f, .15f, .35f), Mat("dark"));
                Label("Guichet_numero_" + i, counters, "0" + (i + 1) + (i == 0 ? "  ACCES PMR" : "  BILLETS"), new Vector3(x, 2.35f, 2.6f), .36f, new Color(.03f, .16f, .32f));
                InteriorChair(counters, new Vector3(x, 0, 3.5f), 0, "Siege_agent");
            }
            Box("Mur_fond_signal", bay, new Vector3(0, 2.1f, 6.68f), new Vector3(13.8f, 2.1f, .05f), Mat("blue"), false);
            Label("Message_accueil", bay, "BIENVENUE\nCONSEIL  /  VENTE  /  ASSISTANCE", new Vector3(0, 2.25f, 6.61f), .53f, Color.white);
            for (int i = 0; i < 4; i++)
                InteriorQueuePost(bay, new Vector3(-4.5f + i * 3, 0, -2.1f));
            Box("Ruban_file_gauche", bay, new Vector3(-3, .85f, -2.1f), new Vector3(3, .08f, .035f), Mat("blue"), false);
            Box("Ruban_file_droite", bay, new Vector3(3, .85f, -2.1f), new Vector3(3, .08f, .035f), Mat("blue"), false);
            InteriorBrochureRack(bay, new Vector3(-6.2f, 0, -3.8f));
            InteriorBench(bay, new Vector3(4.4f, 0, -4.2f), 0, 3);
        }

        static void InteriorCafe(Transform bay)
        {
            var counter = Group("Comptoir_boulangerie", bay);
            Box("Socle_bois", counter, new Vector3(-2.5f, .53f, 2.8f), new Vector3(7.5f, 1.06f, 1.6f), Mat("wood"));
            Box("Plan_de_travail", counter, new Vector3(-2.5f, 1.08f, 2.8f), new Vector3(7.7f, .1f, 1.75f), Mat("white"));
            Box("Vitrine_patisserie", counter, new Vector3(-3.9f, 1.49f, 2.8f), new Vector3(4.4f, .72f, 1.65f), Mat("glass"));
            Box("Dessus_vitrine", counter, new Vector3(-3.9f, 1.86f, 2.8f), new Vector3(4.5f, .06f, 1.7f), Mat("steel"));
            for (int i = 0; i < 9; i++)
            {
                float x = -5.5f + (i % 5) * .8f;
                float z = 2.4f + (i / 5) * .65f;
                Box("Plateau_patisserie_" + i, counter, new Vector3(x, 1.15f, z), new Vector3(.64f, .035f, .43f), Mat("dark"), false);
                var loaf = Cylinder("Pain_ou_viennoiserie_" + i, counter, new Vector3(x, 1.25f, z), .2f, .22f, Mat("wood"), false);
                loaf.transform.localScale = new Vector3(.42f, .11f, .26f);
            }
            Box("Machine_expresso", counter, new Vector3(.1f, 1.45f, 2.8f), new Vector3(1.1f, .66f, .85f), Mat("steel"));
            Box("Façade_expresso", counter, new Vector3(.1f, 1.43f, 2.36f), new Vector3(.94f, .3f, .035f), Mat("dark"), false);
            for (int i = 0; i < 3; i++)
            {
                Cylinder("Tasse_" + i, counter, new Vector3(-.2f + i * .3f, 1.16f, 2.24f), .085f, .12f, Mat("white"), false);
                Cylinder("Buse_cafe_" + i, counter, new Vector3(-.2f + i * .3f, 1.37f, 2.28f), .022f, .12f, Mat("steel"), false);
            }
            InteriorMonitor(counter, new Vector3(.65f, 1.1f, 3.1f), 0);
            Box("Meuble_arriere", bay, new Vector3(-3, .48f, 5.9f), new Vector3(7, .96f, 1.1f), Mat("white"));
            Box("Menu_cafe", bay, new Vector3(-2.8f, 2.5f, 6.45f), new Vector3(7.2f, 1.45f, .1f), Mat("dark"));
            Label("Texte_menu", bay, "CAFE   THE   CHOCOLAT\nCROISSANTS  /  SANDWICHS\nSUR PLACE OU A EMPORTER", new Vector3(-2.8f, 2.52f, 6.38f), .36f, Color.white);
            for (int i = 0; i < 4; i++)
            {
                float x = i % 2 == 0 ? 3.8f : 5.8f;
                float z = i < 2 ? -.7f : 3.4f;
                InteriorCafeTable(bay, new Vector3(x, 0, z));
            }
            InteriorCafeTable(bay, new Vector3(-4.8f, 0, -2.5f));
            InteriorCafeTable(bay, new Vector3(-1.8f, 0, -2.5f));
            InteriorBin(bay, new Vector3(6.5f, 0, -5.6f), "Retour_plateaux_et_tri");
            InteriorPlanter(bay, new Vector3(-6.5f, 0, -5.5f), .5f);
        }

        static void InteriorRelay(Transform bay)
        {
            for (int row = 0; row < 3; row++)
            {
                var rack = Group("Rayonnage_presse_" + (row + 1), bay);
                rack.localPosition = new Vector3(-4.8f + row * 4.8f, 0, 1.5f);
                InteriorBookshelf(rack, 3.1f, 3, row);
            }
            var back = Group("Librairie_du_fond", bay);
            back.localPosition = new Vector3(0, 0, 6.2f);
            InteriorBookshelf(back, 12.6f, 4, 2);
            Box("Comptoir_caisse", bay, new Vector3(-4.9f, .5f, -3.5f), new Vector3(3.2f, 1, 1.35f), Mat("white"));
            Box("Bandeau_caisse", bay, new Vector3(-4.9f, .72f, -4.2f), new Vector3(3.1f, .28f, .04f), Mat("red"), false);
            InteriorMonitor(bay, new Vector3(-4.6f, 1.03f, -3.6f), 0);
            Label("Librairie_categories", bay, "PRESSE   /   VOYAGE   /   LECTURE", new Vector3(0, 2.95f, 6.52f), .42f, new Color(.55f, .02f, .05f));
            var postcards = Group("Presentoir_cartes_postales", bay);
            postcards.localPosition = new Vector3(4.5f, 0, -3.3f);
            InteriorBookshelf(postcards, 2.2f, 3, 1);
        }

        static void InteriorToilets(Transform bay)
        {
            // The front half is a generous common lobby; three open cubicles face it.
            float[] centers = { -3.65f, 0, 3.65f };
            string[] names = { "Cabine_accessible", "Cabine_famille", "Cabine_standard" };
            for (int i = 0; i < centers.Length; i++)
            {
                float x = centers[i];
                var cubicle = Group(names[i], bay);
                cubicle.localPosition = new Vector3(x, 0, 3.8f);
                if (i < 2) Box("Cloison", cubicle, new Vector3(1.8f, 1.25f, 0), new Vector3(.12f, 2.5f, 5.3f), Mat("white"));
                Box("Porte_ouverte", cubicle, new Vector3(-1.6f, 1.2f, -1.5f), new Vector3(.08f, 2.35f, 2), Mat("blue"));
                Box("Imposte", cubicle, new Vector3(0, 2.6f, -2.65f), new Vector3(3.55f, .28f, .14f), Mat("blue"));
                Label("Identification", cubicle, i == 0 ? "PMR" : i == 1 ? "FAMILLE" : "WC", new Vector3(0, 2.62f, -2.74f), .3f, Color.white);
                Cylinder("Cuvette", cubicle, new Vector3(.15f, .38f, 1.2f), .35f, .56f, Mat("white"));
                Box("Reservoir", cubicle, new Vector3(.15f, .75f, 1.64f), new Vector3(.75f, .75f, .32f), Mat("white"));
                var seat = Cylinder("Abattant", cubicle, new Vector3(.15f, .68f, 1.17f), .34f, .06f, Mat("dark"));
                seat.transform.localScale = new Vector3(.68f, .03f, .81f);
                Box("Distributeur_papier", cubicle, new Vector3(1.55f, .8f, .65f), new Vector3(.22f, .25f, .25f), Mat("steel"));
                if (i == 0)
                {
                    Beam("Barre_appui_horizontale", cubicle, new Vector3(-.75f, .9f, .45f), new Vector3(-.75f, .9f, 1.6f), .045f, Mat("steel"));
                    Beam("Barre_appui_verticale", cubicle, new Vector3(-.75f, .65f, 1.6f), new Vector3(-.75f, 1.5f, 1.6f), .045f, Mat("steel"));
                    Box("Bouton_assistance", cubicle, new Vector3(-1.69f, .8f, 1.2f), new Vector3(.08f, .22f, .18f), Mat("red"));
                }
                if (i == 1)
                    Box("Table_a_langer", cubicle, new Vector3(-.9f, .9f, .25f), new Vector3(1, .14f, 1.2f), Mat("white"));
            }
            Box("Plan_vasques", bay, new Vector3(-3.6f, .81f, -3.2f), new Vector3(2.6f, .14f, 1.1f), Mat("white"));
            for (int i = 0; i < 2; i++)
            {
                Cylinder("Vasque_" + i, bay, new Vector3(-4.25f + 1.3f * i, .88f, -3.2f), .32f, .05f, Mat("steel"));
                Beam("Robinet_" + i, bay, new Vector3(-4.25f + 1.3f * i, .86f, -2.86f), new Vector3(-4.25f + 1.3f * i, 1.14f, -2.86f), .055f, Mat("steel"));
            }
            Box("Miroir", bay, new Vector3(-5.47f, 1.65f, -3.2f), new Vector3(.045f, 1.25f, 2.7f), Mat("glass"), false);
            Box("Seche_mains", bay, new Vector3(4.65f, 1.15f, -3), new Vector3(.6f, .75f, .45f), Mat("white"));
            InteriorBin(bay, new Vector3(4.4f, 0, -5.4f), "Corbeille_sanitaires");
            Label("Hygiene", bay, "LAVABOS  /  ESPACE BEBE", new Vector3(0, 2.65f, -.8f), .3f, new Color(.06f, .2f, .35f));
        }

        static void InteriorWaiting(Transform bay)
        {
            for (int z = 0; z < 3; z++)
            {
                InteriorBench(bay, new Vector3(-2.5f, 0, -2.5f + z * 3.5f), -90, 3);
                InteriorBench(bay, new Vector3(2.5f, 0, -2.5f + z * 3.5f), 90, 3);
            }
            Box("Table_recharge", bay, new Vector3(0, .75f, 4.8f), new Vector3(2, .12f, 2), Mat("wood"));
            Box("Pied_table", bay, new Vector3(0, .37f, 4.8f), new Vector3(.35f, .74f, .35f), Mat("steel"));
            for (int i = 0; i < 4; i++)
                Box("Prise_USB_" + i, bay, new Vector3(-.6f + i * .4f, .83f, 4.8f), new Vector3(.22f, .035f, .14f), Mat("dark"), false);
            Label("Services_salon", bay, "ESPACE CALME\nWIFI  /  PRISES  /  RECHARGE", new Vector3(0, 2.5f, 6.6f), .46f, new Color(.02f, .2f, .35f));
            InteriorPlanter(bay, new Vector3(-4.6f, 0, 5.8f), .65f);
            InteriorPlanter(bay, new Vector3(4.6f, 0, 5.8f), .65f);
            InteriorBin(bay, new Vector3(4.6f, 0, -5.8f), "Tri_salon");
        }

        static void InteriorLockers(Transform bay)
        {
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 8; col++)
                {
                    float x = -4.55f + col * 1.3f;
                    float y = .65f + row * 1.24f;
                    var locker = Group("Casier_" + (row * 8 + col + 1).ToString("00"), bay);
                    locker.localPosition = new Vector3(x, y, 5.9f);
                    Box("Corps", locker, Vector3.zero, new Vector3(1.2f, 1.18f, 1.45f), Mat("blue"));
                    Box("Porte", locker, new Vector3(0, 0, -.75f), new Vector3(1.13f, 1.1f, .06f), Mat((col % 3 == 0) ? "steel" : "dark"));
                    Box("Serrure", locker, new Vector3(.36f, 0, -.795f), new Vector3(.14f, .22f, .04f), Mat("steel"), false);
                    Label("Numero", locker, (row * 8 + col + 1).ToString("00"), new Vector3(-.23f, .3f, -.8f), .21f, Color.white);
                }
            InteriorTicketMachine(bay, new Vector3(-3.8f, 0, -.7f), "Paiement_consignes", "CONSIGNE\nCHOISIR UN CASIER");
            InteriorBench(bay, new Vector3(2.8f, 0, -1), 90, 3);
            Box("Balance_bagages", bay, new Vector3(-3.8f, .12f, -3), new Vector3(1.3f, .24f, 1.3f), Mat("steel"));
            Label("Consignes_mode_emploi", bay, "BAGAGES\n1. CHOISIR  2. DEPOSER  3. FERMER", new Vector3(0, 2.9f, 6.3f), .35f, new Color(.05f, .2f, .4f));
            var trolleys = Group("Chariots_bagages_stationnes", bay);
            for (int i = 0; i < 3; i++)
            {
                var trolley = Group("Chariot_" + (i + 1), trolleys);
                trolley.localPosition = new Vector3(3.8f, 0, -4.5f + i * .48f);
                Box("Plateforme", trolley, new Vector3(0, .24f, 0), new Vector3(.82f, .075f, 1), Mat("steel"));
                for (int s = -1; s <= 1; s += 2)
                {
                    Beam("Montant_poignee_" + s, trolley, new Vector3(s * .36f, .24f, .4f), new Vector3(s * .36f, 1.08f, .4f), .04f, Mat("steel"));
                    for (int w = -1; w <= 1; w += 2)
                    {
                        var wheel = Cylinder("Roulette_" + s + "_" + w, trolley, new Vector3(s * .36f, .12f, w * .37f), .12f, .065f, Mat("dark"));
                        wheel.transform.localRotation = Quaternion.Euler(0, 0, 90);
                    }
                }
                Beam("Poignee", trolley, new Vector3(-.36f, 1.08f, .4f), new Vector3(.36f, 1.08f, .4f), .055f, Mat("dark"));
            }
        }

        static void InteriorStaff(Transform bay)
        {
            Box("Cloison_bureau", bay, new Vector3(.7f, 1.45f, 3.2f), new Vector3(.14f, 2.9f, 7), Mat("stone"));
            Box("Lit_examen", bay, new Vector3(-2.7f, .65f, 3.8f), new Vector3(1.4f, .22f, 2.5f), Mat("white"));
            Box("Pied_lit", bay, new Vector3(-2.7f, .27f, 3.8f), new Vector3(.85f, .54f, 1.8f), Mat("steel"));
            Box("Oreiller", bay, new Vector3(-2.7f, .83f, 4.65f), new Vector3(1.15f, .16f, .5f), Mat("floor"));
            Box("Armoire_soins", bay, new Vector3(-4.8f, 1, 5.7f), new Vector3(1, 2, 1), Mat("white"));
            Box("Croix_verticale", bay, new Vector3(-4.8f, 1.55f, 5.18f), new Vector3(.12f, .48f, .025f), Mat("green"), false);
            Box("Croix_horizontale", bay, new Vector3(-4.8f, 1.55f, 5.16f), new Vector3(.45f, .12f, .025f), Mat("green"), false);
            Box("Bureau_securite", bay, new Vector3(3.1f, .76f, 3.8f), new Vector3(3.1f, .12f, 1.2f), Mat("wood"));
            for (int s = -1; s <= 1; s += 2)
                Box("Pied_bureau_" + s, bay, new Vector3(3.1f + s * 1.3f, .37f, 3.8f), new Vector3(.12f, .74f, 1), Mat("steel"));
            InteriorMonitor(bay, new Vector3(2.4f, .82f, 3.9f), 180);
            InteriorMonitor(bay, new Vector3(3.9f, .82f, 3.9f), 180);
            InteriorChair(bay, new Vector3(3.1f, 0, 5.15f), 0, "Siege_securite");
            Box("Armoire_personnel", bay, new Vector3(4.6f, 1.2f, 6.1f), new Vector3(1.6f, 2.4f, .9f), Mat("steel"));
            Label("Identification_soins", bay, "PREMIERS SECOURS", new Vector3(-2.65f, 2.7f, 6.6f), .32f, new Color(.04f, .36f, .2f));
            Label("Identification_securite", bay, "SECURITE  /  PERSONNEL", new Vector3(3.1f, 2.7f, 6.6f), .27f, new Color(.03f, .18f, .32f));
            Box("Comptoir_assistance", bay, new Vector3(-2.7f, .43f, -2.1f), new Vector3(3.2f, .86f, 1.1f), Mat("white"));
            Label("Accueil_assistance", bay, "ASSISTANCE VOYAGEURS", new Vector3(-2.7f, .55f, -2.67f), .21f, new Color(.03f, .27f, .22f));
            InteriorChair(bay, new Vector3(-2.7f, 0, -.4f), 0, "Siege_assistance");
            InteriorBench(bay, new Vector3(3.25f, 0, -3.8f), 0, 3);
            Box("Defibrillateur_coffret", bay, new Vector3(4.95f, 1.45f, -5.4f), new Vector3(.65f, .7f, .25f), Mat("green"));
            Label("Defibrillateur", bay, "DAE", new Vector3(4.95f, 1.47f, -5.54f), .21f, Color.white);
        }

        static void InteriorInfoDesk(Transform hall)
        {
            var desk = Group("Point_information_central", hall);
            desk.localPosition = new Vector3(0, 0, 5);
            Box("Comptoir_avant", desk, new Vector3(0, .5f, -.9f), new Vector3(6.2f, 1, 1.05f), Mat("blue"));
            Box("Plateau_avant", desk, new Vector3(0, 1.04f, -.9f), new Vector3(6.4f, .09f, 1.2f), Mat("white"));
            Box("Retour_accessible", desk, new Vector3(-3.65f, .38f, -.9f), new Vector3(1.2f, .76f, 1.05f), Mat("blue"));
            Box("Plateau_accessible", desk, new Vector3(-3.65f, .8f, -.9f), new Vector3(1.3f, .08f, 1.18f), Mat("white"));
            Box("Retour_droit", desk, new Vector3(2.6f, .5f, .1f), new Vector3(1, 1, 1.2f), Mat("blue"));
            Label("Accueil_front", desk, "INFORMATION  /  ACCUEIL", new Vector3(0, .58f, -1.44f), .38f, Color.white);
            InteriorMonitor(desk, new Vector3(-1.25f, 1.1f, -.6f), 180);
            InteriorMonitor(desk, new Vector3(1.25f, 1.1f, -.6f), 180);
            InteriorChair(desk, new Vector3(-1.25f, 0, .75f), 0, "Siege_accueil_1");
            InteriorChair(desk, new Vector3(1.25f, 0, .75f), 0, "Siege_accueil_2");
            InteriorBrochureRack(desk, new Vector3(4.2f, 0, -.9f));
            Cylinder("Totem_information", desk, new Vector3(0, 2.5f, .5f), .06f, 2.8f, Mat("steel"), false);
            Box("Panneau_information", desk, new Vector3(0, 4.1f, .5f), new Vector3(1.15f, 1.15f, .13f), Mat("blue"), false);
            Label("Symbole_information", desk, "i", new Vector3(0, 4.15f, .42f), .94f, Color.white);
        }

        static void InteriorDepartures(Transform hall)
        {
            var board = Group("Tableau_departs_double_face_STATIQUE", hall);
            board.localPosition = new Vector3(0, 6, 14);
            Box("Caisson_affichage", board, Vector3.zero, new Vector3(12.8f, 3.7f, .3f), Mat("dark"), false);
            for (int side = 0; side < 2; side++)
            {
                var face = Group(side == 0 ? "Face_hall" : "Face_quais", board);
                face.localRotation = Quaternion.Euler(0, side * 180, 0);
                Box("Entete_departs", face, new Vector3(0, 1.35f, -.17f), new Vector3(12.4f, .73f, .02f), Mat("blue"), false);
                Label("DEPARTS", face, "DEPARTS  /  DEPARTURES", new Vector3(-1.8f, 1.35f, -.2f), .53f, Color.white);
                Label("Horloge_affichee", face, "14:20", new Vector3(5, 1.35f, -.2f), .47f, Color.white);
                Label("Colonnes", face, "HEURE      DESTINATION                       TRAIN      VOIE", new Vector3(0, .77f, -.2f), .27f, new Color(.7f, .77f, .82f));
                string[] times = { "14:32", "14:45", "15:02", "15:18", "15:35" };
                string[] dest = { "PARIS GARE DE LYON", "LYON PART-DIEU", "MARSEILLE ST-CHARLES", "DIJON VILLE", "STRASBOURG" };
                string[] trains = { "TGV 6624", "TER 17803", "TGV 5308", "TER 17764", "TGV 6882" };
                for (int i = 0; i < 5; i++)
                {
                    float y = .29f - i * .38f;
                    if (i % 2 == 0) Box("Fond_ligne_" + i, face, new Vector3(0, y, -.178f), new Vector3(12.35f, .36f, .012f), Mat("blue"), false);
                    Label("Heure_" + i, face, times[i], new Vector3(-5.25f, y, -.215f), .31f, Color.white);
                    Label("Destination_" + i, face, dest[i], new Vector3(-1.2f, y, -.215f), .31f, Color.white);
                    Label("Train_" + i, face, trains[i], new Vector3(3.6f, y, -.215f), .27f, Color.white);
                    Label("Voie_" + i, face, (i + 1).ToString(), new Vector3(5.5f, y, -.215f), .33f, new Color(1, .8f, .08f));
                }
                Label("Mention_statique", face, "HORAIRES DE DEMONSTRATION  /  AFFICHAGE STATIQUE", new Vector3(0, -1.61f, -.2f), .17f, new Color(.64f, .7f, .73f));
            }
            for (int s = -1; s <= 1; s += 2)
                Beam("Suspente_" + s, board, new Vector3(s * 4.7f, 1.85f, 0), new Vector3(s * 4.7f, 8.61f, 0), .055f, Mat("steel"));
        }

        static void InteriorWayfinding(Transform hall)
        {
            InteriorSign(hall, "Orientation_quais", new Vector3(0, 4.4f, 25.5f), "^  ACCES AUX QUAIS  /  VOIES 1 A 8", 13.5f);
            InteriorSign(hall, "Orientation_sortie", new Vector3(0, 4.4f, -24), "SORTIE  /  PLACE DE LA GARE  v", 11, 180);
            InteriorSign(hall, "Services_ouest", new Vector3(-12.7f, 3.8f, -14.4f), "<  BILLETS   CAFE   PRESSE", 7);
            InteriorSign(hall, "Services_est", new Vector3(12.7f, 3.8f, -14.4f), "WC   CONSIGNES   ASSISTANCE  >", 8);
            var map = Group("Plan_general_de_la_gare", hall);
            map.localPosition = new Vector3(7.1f, 0, -15.6f);
            Box("Pied_plan", map, new Vector3(0, 1.1f, 0), new Vector3(.16f, 2.2f, .25f), Mat("steel"));
            Box("Cadre_plan", map, new Vector3(0, 1.95f, 0), new Vector3(2.7f, 1.85f, .13f), Mat("blue"));
            Box("Fond_plan", map, new Vector3(0, 1.95f, -.08f), new Vector3(2.53f, 1.68f, .02f), Mat("white"), false);
            Label("Titre_plan", map, "PLAN DE LA GARE", new Vector3(0, 2.61f, -.1f), .23f, new Color(.04f, .2f, .36f));
            Box("Plan_hall", map, new Vector3(0, 1.88f, -.105f), new Vector3(.9f, .71f, .015f), Mat("floor"), false);
            Box("Plan_concourse", map, new Vector3(0, 2.23f, -.107f), new Vector3(2.1f, .14f, .015f), Mat("blue"), false);
            for (int i = 0; i < 4; i++)
                Box("Plan_quai_" + i, map, new Vector3(-.78f + i * .52f, 2.4f, -.108f), new Vector3(.26f, .3f, .015f), Mat("blue"), false);
            for (int s = -1; s <= 1; s += 2)
                for (int i = 0; i < 3; i++)
                    Box("Plan_service_" + s + "_" + i, map, new Vector3(s * .71f, 1.64f + i * .2f, -.11f), new Vector3(.34f, .16f, .015f), Mat(s < 0 ? "wood" : "green"), false);
            Label("Vous_etes_ici", map, "VOUS ETES ICI", new Vector3(0, 1.32f, -.11f), .17f, new Color(.67f, .03f, .3f));
        }

        static void InteriorConcourse(Transform hall)
        {
            var area = Group("Concourse_et_acces_quais", hall);
            for (int i = 0; i < 4; i++)
            {
                float x = -36 + i * 24;
                InteriorSign(area, "Direction_quai_" + (i + 1), new Vector3(x, 4, 40.5f), "VOIES " + (i * 2 + 1) + " / " + (i * 2 + 2) + "  ^", 7.3f);
                for (int s = -1; s <= 1; s += 2)
                {
                    var validator = Group("Borne_validation_" + i + "_" + s, area);
                    validator.localPosition = new Vector3(x + s * 4.35f, 0, 40.8f);
                    Box("Socle", validator, new Vector3(0, .7f, 0), new Vector3(.42f, 1.4f, .42f), Mat("yellow"));
                    Box("Tete_lecteur", validator, new Vector3(0, 1.45f, -.05f), new Vector3(.54f, .48f, .52f), Mat("yellow"));
                    Box("Lecteur_billet", validator, new Vector3(0, 1.44f, -.322f), new Vector3(.34f, .2f, .035f), Mat("dark"), false);
                    Label("Valider", validator, "BILLETS", new Vector3(0, 1.84f, -.1f), .19f, new Color(.02f, .2f, .35f));
                }
                InteriorBench(area, new Vector3(x + 6.4f, 0, 31), 0, 3);
                InteriorBin(area, new Vector3(x - 6.4f, 0, 31), "Tri_concourse_" + (i + 1));
            }
            InteriorSign(area, "Orientation_transversale", new Vector3(0, 4.5f, 32), "<  VOIES 1 A 4        VOIES 5 A 8  >", 14);
        }

        static void InteriorTactile(Transform hall)
        {
            var guide = Group("Bandes_guidage_tactile_continues", hall);
            InteriorTactileStrip(guide, "Entree_vers_hall", new Vector3(0, .022f, -14.5f), new Vector3(.42f, .025f, 27));
            InteriorTactileStrip(guide, "Contournement_accueil_sud", new Vector3(-3, .022f, -1), new Vector3(6.4f, .025f, .42f));
            InteriorTactileStrip(guide, "Contournement_accueil_ouest", new Vector3(-6, .022f, 4), new Vector3(.42f, .025f, 10.4f));
            InteriorTactileStrip(guide, "Retour_axe_hall", new Vector3(-3, .022f, 9), new Vector3(6.4f, .025f, .42f));
            InteriorTactileStrip(guide, "Hall_vers_concourse", new Vector3(0, .022f, 22.5f), new Vector3(.42f, .025f, 27));
            InteriorTactileStrip(guide, "Desserte_transversale_quais", new Vector3(0, .022f, 36), new Vector3(72.4f, .025f, .42f));
            for (int i = 0; i < 4; i++)
            {
                float x = -36 + i * 24;
                InteriorTactileStrip(guide, "Branche_quai_" + (i + 1), new Vector3(x, .022f, 40), new Vector3(.42f, .025f, 8.4f));
                Box("Zone_orientation_" + i, guide, new Vector3(x, .025f, 36), new Vector3(.85f, .03f, .85f), Mat("yellow"), false);
            }
        }

        static void InteriorTactileStrip(Transform parent, string name, Vector3 pos, Vector3 size)
        {
            var strip = Group(name, parent);
            strip.localPosition = pos;
            bool alongZ = size.z > size.x;
            for (int i = -1; i <= 1; i++)
            {
                Vector3 barPos = alongZ ? new Vector3(i * .13f, 0, 0) : new Vector3(0, 0, i * .13f);
                Vector3 barSize = alongZ ? new Vector3(.075f, size.y, size.z) : new Vector3(size.x, size.y, .075f);
                Box("Nervure_" + i, strip, barPos, barSize, Mat("yellow"), false);
            }
        }

        static void InteriorClock(Transform parent, Vector3 pos)
        {
            var clock = Group("Horloge_analogique_14h20_STATIQUE", parent);
            clock.localPosition = pos;
            clock.localRotation = Quaternion.Euler(0, 180, 0);
            Beam("Suspente_horloge", clock, new Vector3(0, 1.05f, 0), new Vector3(0, 14.93f - pos.y, 0), .035f, Mat("steel"));
            var rim = Cylinder("Boitier", clock, Vector3.zero, 1.05f, .18f, Mat("steel"), false);
            rim.transform.localRotation = Quaternion.Euler(90, 0, 0);
            var dial = Cylinder("Cadran", clock, new Vector3(0, 0, -.11f), .97f, .035f, Mat("white"), false);
            dial.transform.localRotation = Quaternion.Euler(90, 0, 0);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6;
                var tick = Box("Index_" + i, clock, new Vector3(Mathf.Sin(angle) * .81f, Mathf.Cos(angle) * .81f, -.145f), new Vector3(.045f, .14f, .015f), Mat("dark"), false);
                tick.transform.localRotation = Quaternion.Euler(0, 0, -i * 30);
            }
            Beam("Aiguille_heures", clock, new Vector3(0, 0, -.18f), new Vector3(.46f, .17f, -.18f), .07f, Mat("dark"));
            Beam("Aiguille_minutes", clock, new Vector3(0, 0, -.19f), new Vector3(.64f, -.37f, -.19f), .04f, Mat("dark"));
            Label("Signature_horloge", clock, "SNCF", new Vector3(0, -.46f, -.17f), .15f, new Color(.58f, .03f, .25f));
        }

        static void InteriorBench(Transform parent, Vector3 pos, float yaw, int seats)
        {
            var bench = Group("Banquette_" + seats + "_places", parent);
            bench.localPosition = pos;
            bench.localRotation = Quaternion.Euler(0, yaw, 0);
            float length = seats * .85f;
            for (int s = -1; s <= 1; s += 2)
                Box("Pietement_" + s, bench, new Vector3(s * (length / 2 - .35f), .23f, 0), new Vector3(.13f, .46f, .72f), Mat("steel"));
            Box("Traverse", bench, new Vector3(0, .34f, 0), new Vector3(length, .12f, .2f), Mat("steel"));
            for (int slat = 0; slat < 4; slat++)
            {
                Box("Latte_assise_" + slat, bench, new Vector3(0, .48f, -.28f + slat * .18f), new Vector3(length, .075f, .15f), Mat("wood"));
                if (slat < 3) Box("Latte_dossier_" + slat, bench, new Vector3(0, .71f + slat * .16f, .34f), new Vector3(length, .12f, .075f), Mat("wood"));
            }
            for (int i = 0; i <= seats; i++)
                Box("Accoudoir_" + i, bench, new Vector3(-length / 2 + i * .85f, .68f, .02f), new Vector3(.045f, .055f, .57f), Mat("steel"));
        }

        static void InteriorChair(Transform parent, Vector3 pos, float yaw, string name)
        {
            var chair = Group(name, parent);
            chair.localPosition = pos;
            chair.localRotation = Quaternion.Euler(0, yaw, 0);
            Box("Assise", chair, new Vector3(0, .46f, 0), new Vector3(.58f, .09f, .58f), Mat("blue"));
            Box("Dossier", chair, new Vector3(0, .78f, .27f), new Vector3(.58f, .56f, .08f), Mat("blue"));
            for (int x = -1; x <= 1; x += 2)
                for (int z = -1; z <= 1; z += 2)
                    Box("Pied_" + x + "_" + z, chair, new Vector3(x * .23f, .22f, z * .23f), new Vector3(.045f, .44f, .045f), Mat("steel"));
        }

        static void InteriorCafeTable(Transform parent, Vector3 pos)
        {
            var table = Group("Table_bistrot_et_chaises", parent);
            table.localPosition = pos;
            Cylinder("Plateau", table, new Vector3(0, .77f, 0), .62f, .075f, Mat("wood"));
            Cylinder("Colonne", table, new Vector3(0, .38f, 0), .06f, .73f, Mat("steel"));
            Cylinder("Embase", table, new Vector3(0, .06f, 0), .33f, .06f, Mat("steel"));
            InteriorChair(table, new Vector3(0, 0, -.98f), 180, "Chaise_1");
            InteriorChair(table, new Vector3(0, 0, .98f), 0, "Chaise_2");
            Cylinder("Tasse_sur_table", table, new Vector3(.2f, .88f, .1f), .075f, .14f, Mat("white"), false);
            Box("Carte_menu", table, new Vector3(-.2f, .83f, 0), new Vector3(.25f, .015f, .35f), Mat("white"), false);
        }

        static void InteriorBookshelf(Transform parent, float width, int shelves, int variation)
        {
            Box("Fond_presentoir", parent, new Vector3(0, shelves * .29f + .22f, .38f), new Vector3(width, shelves * .58f + .45f, .08f), Mat("wood"));
            for (int s = -1; s <= 1; s += 2)
                Box("Joue_" + s, parent, new Vector3(s * width / 2, shelves * .29f + .22f, 0), new Vector3(.08f, shelves * .58f + .45f, .85f), Mat("wood"));
            string[] colors = { "blue", "magenta", "white", "yellow", "green", "red" };
            int count = Mathf.Max(4, Mathf.RoundToInt(width / .35f));
            for (int shelf = 0; shelf < shelves; shelf++)
            {
                float y = .3f + shelf * .58f;
                Box("Etagere_" + shelf, parent, new Vector3(0, y, 0), new Vector3(width, .055f, .8f), Mat("white"));
                for (int book = 0; book < count; book++)
                {
                    float x = -width / 2 + .21f + book * (width - .42f) / Mathf.Max(1, count - 1);
                    float height = .34f + ((book + shelf) % 3) * .05f;
                    Box("Livre_" + shelf + "_" + book, parent, new Vector3(x, y + height / 2 + .03f, -.12f), new Vector3(.20f, height, .43f), Mat(colors[(book + shelf + variation) % colors.Length]), false);
                }
            }
        }

        static void InteriorMonitor(Transform parent, Vector3 pos, float yaw)
        {
            var terminal = Group("Terminal_informatique", parent);
            terminal.localPosition = pos;
            terminal.localRotation = Quaternion.Euler(0, yaw, 0);
            Box("Pied_ecran", terminal, new Vector3(0, .14f, 0), new Vector3(.07f, .28f, .08f), Mat("dark"), false);
            Box("Embase", terminal, new Vector3(0, .035f, 0), new Vector3(.4f, .05f, .28f), Mat("dark"), false);
            Box("Ecran", terminal, new Vector3(0, .46f, 0), new Vector3(.76f, .47f, .06f), Mat("dark"), false);
            Box("Affichage", terminal, new Vector3(0, .46f, -.037f), new Vector3(.67f, .39f, .012f), Mat("blue"), false);
            Box("Clavier", terminal, new Vector3(0, .045f, -.35f), new Vector3(.55f, .04f, .21f), Mat("dark"), false);
        }

        static void InteriorTicketMachine(Transform parent, Vector3 pos, string name, string screen = "BILLETS\nTOUTES DESTINATIONS")
        {
            var machine = Group(name, parent);
            machine.localPosition = pos;
            Box("Socle", machine, new Vector3(0, .11f, 0), new Vector3(.94f, .22f, .82f), Mat("steel"));
            Box("Corps", machine, new Vector3(0, .94f, 0), new Vector3(.86f, 1.7f, .72f), Mat("blue"));
            Box("Ecran_cadre", machine, new Vector3(0, 1.37f, -.385f), new Vector3(.65f, .53f, .075f), Mat("dark"), false);
            Box("Ecran_tactile", machine, new Vector3(0, 1.37f, -.432f), new Vector3(.55f, .43f, .025f), Mat("light"), false);
            Label("Interface", machine, screen, new Vector3(0, 1.37f, -.452f), .085f, new Color(.02f, .16f, .3f));
            Box("Lecteur_carte", machine, new Vector3(.22f, .91f, -.395f), new Vector3(.2f, .18f, .075f), Mat("steel"), false);
            Box("Fente_billets", machine, new Vector3(0, .59f, -.39f), new Vector3(.48f, .10f, .07f), Mat("dark"), false);
            Box("Bandeau_SNCF", machine, new Vector3(0, 1.77f, -.38f), new Vector3(.86f, .21f, .045f), Mat("magenta"), false);
            Label("Marque", machine, "SNCF", new Vector3(0, 1.77f, -.409f), .13f, Color.white);
        }

        static void InteriorBin(Transform parent, Vector3 pos, string name)
        {
            var bin = Group(name, parent);
            bin.localPosition = pos;
            for (int i = 0; i < 2; i++)
            {
                float x = -.23f + i * .46f;
                Box("Corbeille_" + i, bin, new Vector3(x, .47f, 0), new Vector3(.42f, .9f, .45f), Mat("steel"));
                Box("Couvercle_" + i, bin, new Vector3(x, .95f, 0), new Vector3(.44f, .08f, .47f), Mat(i == 0 ? "yellow" : "dark"));
                Box("Ouverture_" + i, bin, new Vector3(x, .81f, -.238f), new Vector3(.30f, .16f, .03f), Mat("dark"), false);
                Label("Tri_" + i, bin, i == 0 ? "TRI" : "DECHETS", new Vector3(x, .54f, -.24f), .083f, new Color(.03f, .13f, .2f));
            }
        }

        static void InteriorPlanter(Transform parent, Vector3 pos, float radius)
        {
            var planter = Group("Jardiniere", parent);
            planter.localPosition = pos;
            Cylinder("Bac", planter, new Vector3(0, .33f, 0), radius, .66f, Mat("stone"));
            Cylinder("Terre", planter, new Vector3(0, .67f, 0), radius * .88f, .03f, Mat("dark"), false);
            for (int i = 0; i < 7; i++)
            {
                float angle = i * 2 * Mathf.PI / 7;
                var leaf = Box("Feuillage_" + i, planter, new Vector3(Mathf.Sin(angle) * radius * .35f, 1.12f + (i % 3) * .1f, Mathf.Cos(angle) * radius * .35f), new Vector3(radius * .4f, .87f, radius * .35f), Mat("green"), false);
                leaf.transform.localRotation = Quaternion.Euler(12 * Mathf.Sin(angle), i * 51.4f, 12 * Mathf.Cos(angle));
            }
        }

        static void InteriorVending(Transform parent, Vector3 pos, float yaw, string name)
        {
            var machine = Group(name, parent);
            machine.localPosition = pos;
            machine.localRotation = Quaternion.Euler(0, yaw, 0);
            Box("Carrosserie", machine, new Vector3(0, 1.05f, 0), new Vector3(1.5f, 2.1f, .9f), Mat("dark"));
            Box("Vitrine", machine, new Vector3(-.17f, 1.25f, -.47f), new Vector3(1, 1.4f, .045f), Mat("glass"), false);
            for (int row = 0; row < 4; row++)
            {
                Box("Etagere_" + row, machine, new Vector3(-.17f, .65f + row * .33f, -.33f), new Vector3(1.04f, .04f, .33f), Mat("steel"), false);
                for (int col = 0; col < 4; col++)
                    Cylinder("Boisson_" + row + "_" + col, machine, new Vector3(-.55f + col * .25f, .8f + row * .33f, -.33f), .065f, .23f, Mat((row + col) % 2 == 0 ? "magenta" : "yellow"), false);
            }
            Box("Clavier_paiement", machine, new Vector3(.58f, 1.1f, -.48f), new Vector3(.21f, .65f, .06f), Mat("steel"), false);
            Box("Trappe", machine, new Vector3(-.17f, .28f, -.49f), new Vector3(.95f, .25f, .08f), Mat("steel"), false);
            Label("Titre_distributeur", machine, "PAUSE & VOYAGE", new Vector3(0, 2.15f, -.48f), .20f, new Color(.04f, .17f, .3f));
        }

        static void InteriorWater(Transform parent, Vector3 pos)
        {
            var fountain = Group("Fontaine_eau_potable", parent);
            fountain.localPosition = pos;
            Box("Corps_fontaine", fountain, new Vector3(0, .52f, 0), new Vector3(.65f, 1.04f, .55f), Mat("steel"));
            Cylinder("Bassin", fountain, new Vector3(0, 1.06f, 0), .25f, .035f, Mat("dark"));
            Beam("Col_de_cygne", fountain, new Vector3(0, 1.07f, .18f), new Vector3(0, 1.42f, .18f), .045f, Mat("steel"));
            Beam("Bec", fountain, new Vector3(0, 1.42f, .18f), new Vector3(0, 1.42f, 0), .045f, Mat("steel"));
            Label("Eau_potable", fountain, "EAU POTABLE", new Vector3(0, 1.75f, 0), .20f, new Color(.03f, .2f, .4f));
        }

        static void InteriorFirePoint(Transform parent, Vector3 pos, float yaw)
        {
            var point = Group("Point_incendie", parent);
            point.localPosition = pos;
            point.localRotation = Quaternion.Euler(0, yaw, 0);
            Box("Support", point, new Vector3(0, 1, 0), new Vector3(.85f, 2, .12f), Mat("white"));
            Cylinder("Extincteur", point, new Vector3(0, .85f, -.24f), .14f, .64f, Mat("red"));
            Box("Poignee", point, new Vector3(0, 1.22f, -.24f), new Vector3(.21f, .065f, .09f), Mat("dark"));
            Box("Panneau_rouge", point, new Vector3(0, 1.75f, -.075f), new Vector3(.68f, .37f, .025f), Mat("red"), false);
            Label("Signal_incendie", point, "INCENDIE", new Vector3(0, 1.75f, -.092f), .13f, Color.white);
            Box("Declencheur_manuel", point, new Vector3(.26f, 1.2f, -.09f), new Vector3(.16f, .16f, .07f), Mat("red"));
        }

        static void InteriorQueuePost(Transform parent, Vector3 pos)
        {
            var post = Group("Potelet_file_attente", parent);
            post.localPosition = pos;
            Cylinder("Embase", post, new Vector3(0, .035f, 0), .22f, .07f, Mat("steel"));
            Cylinder("Montant", post, new Vector3(0, .47f, 0), .045f, .9f, Mat("steel"));
            Cylinder("Tete", post, new Vector3(0, .91f, 0), .07f, .11f, Mat("blue"));
        }

        static void InteriorBrochureRack(Transform parent, Vector3 pos)
        {
            var rack = Group("Presentoir_informations_voyageurs", parent);
            rack.localPosition = pos;
            Box("Pied", rack, new Vector3(0, .58f, 0), new Vector3(.06f, 1.16f, .12f), Mat("steel"));
            Box("Embase", rack, new Vector3(0, .045f, 0), new Vector3(.75f, .09f, .65f), Mat("steel"));
            Box("Support_depliants", rack, new Vector3(0, 1.16f, 0), new Vector3(.85f, .8f, .18f), Mat("blue"));
            for (int i = 0; i < 3; i++)
                Box("Brochure_" + i, rack, new Vector3(-.27f + i * .27f, 1.17f, -.11f), new Vector3(.21f, .5f, .035f), Mat(i == 1 ? "magenta" : "white"), false);
            Label("Informations", rack, "INFORMATIONS", new Vector3(0, 1.76f, 0), .16f, new Color(.03f, .18f, .35f));
        }

        static void InteriorSign(Transform parent, string name, Vector3 pos, string text, float width, float yaw = 0)
        {
            var sign = Group(name, parent);
            sign.localPosition = pos;
            sign.localRotation = Quaternion.Euler(0, yaw, 0);
            Box("Caisson", sign, Vector3.zero, new Vector3(width, .85f, .15f), Mat("blue"), false);
            Label("Signaletique", sign, text, new Vector3(0, 0, -.09f), .43f, Color.white);
            for (int s = -1; s <= 1; s += 2)
            {
                float worldX = pos.x + s * width * .38f * Mathf.Cos(yaw * Mathf.Deg2Rad);
                float roofY = pos.z >= 28 ? 7.05f : 10 + 5 * Mathf.Sin((Mathf.Clamp(worldX, -20, 20) + 20) / 40 * Mathf.PI);
                Beam("Suspente_" + s, sign, new Vector3(s * width * .38f, .43f, 0), new Vector3(s * width * .38f, roofY - pos.y, 0), .035f, Mat("steel"));
            }
        }
    }
}
#endif
