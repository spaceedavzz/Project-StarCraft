// KAESA StarCraft - ModuleKAESAPezSlot
// On each Globelink: its place in the PEZ rack stack ("1 of 8" = the bottom one, the first to go out) and whether it is
// turned the right way (long side parallel to the PEZ door). Read-only, part menu in the VAB and in flight.
using System.Collections.Generic;
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAPezSlot : PartModule
    {
        private const string G = "kaesaPezSlot";
        private const string GN = "PEZ";

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Place in the PEZ", groupName = G, groupDisplayName = GN)]
        public string slotText = "-";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Lined up with the door", groupName = G, groupDisplayName = GN)]
        public string alignText = "-";

        private float timer;

        public void Update()
        {
            if (!HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight) return;
            timer -= Time.deltaTime;
            if (timer > 0f) return;
            timer = 0.3f;
            ModuleKAESAPezDispenser rack = ModuleKAESAPezDispenser.FindRack(part);
            if (rack == null) { slotText = "not in a rack"; alignText = "-"; return; }
            List<Part> st = rack.Stack();
            int i = st.IndexOf(part);
            if (i < 0) { slotText = "not in a rack"; alignText = "-"; return; }
            int n = st.Count - i;                                  // 1 = bottom of the stack = goes out first
            slotText = n + " of " + st.Count + (n == 1 ? " (goes out first)" : n == st.Count ? " (goes out last)" : "");
            float off = ModuleKAESAPezDispenser.MisalignDeg(part, rack.part);
            alignText = off <= rack.misalignLimit ? "Yes" :
                        ModuleKAESAPezDispenser.UpsideDown(part, rack.part) ? "NO, upside down" : "NO, turned " + off.ToString("0") + "°";
        }

        public override string GetInfo()
        {
            return "Shows this Globelink's place in the PEZ rack stack and whether it faces the PEZ door the right way.";
        }
    }
}
