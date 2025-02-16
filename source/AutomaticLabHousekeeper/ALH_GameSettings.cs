using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KSP;
using KSP.Localization;

namespace ALH
{
    public class ALHSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "#autoLOC_ALH_0001"; // Section title in settings
        public override string DisplaySection => "#autoLOC_ALH_0001"; // Name in the settings menu
        public override string Section => "#autoLOC_ALH_0001"; // Internal category
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY; // Available in all game modes
        public override bool HasPresets => false; // No presets
        public override int SectionOrder => 1; // Determines the position in the settings menu

        [GameParameters.CustomParameterUI("#autoLOC_ALH_0100", toolTip = "#autoLOC_ALH_0101")]
        public bool enableALH = true; // Toggle ALH on/off

        [GameParameters.CustomParameterUI("#autoLOC_ALH_0102", toolTip = "#autoLOC_ALH_0103")]
        public bool enableDebug = false; // Toggle debug mode

        [GameParameters.CustomFloatParameterUI("#autoLOC_ALH_0104", minValue = 1f, maxValue = 50f, stepCount = 100, toolTip = "#autoLOC_ALH_0105")]
        public float checkInterval = 1.0f; // Days between science checks

        public static ALHSettings Instance
        {
            get
            {
                if (HighLogic.CurrentGame == null) return null; // Prevents crashes when game is loading
                return HighLogic.CurrentGame.Parameters.CustomParams<ALHSettings>();
            }
        }
    }
}
