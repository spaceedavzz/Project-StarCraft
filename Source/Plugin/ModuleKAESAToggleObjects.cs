// KAESA StarCraft - ModuleKAESAToggleObjects
// A part-menu button (and action groups) that shows / hides some model transforms, kept in the save.
// Used for the Geolink's laser links: the blue beams out of both ends ("Laser: on" / "Laser: off").
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAToggleObjects : PartModule
    {
        [KSPField] public string objectNames = "";          // comma-separated transform names
        [KSPField] public string onGuiName = "Laser: on";
        [KSPField] public string offGuiName = "Laser: off";
        [KSPField] public string groupName = "kaesaLaser";
        [KSPField] public string groupDisplayName = "Laser";

        [KSPField(isPersistant = true)] public bool isOn = false;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            BaseEvent e = Events["EventToggle"];
            if (e != null)
            {
                e.group = new BasePAWGroup(groupName, groupDisplayName, false);
            }
            Apply();
        }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Laser: on")]
        public void EventToggle()
        {
            isOn = !isOn;
            Apply();
        }

        [KSPAction("Laser: on/off")]
        public void ActionToggle(KSPActionParam p) { isOn = !isOn; Apply(); }

        [KSPAction("Laser: on")]
        public void ActionOn(KSPActionParam p) { isOn = true; Apply(); }

        [KSPAction("Laser: off")]
        public void ActionOff(KSPActionParam p) { isOn = false; Apply(); }

        private void Apply()
        {
            Events["EventToggle"].guiName = isOn ? offGuiName : onGuiName;
            if (string.IsNullOrEmpty(objectNames) || part == null) return;
            foreach (string raw in objectNames.Split(','))
            {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                Transform[] ts = part.FindModelTransforms(n);
                for (int i = 0; i < ts.Length; i++)
                {
                    // models keep these shrunk to nothing (so they never count for icons / drag cubes); full size when on
                    if (isOn) ts[i].localScale = Vector3.one;
                    if (ts[i].gameObject.activeSelf != isOn) ts[i].gameObject.SetActive(isOn);
                }
            }
        }

        public override string GetInfo()
        {
            return "Laser links: turn the beams on or off from the part menu or an action group.";
        }
    }
}
