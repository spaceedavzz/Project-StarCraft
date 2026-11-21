// KAESA StarCraft - ModuleKAESACatchPin
// Tower catch pin on the Super Heavy's grid fin pods, written from scratch for StarCraft v2 (2026-11-20). NOT a docking
// port: when the pin is armed and comes down onto a Gigazilla arm's rail (ModuleKAESACatchArm) slowly enough, it makes a
// physical joint between the pin's part and the arm, holding the pose it touched in. The two vessels stay separate.
//  - window: along the rail within its length, across within catchRadius, from catchBelow under the rail top to
//    catchAbove over it; relative speed under maxCatchSpeed;
//  - while caught, the booster's colliders ignore the arm's (the pin really sits on the rail; no physics fight);
//  - the joint breaks if its force goes over breakForce (kN); "Release" lets go on purpose;
//  - the arm is remembered by its part flightID: after a reload or coming off rails the joint is made again.
// The B9 "no catch pin" subtype hides the CatchPoint transform; then the module does nothing.
using System.Collections.Generic;
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESACatchPin : PartModule
    {
        private const string GRP = "kaesaCatchPin";
        private const string GRP_NAME = "Catch pin";

        [KSPField] public string pinTransformName = "CatchPoint";
        // false on the part variant without a pin (the V3's rear fin): the pin is hidden and the module stays out of the
        // way (two parts instead of a switch, 2026-11-20)
        [KSPField] public bool pinFitted = true;
        [KSPField] public float catchRadius = 0.6f;       // m across the rail
        [KSPField] public float catchAbove = 0.8f;        // m over the rail top
        [KSPField] public float catchBelow = 0.4f;        // m under the rail top
        [KSPField] public float maxCatchSpeed = 3f;       // m/s, pin vs arm
        [KSPField] public float breakForce = 6000f;       // kN
        [KSPField] public float searchRange = 60f;        // m, vessels farther than this are not looked at

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Armed", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_Toggle(enabledText = "Yes", disabledText = "No", scene = UI_Scene.All)]
        public bool armed = false;

        [KSPField(isPersistant = true)] public uint caughtArmId = 0;      // flightID of the arm part we hang on

        [KSPField(guiActive = true, guiName = "Pin", groupName = GRP, groupDisplayName = GRP_NAME)]
        public string status = "-";

        // remote control of the nearest Gigazilla's chopsticks from the booster (2026-11-20): no need to switch vessels
        [KSPField] public float towerRange = 2500f;       // m (about the physics range of loaded vessels)
        [KSPField(guiActive = true, guiName = "Tower arms", groupName = GRP, groupDisplayName = GRP_NAME)]
        public string towerStatus = "-";
        private float towerCheck;

        private Transform pin;
        private ConfigurableJoint joint;
        private Part armPart;
        private readonly List<KeyValuePair<Collider, Collider>> ignored = new List<KeyValuePair<Collider, Collider>>();
        private float restoreTimer;

        public override void OnStart(StartState state)
        {
            pin = part.FindModelTransform(pinTransformName);
            if (!pinFitted)
            {
                if (pin != null) pin.gameObject.SetActive(false);
                pin = null; armed = false; isEnabled = false;
                foreach (BaseField f in Fields) { f.guiActive = false; f.guiActiveEditor = false; }
                foreach (BaseEvent e in Events) { e.guiActive = false; e.guiActiveEditor = false; }
                foreach (BaseAction a in Actions) a.active = false;
                return;
            }
            if (!HighLogic.LoadedSceneIsFlight) return;
            GameEvents.onVesselGoOnRails.Add(OnRails);
        }

        public void OnDestroy()
        {
            GameEvents.onVesselGoOnRails.Remove(OnRails);
            DropJoint(false);
        }

        private void OnRails(Vessel v)
        {
            if (armPart != null && (v == vessel || v == armPart.vessel)) DropJoint(false);   // keeps caughtArmId
        }

        private bool PinActive { get { return pin != null && pin.gameObject.activeInHierarchy; } }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return;
            if (!PinActive) { status = "no pin fitted"; return; }
            towerCheck -= TimeWarp.fixedDeltaTime;
            if (towerCheck <= 0f)
            {
                towerCheck = 0.5f; float d;
                ModuleKAESAChopsticks c = ModuleKAESAChopsticks.Nearest(part.transform.position, towerRange, out d);
                towerStatus = c == null ? "no tower in range" : c.StateText + ", " + d.ToString("F0") + " m";
            }
            if (restoreTimer > 0f) { restoreTimer -= TimeWarp.fixedDeltaTime; if (restoreTimer <= 0f) RestoreCollisions(); }
            if (vessel.packed || part.Rigidbody == null) return;

            if (joint != null)
            {
                if (armPart == null || armPart.Rigidbody == null || armPart.vessel == null) { Release(); return; }
                float f = joint.currentForce.magnitude;                    // kN (masses in tonnes)
                status = "caught (" + f.ToString("F0") + " kN)";
                if (f > breakForce) { ScreenMessages.PostScreenMessage("Catch pin overloaded: let go", 4f); Release(); }
                return;
            }

            if (caughtArmId != 0)                                          // remake the joint after a reload / off rails
            {
                Part p = FindArmPart(caughtArmId);
                if (p != null && !p.vessel.packed && p.Rigidbody != null) MakeJoint(p);
                else status = "waiting for the arm";
                return;
            }

            if (!armed) { status = "disarmed"; return; }
            status = "armed";
            ModuleKAESACatchArm arm = FindRailUnderPin();
            if (arm != null) MakeJoint(arm.part);
        }

        private Part FindArmPart(uint id)
        {
            List<Vessel> vs = FlightGlobals.VesselsLoaded;
            for (int i = 0; i < vs.Count; i++)
            {
                if (vs[i] == vessel) continue;
                List<Part> ps = vs[i].parts;
                for (int j = 0; j < ps.Count; j++) if (ps[j].flightID == id) return ps[j];
            }
            return null;
        }

        private ModuleKAESACatchArm FindRailUnderPin()
        {
            Vector3 p = pin.position;
            List<Vessel> vs = FlightGlobals.VesselsLoaded;
            for (int i = 0; i < vs.Count; i++)
            {
                Vessel v = vs[i];
                if (v == vessel || v.packed || (v.transform.position - p).sqrMagnitude > searchRange * searchRange) continue;
                for (int j = 0; j < v.parts.Count; j++)
                {
                    if (v.parts[j].Rigidbody == null) continue;
                    // every rail of the part (the Chopsticks part carries two, one per arm)
                    List<ModuleKAESACatchArm> arms = v.parts[j].FindModulesImplementing<ModuleKAESACatchArm>();
                    for (int k = 0; k < arms.Count; k++)
                    {
                        ModuleKAESACatchArm arm = arms[k];
                        if (arm.Rail == null) continue;
                        Vector3 l = arm.Rail.InverseTransformPoint(p);
                        if (Mathf.Abs(l.z) > arm.railLength * 0.5f + 0.2f) continue;
                        if (Mathf.Abs(l.x) > catchRadius) continue;
                        if (l.y < -catchBelow || l.y > catchAbove) continue;
                        Vector3 dv = part.Rigidbody.GetPointVelocity(p) - v.parts[j].Rigidbody.GetPointVelocity(p);
                        if (dv.magnitude > maxCatchSpeed) { status = "too fast (" + dv.magnitude.ToString("F1") + " m/s)"; continue; }
                        return arm;
                    }
                }
            }
            return null;
        }

        private void MakeJoint(Part arm)
        {
            armPart = arm; caughtArmId = arm.flightID;
            joint = part.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = arm.Rigidbody;
            joint.autoConfigureConnectedAnchor = true;                     // hold the pose it touched in
            joint.anchor = part.transform.InverseTransformPoint(pin.position);
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.breakForce = float.PositiveInfinity; joint.breakTorque = float.PositiveInfinity;   // checked by hand
            joint.enableCollision = false;
            IgnoreCollisions(arm);
            status = "caught";
            ScreenMessages.PostScreenMessage("Caught by the tower", 3f);
        }

        private void IgnoreCollisions(Part arm)
        {
            Collider[] mine = vessel.GetComponentsInChildren<Collider>();
            Collider[] theirs = arm.GetComponentsInChildren<Collider>();
            for (int a = 0; a < mine.Length; a++)
                for (int b = 0; b < theirs.Length; b++)
                {
                    if (mine[a] == null || theirs[b] == null || mine[a].isTrigger || theirs[b].isTrigger) continue;
                    Physics.IgnoreCollision(mine[a], theirs[b], true);
                    ignored.Add(new KeyValuePair<Collider, Collider>(mine[a], theirs[b]));
                }
        }

        private void RestoreCollisions()
        {
            for (int i = 0; i < ignored.Count; i++)
                if (ignored[i].Key != null && ignored[i].Value != null) Physics.IgnoreCollision(ignored[i].Key, ignored[i].Value, false);
            ignored.Clear();
        }

        private void DropJoint(bool forget)
        {
            if (joint != null) { Destroy(joint); joint = null; }
            armPart = null;
            if (forget) caughtArmId = 0;
        }

        [KSPEvent(guiActive = true, guiName = "Release from the tower", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void Release()
        {
            bool had = joint != null || caughtArmId != 0;
            DropJoint(true);
            armed = false;                                                 // so it does not grab the rail again at once
            restoreTimer = 2f;                                             // collisions back once it has moved off
            if (had) ScreenMessages.PostScreenMessage("Released from the tower", 3f);
        }

        // ------------------------------------------------------------------ the nearest tower's arms, from here
        private static float lastTowerCmd = -1f;         // both pin fins get the same action group: act once per press
        private void TowerArms(int cmd)                  // 1 close, 2 open, 3 toggle
        {
            if (Time.unscaledTime - lastTowerCmd < 0.25f) return;
            lastTowerCmd = Time.unscaledTime;
            float d;
            ModuleKAESAChopsticks c = ModuleKAESAChopsticks.Nearest(part.transform.position, towerRange, out d);
            if (c == null) { ScreenMessages.PostScreenMessage("No Gigazilla chopsticks within " + towerRange.ToString("F0") + " m", 3f); return; }
            if (cmd == 3) cmd = c.targetOpen > 0.5f ? 1 : 2;
            if (cmd == 1) c.CloseArms(); else c.OpenArms();
            ScreenMessages.PostScreenMessage((cmd == 1 ? "Closing" : "Opening") + " the tower arms (" + d.ToString("F0") + " m)", 3f);
        }

        [KSPEvent(guiActive = true, guiName = "Close tower arms", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void CloseTowerArms() { TowerArms(1); }
        [KSPEvent(guiActive = true, guiName = "Open tower arms", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void OpenTowerArms() { TowerArms(2); }

        [KSPAction("Tower arms: close")] public void ActTowerClose(KSPActionParam p) { TowerArms(1); }
        [KSPAction("Tower arms: open")] public void ActTowerOpen(KSPActionParam p) { TowerArms(2); }
        [KSPAction("Tower arms: open/close")] public void ActTowerToggle(KSPActionParam p) { TowerArms(3); }

        [KSPAction("Catch pins: arm")] public void ActArm(KSPActionParam p) { armed = true; }
        [KSPAction("Catch pins: release")] public void ActRelease(KSPActionParam p) { Release(); }

        public override string GetModuleDisplayName() { return "Catch Pin"; }

        public override string GetInfo()
        {
            if (!pinFitted) return "No catch pin on this fin.";
            return "Lets the Gigazilla's arms catch the booster: when armed, it locks onto an arm's rail it comes down on at under " +
                   maxCatchSpeed.ToString("F0") + " m/s. Holds up to " + breakForce.ToString("F0") + " kN.";
        }
    }
}
