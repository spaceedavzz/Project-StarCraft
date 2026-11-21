// KAESA StarCraft - ModuleKAESAEngineModes
// Engine modes for the Super Heavy cluster, as the real booster flies them: All 33 -> Inner 13 -> Center 3.
// The cluster is ONE ModuleEnginesFX with 33 thrust transforms (stock delta-v / TWR reads only one engine module per
// part, so three modules showed the centre three alone). A mode keeps the engine running and:
//  - sets maxThrust / minThrust (and the fuel flows) to (engines in the mode) x one Raptor;
//  - gives each thrust transform of the mode 1/n of the thrust and the others 0 (thrustTransformMultipliers);
//  - hides the Waterfall plumes of what is off: per ring (a plume per nozzle, under its gimbal) or per mode (merged sets).
// Rings are told apart by each thrust transform's distance from the part axis. Works in the editor too, so the stage
// delta-v / TWR show the chosen mode.
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAEngineModes : PartModule
    {
        [KSPField] public string engineID = "Cluster";
        [KSPField] public float centerMaxRadius = 0.8f;      // thrust transforms inside this radius = centre ring
        [KSPField] public float middleMaxRadius = 1.8f;      // inside this = middle ring, beyond = outer ring
        // plume anchors (Waterfall effects live under them), either way, empty = not used:
        //  - per ring: one plume per nozzle under its gimbal, shown while the ring is lit (the cluster's setup);
        //  - per mode: one merged plume set per mode (Source/plume_merged.py; lighter, but static with the gimbals)
        [KSPField] public string plumeCenterRing = "";
        [KSPField] public string plumeMiddleRing = "";
        [KSPField] public string plumeOuterRing = "";
        [KSPField] public string plumeAll33 = "";
        [KSPField] public string plumeInner13 = "";
        [KSPField] public string plumeCenter3 = "";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Engine mode", groupName = "kaesaEngines",
            groupDisplayName = "Pterodactyl cluster")]
        [UI_ChooseOption(options = new string[] { "All33", "Inner13", "Center3" }, display = new string[] { "33 engines", "13 engines", "3 engines" },
            scene = UI_Scene.All)]
        public string engineMode = "All33";

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Engines in mode", groupName = "kaesaEngines",
            groupDisplayName = "Pterodactyl cluster")]
        public int enginesInMode = 0;

        private ModuleEngines engine;
        private int[] ring;                 // 0 centre, 1 middle, 2 outer, per thrust transform
        private float perMaxThrust, perMinThrust;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            engine = FindEngine(part);
            if (engine == null || engine.thrustTransforms == null || engine.thrustTransforms.Count == 0)
            {
                Debug.LogWarning("[KAESA] ModuleKAESAEngineModes: engine '" + engineID + "' with thrust transforms not found in " + part.partInfo.name);
                return;
            }
            // per-engine thrust from the prefab (a copied part could carry an already-scaled maxThrust)
            ModuleEngines proto = part.partInfo != null && part.partInfo.partPrefab != null ? FindEngine(part.partInfo.partPrefab) : null;
            float fullMax = proto != null ? proto.maxThrust : engine.maxThrust, fullMin = proto != null ? proto.minThrust : engine.minThrust;
            int n = engine.thrustTransforms.Count;
            perMaxThrust = fullMax / n; perMinThrust = fullMin / n;
            ring = new int[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = part.transform.InverseTransformPoint(engine.thrustTransforms[i].position);
                float r = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                ring[i] = r < centerMaxRadius ? 0 : r < middleMaxRadius ? 1 : 2;
            }
            UI_Control c = Fields["engineMode"].uiControlFlight;
            if (c != null) c.onFieldChanged = OnModeChanged;
            c = Fields["engineMode"].uiControlEditor;
            if (c != null) c.onFieldChanged = OnModeChanged;
            Apply();
        }

        private ModuleEngines FindEngine(Part p)
        {
            for (int i = 0; i < p.Modules.Count; i++)
            {
                ModuleEngines e = p.Modules[i] as ModuleEngines;
                if (e != null && e.engineID == engineID) return e;
            }
            return null;
        }

        private void OnModeChanged(BaseField f, object old) { Apply(); }

        private bool RingOn(int r)
        {
            if (r == 0) return true;
            if (r == 1) return engineMode != "Center3";
            return engineMode == "All33";
        }

        private void Apply()
        {
            if (engine == null || ring == null) return;
            int n = 0;
            for (int i = 0; i < ring.Length; i++) if (RingOn(ring[i])) n++;
            if (n == 0) return;
            enginesInMode = n;
            engine.maxThrust = perMaxThrust * n;
            engine.minThrust = perMinThrust * n;
            float isp0 = engine.atmosphereCurve.Evaluate(0f), g = engine.g > 0f ? engine.g : 9.80665f;
            if (isp0 > 0f)
            {
                engine.maxFuelFlow = engine.maxThrust / (isp0 * g);
                engine.minFuelFlow = engine.minThrust / (isp0 * g);
            }
            if (engine.thrustTransformMultipliers == null) engine.thrustTransformMultipliers = new System.Collections.Generic.List<float>();
            while (engine.thrustTransformMultipliers.Count < ring.Length) engine.thrustTransformMultipliers.Add(0f);
            for (int i = 0; i < ring.Length; i++) engine.thrustTransformMultipliers[i] = RingOn(ring[i]) ? 1f / n : 0f;
            ShowPlumes(plumeCenterRing, RingOn(0)); ShowPlumes(plumeMiddleRing, RingOn(1)); ShowPlumes(plumeOuterRing, RingOn(2));
            ShowPlumes(plumeAll33, engineMode == "All33");
            ShowPlumes(plumeInner13, engineMode == "Inner13");
            ShowPlumes(plumeCenter3, engineMode == "Center3");
            // let the stage delta-v / TWR readouts pick the new thrust up
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            else if (HighLogic.LoadedSceneIsFlight && vessel != null && vessel.VesselDeltaV != null)
                vessel.VesselDeltaV.SetCalcsDirty(true, true);
        }

        private void ShowPlumes(string name, bool on)
        {
            if (string.IsNullOrEmpty(name)) return;
            Transform[] ts = part.FindModelTransforms(name);
            for (int i = 0; i < ts.Length; i++)
                if (ts[i].gameObject.activeSelf != on) ts[i].gameObject.SetActive(on);
        }

        private void SetMode(string m) { engineMode = m; Apply(); }

        private string NextMode()
        {
            if (engineMode == "All33") return "Inner13";
            if (engineMode == "Inner13") return "Center3";
            return "All33";
        }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Next engine mode", groupName = "kaesaEngines", groupDisplayName = "Pterodactyl cluster")]
        public void EventNextMode() { SetMode(NextMode()); }

        [KSPAction("Engines: 33")]
        public void Action33(KSPActionParam p) { SetMode("All33"); }

        [KSPAction("Engines: 13")]
        public void Action13(KSPActionParam p) { SetMode("Inner13"); }

        [KSPAction("Engines: 3")]
        public void Action3(KSPActionParam p) { SetMode("Center3"); }

        [KSPAction("Engines: next mode")]
        public void ActionNext(KSPActionParam p) { SetMode(NextMode()); }

        public override string GetModuleDisplayName()
        {
            return "Pterodactyl cluster modes";
        }

        public override string GetInfo()
        {
            return "Engine modes: 33 (all) / 13 (centre + middle ring, the ones that gimbal) / 3 (centre), from the part menu or " +
                   "action groups. Thrust and fuel flow follow the number of engines in the mode.";
        }
    }
}
