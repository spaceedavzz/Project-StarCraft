// KAESA StarCraft - ModuleKAESAPezDispenser
// The PEZ rack (KAESA_StarCraft_PezRack) is a Breaking Ground piston whose carriage holds a stack of Globelinks hanging
// node to node. This module runs the Starlink-style deploy cycle:
//   1. lower the stack until its bottom Globelink sits in front of the PEZ slot ((count - 1) x pitch of extension; the
//      piston works like a stock one: extension 0 = carriage at the bottom, full extension = full stack)
//   2. release the bottom Globelink: its own ModuleDecouple (top node); the last one: the rack's carriage-node decoupler
//   3. the pusher (animation) slides it out through the slot: while the pusher moves, the Globelink gets at least
//      pushSpeed toward the slot relative to the ship (a pusher only pushes: nothing else about its motion is touched)
//   4. once it is clear of the hull, lower the next one into place and lock the piston again
// Needs the PEZ door open (ModuleAnimateGeneric "PezDoor" on the ship). Auto mode repeats every N seconds until empty.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Expansions.Serenity;

namespace KAESA.StarCraft
{
    public class ModuleKAESAPezDispenser : PartModule
    {
        [KSPField] public string carriageNode = "carriage";
        [KSPField] public string payloadTopNode = "top";
        [KSPField] public string payloadBottomNode = "bottom";
        [KSPField] public float pitch = 0.515f;
        [KSPField] public int capacity = 8;
        [KSPField] public float pushSpeed = 0.5f;
        [KSPField] public float stroke = 1.2f;
        [KSPField] public string pusherAnimation = "PezPusher";
        [KSPField] public string doorAnimation = "PezDoor";
        [KSPField] public float clearDistance = 3.6f;

        private const string G = "kaesaPez";
        private const string GN = "PEZ dispenser";

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Globelinks in the rack", groupName = G, groupDisplayName = GN)]
        public string stackText = "";
        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "State", groupName = G, groupDisplayName = GN)]
        public string stateText = "Idle";
        [KSPField(guiActive = false, guiActiveEditor = true, guiName = "Lined up with the slot", groupName = G, groupDisplayName = GN)]
        public string alignText = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Globelinks lined up", groupName = G, groupDisplayName = GN)]
        public string payloadAlignText = "";
        [KSPField(guiActive = false, guiActiveEditor = true, guiName = "PEZ door arrow", groupName = G, groupDisplayName = GN),
         UI_Toggle(disabledText = "Hidden", enabledText = "Shown")]
        public bool showArrow = true;
        [KSPField] public float misalignLimit = 5f;          // degrees a Globelink may be turned off the rack's axis
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Auto release", groupName = G, groupDisplayName = GN),
         UI_Toggle(disabledText = "Off", enabledText = "On")]
        public bool autoDeploy = false;
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Interval", guiUnits = " s", groupName = G, groupDisplayName = GN),
         UI_FloatRange(minValue = 5f, maxValue = 120f, stepIncrement = 1f)]
        public float autoInterval = 15f;
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Lock piston when idle", groupName = G, groupDisplayName = GN),
         UI_Toggle(disabledText = "No", enabledText = "Yes")]
        public bool lockWhenIdle = true;
        [KSPField(isPersistant = true)] public int deployedCount = 0;

        private enum Phase { Idle, Lower, Push, Clear, Wait }
        private Phase phase = Phase.Idle;
        private float phaseTime, lowerTarget, textTimer;
        private bool releaseAfterLower;
        private bool waitingDoor;                            // auto release armed while the PEZ door is shut: wait quietly
        private Part released;
        private ModuleRoboticServoPiston piston;
        private Animation pusher;
        private static MethodInfo miUpdateTarget;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            piston = part.FindModuleImplementing<ModuleRoboticServoPiston>();
            Animation[] a = part.FindModelAnimators(pusherAnimation);
            if (a != null && a.Length > 0)
            {
                pusher = a[0];
                AnimationState s = pusher[pusherAnimation];
                if (s != null) s.wrapMode = WrapMode.ClampForever;
            }
            if (miUpdateTarget == null)
                miUpdateTarget = typeof(ModuleRoboticServoPiston).GetMethod("UpdateTargetPosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            UpdateTexts();
        }

        // ------------------------------------------------------------------ part menu + action groups
        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Release next Globelink", groupName = G, groupDisplayName = GN)]
        public void EventRelease() { BeginCycle(); }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Stop auto release", groupName = G, groupDisplayName = GN)]
        public void EventStop()
        {
            autoDeploy = false; waitingDoor = false;
            if (phase == Phase.Wait) phase = Phase.Idle;
        }

        [KSPEvent(guiActive = false, guiActiveEditor = true, guiName = "Bring the bottom one to the slot", groupName = G, groupDisplayName = GN)]
        public void EventEditorLower()
        {
            int n = Stack().Count;
            if (n > 0) SetPistonTarget(Desired(n), false);
        }

        // piston lock, here in the PEZ group too (same as the Breaking Ground "Locked" toggle), VAB and flight
        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Piston: free (lock)", groupName = G, groupDisplayName = GN)]
        public void EventLockToggle() { SetLock(!IsLocked()); }

        [KSPAction("Lock piston")]
        public void ActionLock(KSPActionParam p) { SetLock(true); }

        [KSPAction("Unlock piston")]
        public void ActionUnlock(KSPActionParam p) { SetLock(false); }

        [KSPAction("Lock/unlock piston")]
        public void ActionLockToggle(KSPActionParam p) { SetLock(!IsLocked()); }

        private bool IsLocked() { return piston != null && piston.servoIsLocked; }

        private void SetLock(bool on)
        {
            if (piston == null) return;
            if (HighLogic.LoadedSceneIsFlight)
            {
                if (on && !piston.servoIsLocked) piston.EngageServoLock();
                else if (!on && piston.servoIsLocked) piston.DisengageServoLock();
            }
            else piston.servoIsLocked = on;
            UpdateTexts();
        }

        [KSPAction("Release next Globelink")]
        public void ActionRelease(KSPActionParam p) { BeginCycle(); }

        [KSPAction("Auto release: on/off")]
        public void ActionToggleAuto(KSPActionParam p) { autoDeploy = !autoDeploy; if (!autoDeploy) { waitingDoor = false; if (phase == Phase.Wait) phase = Phase.Idle; } }

        // ------------------------------------------------------------------ Globelink orientation (shared with ModuleKAESAPezSlot)
        // A Globelink's long side (its X) must run along the slot (the rack's X). Turned 180 deg is fine; 90 deg is not:
        // then it sticks through the columns and the carriage and the physics shoves the whole stack down into the tank.
        public static bool UpsideDown(Part g, Part rack)
        {
            return Vector3.Dot(g.transform.up, rack.transform.up) < 0.9f;
        }

        public static float MisalignDeg(Part g, Part rack)
        {
            Vector3 up = rack.transform.up;
            if (UpsideDown(g, rack)) return 180f;                               // hung by the wrong node / tipped
            Vector3 a = Vector3.ProjectOnPlane(g.transform.right, up);
            Vector3 b = Vector3.ProjectOnPlane(rack.transform.right, up);
            if (a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f) return 90f;    // tipped over
            float ang = Vector3.Angle(a, b);
            return Mathf.Min(ang, 180f - ang);
        }

        // the rack a Globelink hangs from (walks up the stack), or null
        public static ModuleKAESAPezDispenser FindRack(Part p)
        {
            Part q = p != null ? p.parent : null;
            for (int i = 0; i < 16 && q != null; i++, q = q.parent)
            {
                ModuleKAESAPezDispenser r = q.FindModuleImplementing<ModuleKAESAPezDispenser>();
                if (r != null) return r;
            }
            return null;
        }

        private readonly HashSet<Part> highlighted = new HashSet<Part>();

        private void UpdatePayloadAlign(List<Part> st)
        {
            int bad = 0;
            for (int i = 0; i < st.Count; i++)
            {
                bool off = MisalignDeg(st[i], part) > misalignLimit;
                if (off) bad++;
                if (!HighLogic.LoadedSceneIsEditor) continue;
                if (off && !highlighted.Contains(st[i]))
                {
                    st[i].SetHighlightType(Part.HighlightType.AlwaysOn);
                    st[i].SetHighlightColor(Color.red);
                    st[i].SetHighlight(true, false);
                    highlighted.Add(st[i]);
                }
                else if (!off && highlighted.Contains(st[i]))
                {
                    st[i].SetHighlightDefault();
                    highlighted.Remove(st[i]);
                }
            }
            if (HighLogic.LoadedSceneIsEditor)
            {
                List<Part> gone = new List<Part>();
                foreach (Part h in highlighted) if (h == null || !st.Contains(h)) gone.Add(h);
                foreach (Part h in gone) { if (h != null) h.SetHighlightDefault(); highlighted.Remove(h); }
            }
            payloadAlignText = st.Count == 0 ? "-" : bad == 0 ? "Yes (" + st.Count + ")" : bad + " turned or upside down";
        }

        // ------------------------------------------------------------------ VAB arrow: "this way is the PEZ door"
        private GameObject arrowGo;
        private LineRenderer arrow;

        private void UpdateArrow()
        {
            bool want = HighLogic.LoadedSceneIsEditor && showArrow;
            if (!want)
            {
                if (arrowGo != null) arrowGo.SetActive(false);
                return;
            }
            if (arrowGo == null)
            {
                arrowGo = new GameObject("KAESA_PezDoorArrow");
                arrow = arrowGo.AddComponent<LineRenderer>();
                Shader sh = Shader.Find("KSP/Particles/Alpha Blended") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                            Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (sh != null) arrow.material = new Material(sh);
                arrow.useWorldSpace = true;
                arrow.positionCount = 5;
                arrow.startWidth = arrow.endWidth = 0.09f;
                arrow.startColor = arrow.endColor = new Color(1f, 0.45f, 0.05f, 1f);
                if (arrow.material != null) arrow.material.color = new Color(1f, 0.45f, 0.05f, 1f);
                arrow.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                arrow.receiveShadows = false;
            }
            arrowGo.SetActive(true);
            Transform t = part.transform;
            Vector3 dir = -t.forward;                                   // toward the PEZ slot
            Vector3 a = t.position + t.up * 0.62f;                      // slot height above the rack's floor node
            Vector3 b = a + dir * 3.4f;                                 // out past the hull (r 2.5)
            Vector3 side = t.right * 0.35f, back = -dir * 0.45f;
            arrow.SetPosition(0, a);
            arrow.SetPosition(1, b);
            arrow.SetPosition(2, b + back + side);
            arrow.SetPosition(3, b);
            arrow.SetPosition(4, b + back - side);
        }

        public void OnDestroy()
        {
            RestoreAllGhosts();
            if (arrowGo != null) Destroy(arrowGo);
            foreach (Part h in highlighted) if (h != null) h.SetHighlightDefault();
            highlighted.Clear();
        }

        // ------------------------------------------------------------------ stack, piston, door
        public List<Part> Stack()
        {
            List<Part> list = new List<Part>();
            AttachNode n = part.FindAttachNode(carriageNode);
            Part p = n != null ? n.attachedPart : null;
            if (p == null || p.parent != part) return list;
            while (p != null && !list.Contains(p))
            {
                list.Add(p);
                AttachNode b = p.FindAttachNode(payloadBottomNode);
                Part next = b != null ? b.attachedPart : null;
                // a Globelink hung upside down (by its bottom node) has the next one on its top node: still follow the
                // chain, so the count stays right (the orientation check then refuses to release it)
                if ((next == null || next.parent != p) && p.children.Count == 1) next = p.children[0];
                if (next == null || next.parent != p) break;
                p = next;
            }
            return list;
        }

        // the piston works like a stock one: extension 0 = carriage at the bottom (one Globelink left, in front of the
        // slot), full extension = full stack. So n Globelinks need (n - 1) x pitch of extension.
        private float Desired(int count)
        {
            return Mathf.Clamp((count - 1) * pitch, 0f, (capacity - 1) * pitch);
        }

        private void SetPistonTarget(float e, bool unlock)
        {
            if (piston == null) return;
            if (unlock)
            {
                if (piston.servoIsLocked) piston.DisengageServoLock();
                if (!piston.servoMotorIsEngaged) piston.EngageMotor();
            }
            float old = piston.targetExtension;
            piston.targetExtension = e;
            if (miUpdateTarget != null) miUpdateTarget.Invoke(piston, new object[] { (object)old });
        }

        private bool PistonAt(float e)
        {
            return piston == null || Mathf.Abs(piston.currentExtension - e) < 0.006f;
        }

        private void LockIfWanted()
        {
            if (lockWhenIdle && piston != null && !piston.servoIsLocked) piston.EngageServoLock();
        }

        private ModuleAnimateGeneric FindDoor()
        {
            if (vessel == null) return null;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                List<ModuleAnimateGeneric> ms = vessel.parts[i].FindModulesImplementing<ModuleAnimateGeneric>();
                for (int j = 0; j < ms.Count; j++)
                    if (ms[j].animationName == doorAnimation) return ms[j];
            }
            return null;
        }

        private bool DoorOpen()
        {
            ModuleAnimateGeneric d = FindDoor();
            return d == null || d.animTime > 0.97f;
        }

        private static ModuleDecouple Decoupler(Part p, string node)
        {
            List<ModuleDecouple> ds = p.FindModulesImplementing<ModuleDecouple>();
            for (int i = 0; i < ds.Count; i++)
                if (ds[i].explosiveNodeID == node) return ds[i];
            return null;
        }

        private void PlayPusher(bool forward)
        {
            if (pusher == null) return;
            AnimationState s = pusher[pusherAnimation];
            if (s == null) return;
            s.wrapMode = WrapMode.ClampForever;
            s.speed = forward ? 1f : -2f;
            s.normalizedTime = forward ? 0f : 1f;
            pusher.Play(pusherAnimation);
        }

        private static void Msg(string s)
        {
            ScreenMessages.PostScreenMessage("PEZ: " + s, 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ------------------------------------------------------------------ the cycle
        private void BeginCycle()
        {
            if (!HighLogic.LoadedSceneIsFlight || (phase != Phase.Idle && phase != Phase.Wait)) return;
            List<Part> st0 = Stack();
            if (st0.Count == 0) { Msg("the rack is empty."); autoDeploy = false; phase = Phase.Idle; return; }
            for (int i = 0; i < st0.Count; i++)
            {
                float off = MisalignDeg(st0[i], part);
                if (off > misalignLimit)
                {
                    if (UpsideDown(st0[i], part))
                        Msg("Globelink " + (st0.Count - i) + " of " + st0.Count + " is upside down. In the VAB, attach it by the folded " +
                            "panels side (top node), panels and dishes up.");
                    else
                        Msg("Globelink " + (st0.Count - i) + " of " + st0.Count + " is turned " + off.ToString("0") +
                            "°. Its long side has to be parallel to the door: in the VAB, turn the stack 90°.");
                    autoDeploy = false; phase = Phase.Idle; return;
                }
            }
            if (!DoorOpen())
            {
                // a manual release says so; auto release (often armed in the VAB, so the door stays shut all through the
                // launch) waits without a message every interval and starts as soon as the door opens (tester report:
                // the message repeated all through the launch)
                if (!autoDeploy) { Msg("open the PEZ door before releasing."); phase = Phase.Idle; return; }
                waitingDoor = true; phase = Phase.Wait; phaseTime = 0f; return;
            }
            waitingDoor = false;
            StartLower(true);
        }

        private void StartLower(bool thenRelease)
        {
            int n = Stack().Count;
            if (n == 0)
            {
                Msg("all the Globelinks are out.");
                autoDeploy = false; LockIfWanted(); phase = Phase.Idle; return;
            }
            lowerTarget = Desired(n); releaseAfterLower = thenRelease;
            if (PistonAt(lowerTarget)) { AfterLower(); return; }
            SetPistonTarget(lowerTarget, true);
            phase = Phase.Lower; phaseTime = 0f;
        }

        private void AfterLower()
        {
            LockIfWanted();
            if (releaseAfterLower) { Release(); return; }
            if (autoDeploy && Stack().Count > 0) { phase = Phase.Wait; phaseTime = 0f; }
            else phase = Phase.Idle;
        }

        private void Release()
        {
            List<Part> st = Stack();
            if (st.Count == 0) { phase = Phase.Idle; return; }
            Part bottom = st[st.Count - 1];
            ModuleDecouple dec = st.Count >= 2 ? Decoupler(bottom, payloadTopNode) : Decoupler(part, carriageNode);
            if (dec == null) dec = Decoupler(bottom, payloadTopNode);
            Vector3 w = part.Rigidbody != null ? part.Rigidbody.angularVelocity : Vector3.zero;
            if (dec != null && !dec.isDecoupled) dec.Decouple();
            else bottom.decouple(0f);
            released = bottom;
            if (released.Rigidbody != null) released.Rigidbody.angularVelocity = w;
            deployedCount++;
            if (released.vessel != null && released.vessel != vessel)
                released.vessel.vesselName = "Globelink " + deployedCount + " - " + vessel.vesselName;
            if (released.vessel != null && released.vessel != vessel) IgnoreShip(released);
            PlayPusher(true);
            phase = Phase.Push; phaseTime = 0f;
            Msg("Globelink " + deployedCount + " released, going out through the slot.");
        }

        // The slot leaves 1.45 cm each side: the smallest turn of the ship (SAS holding attitude) touched the edges and
        // friction stopped the Globelink half way out (2026-11-17 test). The user's choice: like a real dispenser's guide
        // rails, from release until it is clear of the hull the Globelink keeps at least pushSpeed toward the slot, turns
        // with the ship, and does not collide with the ship; its collisions come back once it is 7 m from the hull.
        // Each released Globelink is a "ghost" to the ship until it is collisionRestoreDistance from the hull (the user
        // asked for 7 m): only then do its collisions with the ship come back. One entry per Globelink, so in auto mode
        // the next release never switches the previous one's collisions back on early.
        [KSPField] public float collisionRestoreDistance = 7f;
        [KSPField] public float hullRadius = 2.5f;

        private class Ghost
        {
            public Part p;
            public float t;
            public readonly List<Collider> a = new List<Collider>(), b = new List<Collider>();
        }
        private readonly List<Ghost> ghosts = new List<Ghost>();

        private void IgnoreShip(Part p)
        {
            if (p == null || vessel == null) return;
            Ghost g = new Ghost(); g.p = p;
            List<Collider> mine = p.FindModelComponents<Collider>();
            for (int k = 0; k < vessel.parts.Count; k++)
            {
                List<Collider> theirs = vessel.parts[k].FindModelComponents<Collider>();
                for (int i = 0; i < mine.Count; i++)
                    for (int j = 0; j < theirs.Count; j++)
                    {
                        if (mine[i] == null || theirs[j] == null) continue;
                        Physics.IgnoreCollision(mine[i], theirs[j], true);
                        g.a.Add(mine[i]); g.b.Add(theirs[j]);
                    }
            }
            ghosts.Add(g);
        }

        private static void SetIgnore(Ghost g, bool ignore)
        {
            for (int i = 0; i < g.a.Count; i++)
                if (g.a[i] != null && g.b[i] != null) Physics.IgnoreCollision(g.a[i], g.b[i], ignore);
        }

        // KSP rebuilds its collision ignores when the vessel splits (right after the decouple), which can undo ours:
        // re-assert them every physics frame; give the collisions back once the Globelink is far enough from the hull
        private void UpdateGhosts(float dt)
        {
            Vector3 bay = part.transform.position + part.transform.up * 0.6f;      // PEZ slot height on the bay axis
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                Ghost g = ghosts[i]; g.t += dt;
                bool gone = g.p == null || g.p.vessel == null;
                bool far = gone || g.p.vessel == vessel ||
                           Vector3.Distance(g.p.transform.position, bay) - hullRadius > collisionRestoreDistance;
                if (far || g.t > 300f)
                {
                    if (!gone) SetIgnore(g, false);
                    ghosts.RemoveAt(i);
                }
                else SetIgnore(g, true);
            }
        }

        private void RestoreAllGhosts()
        {
            for (int i = 0; i < ghosts.Count; i++) SetIgnore(ghosts[i], false);
            ghosts.Clear();
        }

        private void PushReleased(bool ramp)
        {
            if (released == null || released.vessel == null || released.vessel == vessel) return;
            Rigidbody shipRb = part.Rigidbody != null ? part.Rigidbody : (vessel.rootPart != null ? vessel.rootPart.Rigidbody : null);
            if (shipRb == null) return;
            Vector3 dir = -part.transform.forward;                     // the rack's tracks point at the slot (model -Y)
            Vector3 shipV = shipRb.GetPointVelocity(released.transform.position);
            float target = pushSpeed * (ramp ? Mathf.Clamp01(phaseTime / 0.4f) : 1f);
            List<Part> ps = released.vessel.parts;
            for (int i = 0; i < ps.Count; i++)
            {
                Rigidbody rb = ps[i].Rigidbody;
                if (rb == null) continue;
                // on the rails: relative to the ship it only moves straight toward the slot (no drift up, down or
                // sideways into its neighbours), at least at the pusher's speed, and it turns with the ship
                float along = Mathf.Max(Vector3.Dot(rb.velocity - shipV, dir), target);
                rb.velocity = shipV + dir * along;
                rb.angularVelocity = shipRb.angularVelocity;
            }
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || vessel.packed) return;
            phaseTime += TimeWarp.fixedDeltaTime;
            UpdateGhosts(TimeWarp.fixedDeltaTime);                     // released Globelinks: no collision until 7 m out
            switch (phase)
            {
                case Phase.Idle:
                    if (autoDeploy) BeginCycle();
                    break;
                case Phase.Wait:
                    if (!autoDeploy) { phase = Phase.Idle; waitingDoor = false; break; }
                    if (waitingDoor) { if (DoorOpen()) { phase = Phase.Idle; BeginCycle(); } break; }
                    if (phaseTime >= autoInterval) { phase = Phase.Idle; BeginCycle(); }
                    break;
                case Phase.Lower:
                    if (piston != null && piston.servoIsLocked && phaseTime > 0.3f)
                    {
                        Msg("piston locked half way down; unlock it to go on.");
                        autoDeploy = false; phase = Phase.Idle;
                    }
                    else if (PistonAt(lowerTarget) && phaseTime > 0.5f) AfterLower();
                    else if (phaseTime > 20f && piston != null && Mathf.Abs(piston.currentExtension - lowerTarget) < 0.02f) AfterLower();
                    else if (phaseTime > 60f)
                    {
                        Msg("the piston did not reach its position (it is at " + (piston != null ? piston.currentExtension.ToString("0.000") : "?") +
                            " m, alvo " + lowerTarget.ToString("0.000") + " m).");
                        Debug.LogWarning("[KAESA] PEZ rack: piston stuck at " + (piston != null ? piston.currentExtension.ToString("0.0000") : "?") +
                                         " m, target " + lowerTarget.ToString("0.0000") + " m, locked " + (piston != null && piston.servoIsLocked));
                        autoDeploy = false; phase = Phase.Idle;
                    }
                    break;
                case Phase.Push:
                    PushReleased(true);
                    if (phaseTime >= stroke / pushSpeed) { PlayPusher(false); phase = Phase.Clear; phaseTime = 0f; }
                    break;
                case Phase.Clear:
                    bool gone = released == null || released.vessel == null || released.vessel == vessel ||
                        Vector3.Dot(released.transform.position - part.transform.position, -part.transform.forward) > clearDistance;
                    if (gone) { released = null; StartLower(false); }    // out of the slot: the rails let go
                    else if (phaseTime > 25f)
                    {
                        Msg("the Globelink did not leave the slot; dispenser stopped.");
                        released = null; autoDeploy = false; phase = Phase.Idle;
                    }
                    else PushReleased(false);                            // guide rails: keep it going until it is out
                    break;
            }
        }

        // ------------------------------------------------------------------ part menu texts
        private float editorAge;
        private bool editorRaised;

        public void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight && !HighLogic.LoadedSceneIsEditor) return;
            // a fresh rack in the VAB: raise the carriage to the top once, so the 8 Globelinks can be hung from it
            if (HighLogic.LoadedSceneIsEditor && !editorRaised)
            {
                editorAge += Time.deltaTime;
                if (editorAge > 1f)
                {
                    editorRaised = true;
                    try
                    {
                        float top = (capacity - 1) * pitch;
                        if (piston != null && Stack().Count == 0 && piston.targetExtension < top - 0.01f) SetPistonTarget(top, false);
                    }
                    catch (Exception e) { Debug.LogWarning("[KAESA] PEZ rack: could not raise the carriage in the editor: " + e.Message); }
                }
            }
            UpdateArrow();                                   // every frame, so it follows the rack while it is dragged
            textTimer -= Time.deltaTime;
            if (textTimer > 0f) return;
            textTimer = 0.25f;
            UpdateTexts();
        }

        private void UpdateTexts()
        {
            List<Part> st = Stack();
            stackText = st.Count + " / " + capacity;
            UpdatePayloadAlign(st);
            UpdateArrow();
            switch (phase)
            {
                case Phase.Idle: stateText = "Idle"; break;
                case Phase.Lower: stateText = "Lowering the stack"; break;
                case Phase.Push: stateText = "Pushing"; break;
                case Phase.Clear: stateText = "Going out through the slot"; break;
                case Phase.Wait: stateText = waitingDoor ? "Waiting for the PEZ door" : "Next in " + Mathf.CeilToInt(Mathf.Max(0f, autoInterval - phaseTime)) + " s"; break;
            }
            if (HighLogic.LoadedSceneIsEditor) alignText = AlignText();
            BaseEvent le = Events["EventLockToggle"];
            if (le != null) le.guiName = IsLocked() ? "Piston: locked (unlock)" : "Piston: free (lock)";
        }

        private string AlignText()
        {
            Part p = part.parent;
            if (p == null) return "-";
            bool hasDoor = false;
            List<ModuleAnimateGeneric> ms = p.FindModulesImplementing<ModuleAnimateGeneric>();
            for (int i = 0; i < ms.Count; i++) if (ms[i].animationName == doorAnimation) hasDoor = true;
            if (!hasDoor) return "no PEZ door";
            Vector3 up = part.transform.up;
            float a = Vector3.Angle(Vector3.ProjectOnPlane(part.transform.forward, up), Vector3.ProjectOnPlane(p.transform.forward, up));
            return a < 3f ? "Yes" : "No (turn " + a.ToString("0") + "°)";
        }

        public override string GetInfo()
        {
            return "Globelink rack, capacity " + capacity + ". Lowers the stack, releases the bottom Globelink and pushes it out " +
                   "through the PEZ slot at " + pushSpeed.ToString("0.0") + " m/s. Needs the PEZ door open.";
        }
    }
}
