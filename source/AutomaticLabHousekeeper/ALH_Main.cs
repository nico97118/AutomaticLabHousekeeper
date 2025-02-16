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
            StartCoroutine(WaitForSettings());
        }

        private IEnumerator WaitForSettings()
        {
            while (HighLogic.CurrentGame == null || ALHSettings.Instance == null)
            {
                yield return null; // Wait until settings are available
            }

            Debug.Log("[AutomaticLabHousekeeper] Settings loaded, proceeding with initialization.");

            // Destroy if ALH is disabled in settings
            if (!ALHSettings.Instance.enableALH)
            {
                Debug.Log("[AutomaticLabHousekeeper] Disabled in settings.");
                Destroy(this);
                yield break;
            }

            // Destroy if not in Flight, Tracking Station, or Space Center
            if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                HighLogic.LoadedScene != GameScenes.TRACKSTATION &&
                HighLogic.LoadedScene != GameScenes.SPACECENTER)
            {
                Destroy(this);
                yield break;
            }

            Debug.Log($"[AutomaticLabHousekeeper] Initialized in {HighLogic.LoadedScene}");

            // Register settings update event
            GameEvents.OnGameSettingsApplied.Add(OnSettingsChanged);

            StartCoroutine(DailyScienceCheck());
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
            foreach (Vessel v in FlightGlobals.Vessels.Where(v => HasScienceLab(v)))
            {
                DebugLog("[AutomaticLabHousekeeper] ============================================================");

                // Check if ALH is on this vessel
                if (!HasALHModule(v))
                {
                    DebugLog($"[AutomaticLabHousekeeper] Skipping {v.vesselName}, no ALH module found.");
                    continue;
                }

                // Retrieve settings
                bool transmissionEnabled = GetTransmissionAutomationStatus(v);
                bool dataPullingEnabled = GetDataPullingStatus(v);

                if (!transmissionEnabled)
                {
                    DebugLog($"[AutomaticLabHousekeeper] Skipping {v.vesselName}, transmission automation is disabled.");
                    continue;
                }

                if (v.loaded)
                    TransferScienceFromLab(v);
                else
                {
                    SimulateScienceProcessingForUnloadedLab(v);
                    TransferScienceFromUnloadedLab(v);
                }

                if (dataPullingEnabled)
                {
                    PullDataIntoLab(v); // Attempt to pull data after processing science
                }

                DebugLog($"[AutomaticLabHousekeeper] Finished {v.vesselName}");
            }
        }

        bool HasScienceLab(Vessel vessel)
        {
            return vessel.loaded
                ? vessel.Parts.Any(p => p.FindModuleImplementing<ModuleScienceLab>() != null)
                : vessel.protoVessel.protoPartSnapshots.Any(protoPart =>
                    protoPart.modules.Any(protoModule => protoModule.moduleName == "ModuleScienceLab"));
        }

        bool HasALHModule(Vessel vessel)
        {
            return vessel.loaded
                ? vessel.Parts.Any(p => p.FindModuleImplementing<Module_AutomaticLabHousekeeper>() != null)
                : vessel.protoVessel.protoPartSnapshots.Any(protoPart =>
                    protoPart.modules.Any(protoModule => protoModule.moduleName == "Module_AutomaticLabHousekeeper"));
        }

        bool GetTransmissionAutomationStatus(Vessel vessel)
        {
            if (vessel.loaded)
            {
                var alhModule = vessel.Parts
                    .Select(p => p.FindModuleImplementing<Module_AutomaticLabHousekeeper>())
                    .FirstOrDefault(m => m != null);

                return alhModule != null && alhModule.transmissionAutomationEnabled;
            }
            else
            {
                foreach (ProtoPartSnapshot protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    foreach (ProtoPartModuleSnapshot protoModule in protoPart.modules)
                    {
                        if (protoModule.moduleName == "Module_AutomaticLabHousekeeper")
                        {
                            return bool.Parse(protoModule.moduleValues.GetValue("transmissionAutomationEnabled"));
                        }
                    }
                }
            }
            return false; // Default to disabled if module is missing
        }

        bool GetDataPullingStatus(Vessel vessel)
        {
            if (vessel.loaded)
            {
                var alhModule = vessel.Parts
                    .Select(p => p.FindModuleImplementing<Module_AutomaticLabHousekeeper>())
                    .FirstOrDefault(m => m != null);

                return alhModule != null && alhModule.dataPullingEnabled;
            }
            else
            {
                foreach (ProtoPartSnapshot protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    foreach (ProtoPartModuleSnapshot protoModule in protoPart.modules)
                    {
                        if (protoModule.moduleName == "Module_AutomaticLabHousekeeper")
                        {
                            return bool.Parse(protoModule.moduleValues.GetValue("dataPullingEnabled"));
                        }
                    }
                }
            }
            return false; // Default to disabled if module is missing
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

        void PullDataIntoLab(Vessel vessel)
        {
            DebugLog($"[AutomaticLabHousekeeper] Attempting data pull for {vessel.vesselName}...");

            if (vessel.loaded)
            {
                // Process loaded vessel
                foreach (Part p in vessel.Parts)
                {
                    var alhModule = p.FindModuleImplementing<Module_AutomaticLabHousekeeper>();
                    if (alhModule == null || !alhModule.dataPullingEnabled || alhModule.selectedExperimentStorageUnit == "None")
                    {
                        DebugLog($"[AutomaticLabHousekeeper] Data pulling disabled or no storage unit selected for part {p.partName}");
                        continue; // Skip if data pulling is disabled or no storage unit is selected
                    }
                    
                    // Find the selected storage unit
                    Part storagePart = vessel.Parts.FirstOrDefault(part => part.persistentId.ToString() == alhModule.selectedExperimentStorageUnit);
                    if (storagePart == null)
                    {
                        DebugLog($"[AutomaticLabHousekeeper] No valid storage unit found for {p.partInfo.title}, skipping.");
                        continue;
                    }
                    
                    ModuleScienceContainer storageContainer = storagePart.FindModuleImplementing<ModuleScienceContainer>();
                    ModuleScienceLab lab = p.FindModuleImplementing<ModuleScienceLab>();
                    
                    if (storageContainer == null || lab == null)
                    {
                        DebugLog($"[AutomaticLabHousekeeper] Missing storage container or lab on {p.partInfo.title}");
                        continue;
                    }
                    
                    List<ScienceData> storedExperiments = storageContainer.GetData().ToList();
                    float availableDataSpace = lab.dataStorage - lab.dataStored; // Remaining capacity in lab
                    
                    if (storedExperiments.Count == 0 || availableDataSpace <= 0)
                    {
                        DebugLog("[AutomaticLabHousekeeper] No experiments available or lab is full.");
                        continue;
                    }
                    
                    foreach (ScienceData experiment in storedExperiments)
                    {
                        float processedData = CalculateProcessedData(experiment, p);
                        
                        if (processedData <= availableDataSpace)
                        {
                            lab.dataStored += processedData;
                            availableDataSpace -= processedData;
                            storageContainer.DumpData(experiment); // Delete the experiment from storage

                            DebugLog($"[AutomaticLabHousekeeper] Transferred {processedData} data from {storagePart.partInfo.title} to lab.");
                        }
                        else
                        {
                            DebugLog($"[AutomaticLabHousekeeper] Not enough space in lab {p.partName} to store experiment {experiment.subjectID}. Current stored Data: {lab.dataStored}");
                        }
                    }
                }
            }
            else
            {
                // Process unloaded vessel
                ProtoVessel protoVessel = vessel.protoVessel;

                foreach (ProtoPartSnapshot protoPart in protoVessel.protoPartSnapshots)
                {
                    ProtoPartModuleSnapshot alhModule = protoPart.modules.FirstOrDefault(m => m.moduleName == "Module_AutomaticLabHousekeeper");
                    
                    if (alhModule == null || alhModule.moduleValues.GetValue("dataPullingEnabled") != "True")
                    {
                        DebugLog($"[AutomaticLabHousekeeper] Data pulling disabled or no storage unit selected or no ALH module for part {protoPart.partName}");
                        continue;
                    }
                    
                    string selectedStorageUnit = alhModule.moduleValues.GetValue("selectedExperimentStorageUnit");
                    if (selectedStorageUnit == "None")
                    {
                        DebugLog("[AutomaticLabHousekeeper] Not able to pull data, Experiment Storage Unit None");
                        continue;
                    }
                    
                    ProtoPartSnapshot storagePart = protoVessel.protoPartSnapshots.FirstOrDefault(part => part.persistentId.ToString() == selectedStorageUnit);
                    if (storagePart == null)
                    {
                        DebugLog("[AutomaticLabHousekeeper] Not able to pull data, Storage Part faulty");
                        continue;
                    }
                    
                    ProtoPartModuleSnapshot storageContainer = storagePart.modules.FirstOrDefault(m => m.moduleName == "ModuleScienceContainer");
                    ProtoPartModuleSnapshot labModule = protoPart.modules.FirstOrDefault(m => m.moduleName == "ModuleScienceLab");
                    
                    if (storageContainer == null || labModule == null)
                    {
                        DebugLog("[AutomaticLabHousekeeper] Missing storage container or lab on protoPart");
                        continue;
                    }
                    
                    // Get stored Experiments
                    ConfigNode[] storedExperiments = storageContainer.moduleValues.GetNodes("ScienceData");
                    
                    if (storedExperiments.Length == 0)
                    {
                        DebugLog($"[AutomaticLabHousekeeper] No stored experiments in {storagePart.partName}");
                        continue;
                    }
                    
                    // Retrieve dataStorage from the part config if not found in the module values
                    ConfigNode partConfig = PartLoader.getPartInfoByName(protoPart.partName).partConfig;

                    // Get the ModuleScienceLab node inside partConfig
                    ConfigNode labModuleConfig = partConfig.GetNodes("MODULE").FirstOrDefault(n => n.GetValue("name") == "ModuleScienceLab");

                    if (labModuleConfig == null)
                    {
                        Debug.LogError($"[AutomaticLabHousekeeper] ERROR: Could not find ModuleScienceLab config for {protoPart.partName}");
                        continue;
                    }

                    // Retrieve dataStorage from part config
                    float dataStorage = float.Parse(labModuleConfig.GetValue("dataStorage"));
                    DebugLog($"[AutomaticLabHousekeeper] Found dataStorage = {dataStorage} for {protoPart.partName}");

                    // Get the current dataStored from protoModule
                    string dataStoredStr = labModule.moduleValues.GetValue("dataStored");
                    float dataStored = string.IsNullOrEmpty(dataStoredStr) ? 0f : float.Parse(dataStoredStr);

                    float availableDataSpace = dataStorage - dataStored;

                    
                    if (availableDataSpace <= 0)
                    {
                        DebugLog($"[AutomaticLabHousekeeper] Lab in {protoPart.partName} is full.");
                        continue;
                    }
                    
                    foreach (ConfigNode experimentNode in storedExperiments)
                    {
                        float processedData = CalculateProcessedDataUnloaded(experimentNode, protoVessel);
                        
                        if (processedData <= availableDataSpace)
                        {
                            // Add the processed data to the lab
                            availableDataSpace -= processedData;
                            float newStoredData = float.Parse(labModule.moduleValues.GetValue("dataStored")) + processedData;
                            labModule.moduleValues.SetValue("dataStored", newStoredData.ToString("F2"));
                            
                            // Remove experiment data from storage
                            storageContainer.moduleValues.RemoveNode(experimentNode);

                            DebugLog($"[AutomaticLabHousekeeper] Transferred {processedData} data from {storagePart.partName} to {protoPart.partName}.");
                        }
                        else
                        {
                            DebugLog($"[AutomaticLabHousekeeper] Not enough space in lab {protoPart.partName} to store experiment {experimentNode.GetValue("subjectID")}. Current stored Data: {labModule.moduleValues.GetValue("dataStored")}");
                        }
                    }
                }
            }
        }

        float CalculateProcessedData(ScienceData experiment, Part part)
        {
            float dataValue = experiment.dataAmount;
            float SurfaceBonus = (part.vessel.LandedOrSplashed) ? 0.1f : 0f;
            float homeworldMultiplier = (part.vessel.mainBody.isHomeWorld && part.vessel.LandedOrSplashed) ? 0.1f : 1f;
            bool sameSOI = experiment.subjectID.Contains(part.vessel.mainBody.bodyName);
            float ContextBonus = sameSOI ? 0.25f : 0f;

            DebugLog($"[AutomaticLabHousekeeper] Base: {dataValue}, Bonuses Applied: SurfaceBonus={SurfaceBonus}, ContextBonus={ContextBonus}, HomeworldMultiplier={homeworldMultiplier}");

            return dataValue * (1 + SurfaceBonus) * (1 + ContextBonus) * homeworldMultiplier;
        }

        float CalculateProcessedDataUnloaded(ConfigNode experimentNode, ProtoVessel protoVessel)
        {
            if (experimentNode == null)
            {
                Debug.LogError("[AutomaticLabHousekeeper] Experiment node is null!");
                return 0f;
            }

            // Extract the base data value (DataValue)
            float dataValue = float.Parse(experimentNode.GetValue("data"));

            // Determine if the vessel is landed or splashed down
            bool landed = protoVessel.landed || protoVessel.splashed;

            // Get the celestial body the vessel is orbiting
            CelestialBody vesselBody = FlightGlobals.Bodies[protoVessel.orbitSnapShot.ReferenceBodyIndex];

            // Apply the surface and homeworld multipliers
            float SurfaceBonus = landed ? 0.1f : 0f;
            float homeworldMultiplier = (vesselBody.isHomeWorld && landed) ? 0.1f : 1f;

            // Determine if the experiment was taken in the same SOI as the lab
            bool sameSOI = experimentNode.GetValue("subjectID").Contains(vesselBody.bodyName);
            float ContextBonus = sameSOI ? 0.25f : 0f;

            DebugLog($"[AutomaticLabHousekeeper] Base Data Value: {dataValue}, Bonuses Applied: " +
                      $"SurfaceBonus={SurfaceBonus}, ContextBonus={ContextBonus}, HomeworldMultiplier={homeworldMultiplier}");

            // Calculate final processed data
            return dataValue * (1 + SurfaceBonus) * (1 + ContextBonus) * homeworldMultiplier;
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
