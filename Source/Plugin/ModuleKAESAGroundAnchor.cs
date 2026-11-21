// KAESA StarCraft - ModuleKAESAGroundAnchor
// Holds a part (the Gigazilla tower) fixed to the ground, written for StarCraft v2 (2026-11-20). Same idea as the stock
// launch clamp (a ConfigurableJoint with every motion locked and no connected body, so it is anchored to the world; made
// when the vessel goes off rails, removed when it is packed), plus: the anchor point is kept as latitude / longitude /
// altitude and re-applied every physics frame, so floating origin shifts and the rotating frame do not drag the tower.
// The tower is launched attached to the booster (radial decoupler); after staging it stays where it stood.
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAGroundAnchor : PartModule
    {
        private const string GRP = "kaesaAnchor";
        private const string GRP_NAME = "Ground anchor";

        [KSPField(isPersistant = true, guiActive = true, guiName = "Anchored", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_Toggle(enabledText = "Yes", disabledText = "No", scene = UI_Scene.Flight)]
        public bool anchored = true;

        // where the part was anchored (body-fixed), saved so a reload keeps the tower on the same spot
        [KSPField(isPersistant = true)] public bool havePoint = false;
        [KSPField(isPersistant = true)] public double anchorLat = 0, anchorLon = 0, anchorAlt = 0;

        [KSPField(guiActive = true, guiName = "Anchor", groupName = GRP, groupDisplayName = GRP_NAME)]
        public string status = "-";

        private ConfigurableJoint joint;

        public override void OnStart(StartState state)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            GameEvents.onVesselGoOnRails.Add(OnRails);
            GameEvents.onVesselGoOffRails.Add(OffRails);
        }

        public void OnDestroy()
        {
            GameEvents.onVesselGoOnRails.Remove(OnRails);
            GameEvents.onVesselGoOffRails.Remove(OffRails);
            Drop();
        }

        private void OnRails(Vessel v) { if (v == vessel) Drop(); }
        private void OffRails(Vessel v) { if (v == vessel) { /* the joint is made in FixedUpdate, once physics is running */ } }

        private bool OnGround()
        {
            return vessel != null && (vessel.situation == Vessel.Situations.PRELAUNCH || vessel.situation == Vessel.Situations.LANDED
                                      || vessel.situation == Vessel.Situations.SPLASHED);
        }

        private Vector3d AnchorWorld()
        {
            return vessel.mainBody.GetWorldSurfacePosition(anchorLat, anchorLon, anchorAlt);
        }

        private void Make()
        {
            if (part.Rigidbody == null) return;
            if (!havePoint)
            {
                Vector3d p = part.transform.position;
                anchorLat = vessel.mainBody.GetLatitude(p); anchorLon = vessel.mainBody.GetLongitude(p); anchorAlt = vessel.mainBody.GetAltitude(p);
                havePoint = true;
            }
            joint = part.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = null;                                    // anchored to the world
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero;
            joint.connectedAnchor = AnchorWorld();
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.01f; joint.projectionAngle = 0.1f;
            joint.breakForce = float.PositiveInfinity; joint.breakTorque = float.PositiveInfinity;
        }

        private void Drop()
        {
            if (joint != null) { Destroy(joint); joint = null; }
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return;
            if (vessel.packed || !anchored) { Drop(); status = anchored ? "on rails" : "free"; return; }
            if (joint == null)
            {
                if (!OnGround() && !havePoint) { status = "not on the ground"; return; }
                Make();
            }
            if (joint != null)
            {
                joint.connectedAnchor = AnchorWorld();                     // follows floating origin shifts
                status = "holding";
            }
        }

        [KSPEvent(guiActive = true, guiName = "Anchor here", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void ReAnchor()
        {
            Drop(); havePoint = false; anchored = true;
        }

        public override string GetModuleDisplayName() { return "Ground Anchor"; }

        public override string GetInfo()
        {
            return "Holds the tower to the ground where it stands (like a launch clamp that never lets go). " +
                   "Launch it attached to the booster; staging separates it and it stays planted.";
        }
    }
}
