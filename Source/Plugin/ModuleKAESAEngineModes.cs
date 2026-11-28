// KAESA StarCraft - ModuleKAESAEngineModes (v2, written from scratch 2026-11-26)
//
// Lit-nozzle sets for a clustered engine part, the way the real Super Heavy flies them (SpaceX webcasts: all 33 for the
// ascent, the inner 13 for the boostback, 13 -> 5 -> 3 for the landing burn: the centre three plus two of the middle ring
// before the last seconds on three; the 5 step added 2026-11-26 at the user's note).
//
// History: v1 of this module (2026-11-15) followed the idea of SEP's ModuleSEPEngineSwitch, and the SEP team pointed out in
// 2026-11 that it was too close to their code. This v2 is a new file written from a description of what it must do. SEP's
// code was not looked at; only v1's field list was read, to keep the part-menu names, action names and the saved field so
// existing crafts and kOS scripts still work. Afterwards, SEP's DLL was compared by structure only: SEP switches between
// several engine modules; this one scales a single engine per nozzle. The old source stays in KAESA_backup/plugin_reescrita_2026-11-26.
//
// How it works:
//  - the part has ONE ModuleEnginesFX with every nozzle as a thrust transform (KSP's stage delta-v / TWR reads one engine
//    module per part); this module scales that one engine to the nozzles that are lit;
//  - each nozzle gets a weight 0..1 (how lit it is). Thrust and fuel flow = (sum of weights) x one nozzle's share of the
//    cfg values; the thrust is split over the nozzles by weight (thrustTransformMultipliers);
//  - nozzles are sorted into rings by their distance from the part axis (centre / middle / outer);
//  - a mode is a set of rings, plus optionally some nozzles of one more ring, evenly spaced. Built in: All33 (every ring),
//    Inner13 (centre + middle), Inner5 (centre + 2 opposite middle nozzles), Center3 (centre). A cfg can replace them:
//    MODE { name = X  title = Shown name  rings = 0, 1  partial = 2, 4 }   (rings: 0 centre, 1 middle, 2 outer;
//    partial = ring, how many of its nozzles);
//  - NEW in v2: a switch in flight is not a jump. The weights move from the old set to the new one over switchTime
//    seconds (nozzles spooling down / up), so 33 -> 13 does not kick the booster; 0 = instant;
//  - each plume anchor goes with its nearest nozzle and is hidden while that nozzle is dark;
//  - works in the editor (the stage delta-v / TWR follow the mode) and from kOS ("engines: 33" / "13" / "5" / "3" actions, or
//    setfield on "engine mode").
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAEngineModes : PartModule
    {
        private const string GRP = "kaesaEngines";
        private const string GRP_NAME = "Pterodactyl cluster";

        // ---------- cfg
        [KSPField] public string engineID = "";               // engine module to drive; empty = the part's first ModuleEngines
        [KSPField] public float centerMaxRadius = 0.8f;       // m from the part axis: inside = centre ring
        [KSPField] public float middleMaxRadius = 1.8f;       // inside = middle ring, beyond = outer ring
        [KSPField] public float switchTime = 0.2f;            // s for the thrust to move to a new set in flight
        [KSPField] public string plumeCenterRing = "";        // transforms (all with that name) shown while the ring is lit
        [KSPField] public string plumeMiddleRing = "";
        [KSPField] public string plumeOuterRing = "";

        // ---------- part menu (field name and values kept from v1: crafts save them)
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Engine mode", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_ChooseOption(options = new string[] { "All33", "Inner13", "Inner5", "Center3" }, display = new string[] { "33 engines", "13 engines", "5 engines", "3 engines" },
            scene = UI_Scene.All)]
        public string engineMode = "All33";

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Engines in mode", groupName = GRP, groupDisplayName = GRP_NAME)]
        public int enginesInMode = 0;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Mode thrust (vac)", guiUnits = " kN", guiFormat = "F0", groupName = GRP, groupDisplayName = GRP_NAME)]
        public float modeThrust = 0f;

        // ---------- state
        private class Mode
        {
            public string name, title;
            public bool[] lit;              // per nozzle
        }

        private readonly List<Mode> modes = new List<Mode>();
        private ModuleEngines engine;
        private int[] ringOf;               // ring of each thrust transform
        private float[] azOf;               // its azimuth about the part axis, deg 0..360
        private float[] weight;             // how lit each nozzle is now (0..1)
        private float[] fromW, toW;         // switch in progress: start and end weights
        private float switchLeft;           // s left in the switch
        private float nozzleMax, nozzleMin; // one nozzle's share of the cfg thrust
        private float ispVac = 1f, g0 = 9.80665f;
        private readonly List<Transform> plumes = new List<Transform>();   // every plume anchor
        private readonly List<int> plumeNozzle = new List<int>();          // the nozzle each one belongs to
        private string appliedMode;

        // ================================================================ setup
        public override void OnStart(StartState state)
        {
            engine = FindEngine(part);
            if (engine == null || engine.thrustTransforms == null || engine.thrustTransforms.Count == 0)
            {
                Debug.LogWarning("[KAESA] ModuleKAESAEngineModes: no engine '" + engineID + "' with thrust transforms on " + part.partInfo.name);
                return;
            }
            ReadNozzleShare();
            SortRings();
            ReadModes();
            FindPlumes();

            UI_ChooseOption ce = Fields["engineMode"].uiControlEditor as UI_ChooseOption;
            UI_ChooseOption cf = Fields["engineMode"].uiControlFlight as UI_ChooseOption;
            string[] names = new string[modes.Count], titles = new string[modes.Count];
            for (int i = 0; i < modes.Count; i++) { names[i] = modes[i].name; titles[i] = modes[i].title; }
            if (ce != null) { ce.options = names; ce.display = titles; }
            if (cf != null) { cf.options = names; cf.display = titles; }
            Fields["engineMode"].OnValueModified += OnModeField;

            if (ModeByName(engineMode) == null) engineMode = modes[0].name;
            SetMode(engineMode, true);
        }

        public void OnDestroy()
        {
            if (Fields != null && Fields["engineMode"] != null) Fields["engineMode"].OnValueModified -= OnModeField;
        }

        private ModuleEngines FindEngine(Part p)
        {
            if (p == null) return null;
            for (int i = 0; i < p.Modules.Count; i++)
            {
                ModuleEngines e = p.Modules[i] as ModuleEngines;
                if (e != null && (string.IsNullOrEmpty(engineID) || e.engineID == engineID)) return e;
            }
            return null;
        }

        // the cfg numbers come from the part prefab: an editor copy of a part would carry an already scaled engine
        private void ReadNozzleShare()
        {
            ModuleEngines proto = part.partInfo != null ? FindEngine(part.partInfo.partPrefab) : null;
            if (proto == null) proto = engine;
            int n = engine.thrustTransforms.Count;
            nozzleMax = proto.maxThrust / n;
            nozzleMin = proto.minThrust / n;
            g0 = engine.g > 0f ? engine.g : 9.80665f;
            ispVac = engine.atmosphereCurve != null ? engine.atmosphereCurve.Evaluate(0f) : 0f;
            if (ispVac <= 0f) ispVac = 1f;
        }

        private void SortRings()
        {
            int n = engine.thrustTransforms.Count;
            ringOf = new int[n]; azOf = new float[n]; weight = new float[n]; fromW = new float[n]; toW = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = part.transform.InverseTransformPoint(engine.thrustTransforms[i].position);
                float r = new Vector2(p.x, p.z).magnitude;          // part axis = local Y
                ringOf[i] = r < centerMaxRadius ? 0 : (r < middleMaxRadius ? 1 : 2);
                azOf[i] = (Mathf.Atan2(p.z, p.x) * Mathf.Rad2Deg + 360f) % 360f;
            }
            while (engine.thrustTransformMultipliers.Count < n) engine.thrustTransformMultipliers.Add(1f / n);
        }

        private void ReadModes()
        {
            modes.Clear();
            ConfigNode cfg = OwnConfig();
            ConfigNode[] nodes = cfg != null ? cfg.GetNodes("MODE") : new ConfigNode[0];
            for (int i = 0; i < nodes.Length; i++)
            {
                string name = nodes[i].GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                bool[] rings = new bool[3];
                foreach (string s in (nodes[i].GetValue("rings") ?? "").Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int k;
                    if (int.TryParse(s, out k) && k >= 0 && k < 3) rings[k] = true;
                }
                // optional "partial = ring, count": that many nozzles of one more ring, evenly spaced around it
                int pRing = -1, pCount = 0;
                string[] pp = (nodes[i].GetValue("partial") ?? "").Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (pp.Length >= 2) { int.TryParse(pp[0], out pRing); int.TryParse(pp[1], out pCount); }
                AddMode(name, nodes[i].GetValue("title"), rings[0], rings[1], rings[2], pRing, pCount);
            }
            if (modes.Count > 0) return;
            // the real booster's landing: 13 -> 5 (centre 3 + 2 of the middle ring) -> 3
            AddMode("All33", null, true, true, true, -1, 0);
            AddMode("Inner13", null, true, true, false, -1, 0);
            AddMode("Inner5", null, true, false, false, 1, 2);
            AddMode("Center3", null, true, false, false, -1, 0);
        }

        private void AddMode(string name, string title, bool c, bool m, bool o, int pRing, int pCount)
        {
            bool[] rings = { c, m, o };
            Mode md = new Mode { name = name, lit = new bool[ringOf.Length] };
            for (int i = 0; i < ringOf.Length; i++) md.lit[i] = rings[ringOf[i]];
            if (pRing >= 0 && pRing < 3 && !rings[pRing] && pCount > 0)
            {
                // nozzles of that ring by azimuth, then every (n / count)-th: two of ten = opposite each other
                List<int> ring = new List<int>();
                for (int i = 0; i < ringOf.Length; i++) if (ringOf[i] == pRing) ring.Add(i);
                ring.Sort((a, b) => azOf[a].CompareTo(azOf[b]));
                int take = Mathf.Min(pCount, ring.Count);
                for (int j = 0; j < take; j++) md.lit[ring[j * ring.Count / take]] = true;
            }
            int n = Count(md);
            if (n == 0) return;                                  // a ring this cluster does not have
            md.title = string.IsNullOrEmpty(title) ? n + " engines" : title;
            modes.Add(md);
        }

        private ConfigNode OwnConfig()
        {
            if (part.partInfo == null || part.partInfo.partConfig == null) return null;
            ConfigNode[] mods = part.partInfo.partConfig.GetNodes("MODULE");
            for (int i = 0; i < mods.Length; i++)
                if (mods[i].GetValue("name") == moduleName) return mods[i];
            return null;
        }

        private void FindPlumes()
        {
            // each plume anchor goes with the nearest nozzle of its ring, so a mode that lights part of a ring shows only those
            string[] names = { plumeCenterRing, plumeMiddleRing, plumeOuterRing };
            plumes.Clear(); plumeNozzle.Clear();
            for (int k = 0; k < 3; k++)
            {
                if (string.IsNullOrEmpty(names[k])) continue;
                foreach (Transform t in part.FindModelTransforms(names[k]))
                {
                    int best = -1; float bd = float.MaxValue;
                    for (int i = 0; i < ringOf.Length; i++)
                    {
                        if (ringOf[i] != k) continue;
                        Vector3 d = t.position - engine.thrustTransforms[i].position;
                        Vector3 dl = part.transform.InverseTransformDirection(d);
                        float dist = new Vector2(dl.x, dl.z).sqrMagnitude;          // across the axis only
                        if (dist < bd) { bd = dist; best = i; }
                    }
                    if (best >= 0) { plumes.Add(t); plumeNozzle.Add(best); }
                }
            }
        }

        private static int Count(Mode m)
        {
            int n = 0;
            for (int i = 0; i < m.lit.Length; i++) if (m.lit[i]) n++;
            return n;
        }

        private Mode ModeByName(string name)
        {
            for (int i = 0; i < modes.Count; i++) if (modes[i].name == name) return modes[i];
            return null;
        }

        // ================================================================ switching
        private void OnModeField(object value) { SetMode(engineMode, false); }

        private void SetMode(string name, bool instant)
        {
            if (engine == null || ringOf == null) return;
            Mode m = ModeByName(name);
            if (m == null) return;
            engineMode = m.name;
            enginesInMode = Count(m);
            modeThrust = nozzleMax * enginesInMode;

            for (int i = 0; i < weight.Length; i++) { fromW[i] = weight[i]; toW[i] = m.lit[i] ? 1f : 0f; }
            bool smooth = !instant && HighLogic.LoadedSceneIsFlight && switchTime > 0.01f && engine.EngineIgnited && appliedMode != null;
            if (smooth) switchLeft = switchTime;
            else { switchLeft = 0f; Array.Copy(toW, weight, weight.Length); Apply(); }
            appliedMode = m.name;

            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        public void FixedUpdate()
        {
            if (engine == null || switchLeft <= 0f) return;
            switchLeft = Mathf.Max(0f, switchLeft - TimeWarp.fixedDeltaTime);
            float t = 1f - switchLeft / switchTime;
            t = t * t * (3f - 2f * t);                           // ease in and out
            for (int i = 0; i < weight.Length; i++) weight[i] = Mathf.Lerp(fromW[i], toW[i], t);
            Apply();
        }

        // thrust, fuel flow, thrust split and plumes from the nozzle weights
        private void Apply()
        {
            float lit = 0f;
            for (int i = 0; i < weight.Length; i++) lit += weight[i];
            lit = Mathf.Max(lit, 0.001f);
            engine.maxThrust = nozzleMax * lit;
            engine.minThrust = nozzleMin * lit;
            engine.maxFuelFlow = engine.maxThrust / (ispVac * g0);
            engine.minFuelFlow = engine.minThrust / (ispVac * g0);
            for (int i = 0; i < weight.Length; i++) engine.thrustTransformMultipliers[i] = weight[i] / lit;

            for (int j = 0; j < plumes.Count; j++)
            {
                bool on = weight[plumeNozzle[j]] > 0.01f;
                if (plumes[j] != null && plumes[j].gameObject.activeSelf != on) plumes[j].gameObject.SetActive(on);
            }
        }

        private string NextMode()
        {
            for (int i = 0; i < modes.Count; i++)
                if (modes[i].name == engineMode) return modes[(i + 1) % modes.Count].name;
            return modes.Count > 0 ? modes[0].name : engineMode;
        }

        private string ModeWithCount(int n)
        {
            for (int i = 0; i < modes.Count; i++) if (Count(modes[i]) == n) return modes[i].name;
            return engineMode;
        }

        // ================================================================ menu and action groups (names kept from v1 for kOS)
        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Next engine mode", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void EventNextMode() { SetMode(NextMode(), false); }

        [KSPAction("Engines: 33")]
        public void Action33(KSPActionParam p) { SetMode(ModeWithCount(33), false); }

        [KSPAction("Engines: 13")]
        public void Action13(KSPActionParam p) { SetMode(ModeWithCount(13), false); }

        [KSPAction("Engines: 5")]
        public void Action5(KSPActionParam p) { SetMode(ModeWithCount(5), false); }

        [KSPAction("Engines: 3")]
        public void Action3(KSPActionParam p) { SetMode(ModeWithCount(3), false); }

        [KSPAction("Engines: next mode")]
        public void ActionNext(KSPActionParam p) { SetMode(NextMode(), false); }

        public override string GetModuleDisplayName() { return "Engine Modes"; }

        public override string GetInfo()
        {
            return "Lights a set of the cluster's nozzles: 33 (all), 13 (centre + middle ring), 5 (centre + 2 middle) or 3 (centre).\n" +
                   "Thrust and fuel flow follow the lit nozzles; in flight a switch spools over " + switchTime.ToString("F1") + " s.\n" +
                   "Part menu, action groups or kOS.";
        }
    }
}
