// KAESA StarCraft - ModuleKAESAChopsticks
// Gigazilla chopsticks as ONE part (2026-11-20; before: two Breaking Ground hinge parts). Turns the two arm transforms
// about the part's vertical axis (the pivot axis), mirrored, so both arms open and close together from one control:
// a slider (opening angle), Open / Close events and action groups. No physical joint per arm: the arms are part of the
// part (their colliders move with them), so there is less to simulate and nothing to wobble.
// While a booster hangs on the rails (a ModuleKAESACatchPin caught on this part), the arms hold still.
using System.Collections.Generic;
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAChopsticks : PartModule
    {
        private const string GRP = "kaesaChopsticks";
        private const string GRP_NAME = "Chopsticks";

        [KSPField] public string leftArmName = "ArmL";
        [KSPField] public string rightArmName = "ArmR";
        // rotation about the part's up axis (Unity Y) that swings each arm outward, away from the booster
        [KSPField] public float leftOpenSign = -1f;
        [KSPField] public float rightOpenSign = 1f;
        [KSPField] public float maxOpen = 70f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Arms open", guiUnits = "°",
                  groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_FloatRange(minValue = 0f, maxValue = 70f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float targetOpen = 0f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Arm speed", guiUnits = "°/s",
                  groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_FloatRange(minValue = 1f, maxValue = 20f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float speed = 8f;

        [KSPField(isPersistant = true)] public float currentOpen = 0f;

        [KSPField(guiActive = true, guiName = "Arms", groupName = GRP, groupDisplayName = GRP_NAME)]
        public string status = "Closed";

        private Transform armL, armR;
        private Quaternion restL, restR;
        private float caughtCheck;
        private bool caught, warned;

        public override void OnStart(StartState state)
        {
            armL = part.FindModelTransform(leftArmName);
            armR = part.FindModelTransform(rightArmName);
            if (armL != null) restL = armL.localRotation;
            if (armR != null) restR = armR.localRotation;
            if (armL == null || armR == null) Debug.LogWarning("[KAESA] ModuleKAESAChopsticks: arm transforms not found in " + part.partInfo.name);
            foreach (UI_Control c in new UI_Control[] { Fields["targetOpen"].uiControlEditor, Fields["targetOpen"].uiControlFlight })
            {
                UI_FloatRange r = c as UI_FloatRange;
                if (r != null) r.maxValue = maxOpen;
            }
            targetOpen = Mathf.Clamp(targetOpen, 0f, maxOpen);
            if (!HighLogic.LoadedSceneIsFlight) currentOpen = targetOpen;
            Apply();
        }

        public void Update()
        {
            if (HighLogic.LoadedSceneIsEditor) { currentOpen = targetOpen; Apply(); }
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            caughtCheck -= TimeWarp.fixedDeltaTime;
            if (caughtCheck <= 0f) { caughtCheck = 0.5f; caught = BoosterCaught(); }
            if (caught)
            {
                if (Mathf.Abs(targetOpen - currentOpen) > 0.01f)
                {
                    targetOpen = currentOpen;
                    if (!warned) { ScreenMessages.PostScreenMessage("Chopsticks: a booster is held on the rails - release its catch pins first", 4f); warned = true; }
                }
                status = "Holding a booster";
                return;
            }
            warned = false;
            float step = speed * TimeWarp.fixedDeltaTime;
            if (Mathf.Abs(targetOpen - currentOpen) > 0.001f)
            {
                currentOpen = Mathf.MoveTowards(currentOpen, targetOpen, step);
                Apply();
                status = targetOpen > currentOpen ? "Opening" : "Closing";
            }
            else status = currentOpen < 0.5f ? "Closed" : "Open " + currentOpen.ToString("F0") + "°";
        }

        private void Apply()
        {
            if (armL != null) armL.localRotation = restL * Quaternion.AngleAxis(leftOpenSign * currentOpen, Vector3.up);
            if (armR != null) armR.localRotation = restR * Quaternion.AngleAxis(rightOpenSign * currentOpen, Vector3.up);
        }

        // a catch pin on any loaded vessel locked onto this part
        private bool BoosterCaught()
        {
            List<Vessel> vs = FlightGlobals.VesselsLoaded;
            for (int i = 0; i < vs.Count; i++)
            {
                if (vs[i] == vessel) continue;
                List<Part> ps = vs[i].parts;
                for (int j = 0; j < ps.Count; j++)
                {
                    List<ModuleKAESACatchPin> pins = ps[j].FindModulesImplementing<ModuleKAESACatchPin>();
                    for (int k = 0; k < pins.Count; k++)
                        if (pins[k].caughtArmId != 0 && pins[k].caughtArmId == part.flightID) return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ remote control (2026-11-20): another vessel - the
        // booster coming back, through its catch pins (ModuleKAESACatchPin) - can open / close the nearest tower's arms
        public string StateText { get { return status; } }
        public float OpenAngle { get { return currentOpen; } }

        public static ModuleKAESAChopsticks Nearest(Vector3 from, float range, out float dist)
        {
            ModuleKAESAChopsticks best = null; dist = range;
            List<Vessel> vs = FlightGlobals.VesselsLoaded;
            for (int i = 0; i < vs.Count; i++)
            {
                if ((vs[i].transform.position - from).sqrMagnitude > range * range * 4f) continue;
                List<Part> ps = vs[i].parts;
                for (int j = 0; j < ps.Count; j++)
                {
                    ModuleKAESAChopsticks c = ps[j].FindModuleImplementing<ModuleKAESAChopsticks>();
                    if (c == null) continue;
                    float d = Vector3.Distance(ps[j].transform.position, from);
                    if (d <= dist) { dist = d; best = c; }
                }
            }
            return best;
        }

        // also usable from another vessel (right-click the tower's arms while flying the booster): guiActiveUnfocused
        [KSPEvent(guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, externalToEVAOnly = false, unfocusedRange = 2500f,
                  guiName = "Open arms", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void OpenArms() { targetOpen = maxOpen; }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, externalToEVAOnly = false, unfocusedRange = 2500f,
                  guiName = "Close arms", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void CloseArms() { targetOpen = 0f; }

        [KSPEvent(guiActive = true, guiActiveUnfocused = true, externalToEVAOnly = false, unfocusedRange = 2500f,
                  guiName = "Arms: stop here", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void StopArms() { targetOpen = currentOpen; }

        [KSPAction("Open arms")] public void ActOpen(KSPActionParam p) { OpenArms(); }
        [KSPAction("Close arms")] public void ActClose(KSPActionParam p) { CloseArms(); }
        [KSPAction("Open / close arms")] public void ActToggle(KSPActionParam p) { if (targetOpen > 0.5f) CloseArms(); else OpenArms(); }

        public override string GetModuleDisplayName() { return "Chopsticks"; }

        public override string GetInfo()
        {
            return "Both arms turn together about the pivot, mirrored: 0° = closed (catch pose) to " + maxOpen.ToString("F0") +
                   "° open. They hold still while a booster hangs on the rails.";
        }
    }
}
