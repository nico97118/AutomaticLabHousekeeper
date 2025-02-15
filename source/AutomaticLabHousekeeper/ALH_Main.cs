using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using KSP.UI.Screens;
using System.Collections;
using KSP.Localization;

namespace ALH
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class AutomaticLabHousekeeper : MonoBehaviour
    {
        private static double lastCheckTime = 0; // Manually track last check time

        void Awake()
        {
            // Destroy if ALH not enabled
            if (!ALHSettings.Instance.enableALH)
            {
                Debug.Log("[AutomaticLabHousekeeper] Disabled in settings.");
                Destroy(this);
                return;
            }

            // Destroy if not in Flight, Tracking Station, or Space Center
            if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION &&
                HighLogic.LoadedScene != GameScenes.SPACECENTER)
            {
                Destroy(this);
                return;
            }

            Debug.Log($"[AutomaticLabHousekeeper] Initialized in {HighLogic.LoadedScene}");

            // Register settings update event
            GameEvents.OnGameSettingsApplied.Add(OnSettingsChanged);
        }

        void OnDestroy()
        {
            // Unregister the event to prevent memory leaks
            GameEvents.OnGameSettingsApplied.Remove(OnSettingsChanged);
        }

        void OnSettingsChanged()
        {
            Debug.Log("[AutomaticLabHousekeeper] Settings changed, reloading...");

            // Handle ALH enable/disable in real-time
            if (!ALHSettings.Instance.enableALH)
            {
                Debug.Log("[AutomaticLabHousekeeper] Disabled via settings, stopping ALH.");
                StopAllCoroutines();
                return;
            }

            // Restart science processing with new settings
            StopAllCoroutines();
            StartCoroutine(DailyScienceCheck());
        }

        void Start()
        {
            StartCoroutine(DailyScienceCheck());
        }

        private IEnumerator DailyScienceCheck()
        {
            while (true)
            {
                yield return new WaitUntil(() => HasInGameTimePassed());
                ProcessScienceForAllVessels();
            }
        }

        bool HasInGameTimePassed()
        {
            double currentTime = Planetarium.GetUniversalTime();
            double dayLength = GameSettings.KERBIN_TIME ? 21600.0 : 86400.0;
            double interval = ALHSettings.Instance.checkInterval * dayLength; // Custom interval in settings

            if (currentTime - lastCheckTime >= interval)
            {
                lastCheckTime = currentTime; // Update last check time
                return true;
            }

            return false;
        }

        void ProcessScienceForAllVessels()
        {
            DebugLog("[AutomaticLabHousekeeper] Checking science for ALL vessels...");
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (HasScienceLab(v))
                {
                    DebugLog("[AutomaticLabHousekeeper] ============================================================");

                    if (v.loaded)
                        TransferScienceFromLab(v);
                    else
                    {
                        SimulateScienceProcessingForUnloadedLab(v);
                        TransferScienceFromUnloadedLab(v);
                    }
                }
            }
        }

        bool HasScienceLab(Vessel vessel)
        {
            if (vessel.loaded)
            {
                return vessel.Parts.Any(p => p.Modules.Contains("ModuleScienceLab"));
            }
            else
            {
                foreach (ProtoPartSnapshot protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    foreach (ProtoPartModuleSnapshot protoModule in protoPart.modules)
                    {
                        if (protoModule.moduleName == "ModuleScienceLab")
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        void TransferScienceFromLab(Vessel vessel)
        {
            Debug.Log($"[AutomaticLabHousekeeper] Processing LOADED vessel: {vessel.vesselName}");

            foreach (Part p in vessel.Parts)
            {
                var lab = p.FindModuleImplementing<ModuleScienceLab>();
                if (lab != null && lab.storedScience >= 1)
                {
                    float wholeScience = Mathf.Floor(lab.storedScience);
                    float remainingScience = lab.storedScience - wholeScience;

                    Debug.Log($"[AutomaticLabHousekeeper] Transferring {wholeScience} science from {vessel.vesselName} to R&D, keeping {remainingScience} science in the lab");

                    ResearchAndDevelopment.Instance.AddScience(wholeScience, TransactionReasons.ScienceTransmission);
                    lab.storedScience = remainingScience;

                    ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0003", wholeScience, vessel.vesselName), 10f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }

        void TransferScienceFromUnloadedLab(Vessel vessel)
        {
            Debug.Log($"[AutomaticLabHousekeeper] Processing UNLOADED vessel: {vessel.vesselName}");

            ProtoVessel protoVessel = vessel.protoVessel;
            foreach (ProtoPartSnapshot protoPart in protoVessel.protoPartSnapshots)
            {
                foreach (ProtoPartModuleSnapshot protoModule in protoPart.modules)
                {
                    if (protoModule.moduleName == "ModuleScienceLab")
                    {
                        float storedScience = float.Parse(protoModule.moduleValues.GetValue("storedScience"));

                        if (storedScience >= 1)
                        {
                            float wholeScience = Mathf.Floor(storedScience);
                            float remainingScience = storedScience - wholeScience;

                            Debug.Log($"[AutomaticLabHousekeeper] Moving {wholeScience} science from {vessel.vesselName} to R&D, keeping {remainingScience} science in the lab");

                            ResearchAndDevelopment.Instance.AddScience(wholeScience, TransactionReasons.ScienceTransmission);
                            protoModule.moduleValues.SetValue("storedScience", remainingScience.ToString("F2"));

                            ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_ALH_0003", wholeScience, vessel.vesselName), 10f, ScreenMessageStyle.UPPER_CENTER);
                        }
                        else
                        {
                            DebugLog("[AutomaticLabHousekeeper] Not enough storedScience");
                        }
                        DebugLog($"[AutomaticLabHousekeeper] Finished {vessel.vesselName}");
                    }
                }
            }
        }

        void SimulateScienceProcessingForUnloadedLab(Vessel vessel)
        {
            ProtoVessel protoVessel = vessel.protoVessel;

            Debug.Log($"[AutomaticLabHousekeeper] Simulating Science for Unloaded Lab in Vessel {vessel.vesselName}");

            foreach (ProtoPartSnapshot protoPart in protoVessel.protoPartSnapshots)
            {
                ProtoPartModuleSnapshot labModule = null;
                ProtoPartModuleSnapshot converterModule = null;

                foreach (ProtoPartModuleSnapshot protoModule in protoPart.modules)
                {
                    if (protoModule.moduleName == "ModuleScienceLab")
                        labModule = protoModule;
                    if (protoModule.moduleName == "ModuleScienceConverter")
                        converterModule = protoModule;
                }

                if (labModule != null && converterModule != null)
                {
                    // Ensure values exist
                    float dataStored = float.Parse(labModule.moduleValues.GetValue("dataStored"));
                    DebugLog($"[AutomaticLabHousekeeper] dataStored {dataStored}");
                    float storedScience = float.Parse(labModule.moduleValues.GetValue("storedScience"));
                    DebugLog($"[AutomaticLabHousekeeper] storedScience {storedScience}");
                    bool isActivated = bool.Parse(converterModule.moduleValues.GetValue("IsActivated"));
                    DebugLog($"[AutomaticLabHousekeeper] isActivated {isActivated}");

                    if (!isActivated || dataStored <= 0) return; // Lab is off or no data to process

                    // Read part config values
                    ConfigNode partConfig = PartLoader.getPartInfoByName(protoPart.partName).partConfig;

                    // Get the ModuleScienceConverter node inside partConfig
                    ConfigNode moduleNode = partConfig.GetNodes("MODULE").FirstOrDefault(n => n.GetValue("name") == "ModuleScienceConverter");

                    float scienceCap = float.Parse(moduleNode.GetValue("scienceCap"));
                    DebugLog($"[AutomaticLabHousekeeper] scienceCap {scienceCap}");
                    float dataProcessingMultiplier = float.Parse(moduleNode.GetValue("dataProcessingMultiplier"));
                    DebugLog($"[AutomaticLabHousekeeper] dataProcessingMultiplier {dataProcessingMultiplier}");
                    float scientistBonus = float.Parse(moduleNode.GetValue("scientistBonus"));
                    DebugLog($"[AutomaticLabHousekeeper] scientistBonus {scientistBonus}");
                    float researchTime = float.Parse(moduleNode.GetValue("researchTime"));
                    DebugLog($"[AutomaticLabHousekeeper] researchTime {researchTime}");
                    float scienceMultiplier = float.Parse(moduleNode.GetValue("scienceMultiplier"));
                    DebugLog($"[AutomaticLabHousekeeper] scienceMultiplier {scienceMultiplier}");

                    // Get time elapsed
                    double lastUpdateTime = double.Parse(converterModule.moduleValues.GetValue("lastUpdateTime"));
                    DebugLog($"[AutomaticLabHousekeeper] lastUpdateTime {lastUpdateTime}");
                    double currentTime = Planetarium.GetUniversalTime();
                    double timeElapsed = currentTime - lastUpdateTime;

                    // Calculate the scientist effect
                    float totalScientistLevel = CalculateScientistLevel(protoVessel);
                    DebugLog($"[AutomaticLabHousekeeper] totalScientistLevel {totalScientistLevel}");

                    // Compute science rate using KSP's actual formula
                    double secondsPerDay = GameSettings.KERBIN_TIME ? 21600.0 : 86400.0;
                    float scienceRatePerDay = (float)(secondsPerDay * (1 + scientistBonus * totalScientistLevel) * dataStored * dataProcessingMultiplier * scienceMultiplier) / Mathf.Pow(10f, researchTime);
                    DebugLog($"[AutomaticLabHousekeeper] scienceRatePerDay {scienceRatePerDay}");
                    float scienceRate = scienceRatePerDay / (float)secondsPerDay;
                    DebugLog($"[AutomaticLabHousekeeper] scienceRate {scienceRate}");

                    // Compute total science generated over elapsed time
                    float scienceGenerated = scienceRate * (float)timeElapsed;

                    // Ensure generated science doesn't exceed science cap
                    if (storedScience + scienceGenerated >= scienceCap)
                    {
                        scienceGenerated = scienceCap - storedScience;
                    }
                    DebugLog($"[AutomaticLabHousekeeper] scienceGenerated {scienceGenerated}");

                    // Convert dataStored into science at the correct rate
                    float dataUsed = scienceGenerated / scienceMultiplier;
                    DebugLog($"[AutomaticLabHousekeeper] dataUsed {dataUsed}");

                    // Ensure science storage does not exceed the cap
                    float newStoredScience = storedScience + scienceGenerated;
                    DebugLog($"[AutomaticLabHousekeeper] newStoredScience {newStoredScience}");

                    // Update remaining dataStored
                    float newDataStored = dataStored - dataUsed;

                    // Update ProtoModule values
                    labModule.moduleValues.SetValue("storedScience", newStoredScience.ToString("F2"));
                    labModule.moduleValues.SetValue("dataStored", newDataStored.ToString("F2"));
                    converterModule.moduleValues.SetValue("lastUpdateTime", currentTime.ToString());

                    Debug.Log($"[AutomaticLabHousekeeper] Simulated {scienceGenerated} science for {vessel.vesselName} (Data remaining: {newDataStored}, Stored Science: {newStoredScience}, Total Scientist Level: {totalScientistLevel})");
                }
            }
        }


        float CalculateScientistLevel(ProtoVessel protoVessel)
        {
            float totalScientistLevel = 0;

            foreach (ProtoPartSnapshot protoPart in protoVessel.protoPartSnapshots)
            {
                foreach (ProtoPartModuleSnapshot protoModule in protoPart.modules)
                {
                    if (protoModule.moduleName == "ModuleScienceLab") // Only count scientists in the lab part
                    {
                        foreach (ProtoCrewMember crew in protoPart.protoModuleCrew) // Only count crew inside this part
                        {
                            DebugLog($"[AutomaticLabHousekeeper] Detected scientist {crew} with stars {crew.experienceLevel}");

                            if (crew.experienceTrait.Title == "Scientist")
                            {
                                totalScientistLevel += crew.experienceLevel;
                            }
                        }
                    }
                }
            }

            return Mathf.Max(totalScientistLevel, 0.0f); // If no scientists, processing stops
        }

        void DebugLog(string message)
        {
            if (ALHSettings.Instance.enableDebug)
            {
                Debug.Log($"{message}");
            }
        }
    }
}
