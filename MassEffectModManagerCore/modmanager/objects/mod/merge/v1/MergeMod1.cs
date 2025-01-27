﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using ME3TweaksCore.GameFilesystem;
using ME3TweaksCore.Objects;
using ME3TweaksCore.Targets;
using ME3TweaksModManager.me3tweakscoreextended;
using ME3TweaksModManager.modmanager.diagnostics;
using ME3TweaksModManager.modmanager.localizations;
using ME3TweaksModManager.modmanager.memoryanalyzer;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ME3TweaksModManager.modmanager.objects.mod.merge.v1
{
    /// <summary>
    /// Merge Mod V1. Merges properties, objects
    /// </summary>
    public class MergeMod1 : IMergeMod
    {
        private const string ManifestSchemaInternalPath = @"ME3TweaksModManager.modmanager.objects.mod.merge.v1.schema.json";
        private const string MMV1_ASSETMAGIC = @"MMV1";
        [JsonIgnore]
        public string MergeModFilename { get; set; }

        [JsonProperty(@"game")]
        [JsonConverter(typeof(StringEnumConverter))]
        public MEGame Game { get; set; } // Only used for sanity check

        [JsonProperty(@"files")]
        public List<MergeFile1> FilesToMergeInto;

        [JsonIgnore]
        public CaseInsensitiveDictionary<MergeAsset> Assets { get; set; }

        private MergeMod1() { }
        public static MergeMod1 ReadMergeMod(Stream mergeFileStream, string mergeModName, bool loadAssets)
        {
            // Version and magic will already be read by main value
            var manifest = mergeFileStream.ReadUnrealString();

            // This doesn't work since serialization strips some info
            // Just will have to trust devs
            // 06/02/2022: Validate against the known schema to prevent future changes from being able to be 'backported' to unsupported versions
            //var schemaText = new StreamReader(M3Utilities.ExtractInternalFileToStream(ManifestSchemaInternalPath)).ReadToEnd();
            //var messages = JsonSchemaValidator.ValidateSchema(manifest, schemaText);
            //if (messages != null && messages.Any())
            //{
            //    M3Log.Error($@"Invalid schema for mergemod manifest {mergeModName}:");
            //    foreach (var m in messages)
            //    {
            //        M3Log.Error($@"  {m}");
            //    }
            //    throw new Exception($"The mergemod {mergeModName} manifest contains an invalid manifest. See the Mod Manager log for details.");
            //}

            var mm = JsonConvert.DeserializeObject<MergeMod1>(manifest);
            M3MemoryAnalyzer.AddTrackedMemoryItem($@"MergeMod1 {mergeModName}", mm);
            mm.MergeModFilename = mergeModName;

            // setup links
            foreach (var ff in mm.FilesToMergeInto)
            {
                ff.SetupParent(mm);
                ff.Validate();
            }

            var assetCount = mergeFileStream.ReadInt32();
            mm.Assets = new CaseInsensitiveDictionary<MergeAsset>(assetCount);
            if (assetCount > 0)
            {
                for (int i = 0; i < assetCount; i++)
                {
                    var assetMag = mergeFileStream.ReadStringASCII(4);
                    if (assetMag != MMV1_ASSETMAGIC)
                    {
                        throw new Exception(M3L.GetString(M3L.string_error_mergefile_badMagic));
                    }

                    MergeAsset ma = new MergeAsset();
                    var assetName = mergeFileStream.ReadUnrealString();
                    ma.FileSize = mergeFileStream.ReadInt32();
                    if (loadAssets)
                    {
                        // Read now
                        ma.ReadAssetBinary(mergeFileStream);
                    }
                    else
                    {
                        // Will load at install time
                        ma.FileOffset = (int)mergeFileStream.Position;
                        mergeFileStream.Skip(ma.FileSize);
                    }

                    mm.Assets[assetName] = ma;
                }
            }

            if (mergeFileStream.Position != mergeFileStream.Length)
            {
                throw new Exception(M3L.GetString(M3L.string_interp_mergefile_serialSizeMismatch, mergeFileStream.Position, mergeFileStream.Length));
            }
            return mm;
        }

        public bool ApplyMergeMod(Mod associatedMod, GameTarget target, Action<int> mergeWeightDelegate, Action<string, string> addTrackedFileDelegate, CaseInsensitiveConcurrentDictionary<string> originalFileMD5Map)
        {
            M3Log.Information($@"Applying {MergeModFilename}.");
            Debug.WriteLine($@"Expected weight: {GetMergeWeight()}");
            var loadedFiles = MELoadedFiles.GetFilesLoadedInGame(target.Game, true, gameRootOverride: target.TargetPath);

            if (target.Game == MEGame.LE2)
            {
                // SPECIAL CASE: LE2 EntryMenu is loaded before DLC version so first load of the file
                // will be basegame one. The majority of time this is likely the desirable
                // file so we only target this one instead.
                loadedFiles[@"EntryMenu.pcc"] = Path.Combine(M3Directories.GetCookedPath(target), @"EntryMenu.pcc");
            }

            int numDone = 0;
            foreach (var mf in FilesToMergeInto)
            {
                mf.ApplyChanges(target, loadedFiles, associatedMod, mergeWeightDelegate, originalFileMD5Map, addTrackedFileDelegate);
                numDone++;
            }

            return true;
        }
        /// <summary>
        /// Gets the number of files to merge into
        /// </summary>
        /// <returns></returns>
        public int GetMergeCount() => FilesToMergeInto.Sum(x => x.GetMergeCount());
        /// <summary>
        /// Gets the weight of changes for more accurate progress tracking
        /// </summary>
        /// <returns></returns>
        public int GetMergeWeight() => FilesToMergeInto.Sum(x => x.GetMergeWeight());
        public void ExtractToFolder(string outputfolder)
        {
            // scripts and assets
            foreach (var mc in FilesToMergeInto.SelectMany(x => x.MergeChanges))
            {
                if (mc.PropertyUpdates is not null)
                {
                    foreach (PropertyUpdate1 propertyUpdate in mc.PropertyUpdates)
                    {
                        if (!string.IsNullOrEmpty(propertyUpdate.PropertyAsset))
                        {
                            File.WriteAllText(Path.Combine(outputfolder, Path.GetFileName(propertyUpdate.PropertyAsset)), propertyUpdate.PropertyValue);
                            propertyUpdate.PropertyValue = null;
                        }
                    }
                }

                if (mc.ScriptUpdate != null)
                {
                    File.WriteAllText(Path.Combine(outputfolder, Path.GetFileName(mc.ScriptUpdate.ScriptFileName)), mc.ScriptUpdate.ScriptText);
                    mc.ScriptUpdate.ScriptText = null;
                }

                if (mc.AssetUpdate != null)
                {
                    File.WriteAllBytes(Path.Combine(outputfolder, Path.GetFileName(mc.AssetUpdate.AssetName)), Assets[mc.AssetUpdate.AssetName].AssetBinary);
                }

                if (mc.AddToClassOrReplace != null)
                {
                    foreach ((string script, string fileName) in mc.AddToClassOrReplace.Scripts.Zip(mc.AddToClassOrReplace.ScriptFileNames))
                    {
                        File.WriteAllText(Path.Combine(outputfolder, Path.GetFileName(fileName)), script);
                    }
                    mc.AddToClassOrReplace.Scripts = null;
                }
            }

            // assets
            Assets = null;

            // json
            File.WriteAllText(Path.Combine(outputfolder, $@"{Path.GetFileNameWithoutExtension(MergeModFilename)}.json"),
                JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Ignore
                }));
        }

        public IEnumerable<string> GetMergeFileTargetFiles()
        {
            List<string> targets = new List<string>();
            foreach (var v in FilesToMergeInto)
            {
                targets.Add(v.FileName);

                if (v.ApplyToAllLocalizations)
                {
                    var targetnameBase = Path.GetFileNameWithoutExtension(v.FileName);
                    var targetExtension = Path.GetExtension(v.FileName);
                    var localizations = GameLanguage.GetLanguagesForGame(Game);

                    // Ensure end name is not present on base
                    foreach (var l in localizations)
                    {
                        if (targetnameBase.EndsWith($@"_{l.FileCode}", StringComparison.InvariantCultureIgnoreCase))
                            targetnameBase = targetnameBase.Substring(0, targetnameBase.Length - (l.FileCode.Length + 1));

                        targets.Add($@"{targetnameBase}_{l.FileCode}{targetExtension}");
                    }
                }
            }


            return targets;
        }

        public static IList<string> Serialize(Stream outStream, string manifestFile)
        {
            M3Log.Information($@"M3MCompiler: Beginning M3M V1 serialization");
            var sourceDir = Directory.GetParent(manifestFile).FullName;

            var manifestText = File.ReadAllText(manifestFile);
            M3Log.Information($@"M3MCompiler: Source json text: {manifestText}", Settings.LogModInstallation); // This is as close as I can get...

            // VALIDATE JSON SCHEMA
            var schemaText = new StreamReader(M3Utilities.ExtractInternalFileToStream(ManifestSchemaInternalPath)).ReadToEnd();
            var schemaFailureMessages = JsonSchemaValidator.ValidateSchema(manifestText, schemaText);
            if (schemaFailureMessages != null && schemaFailureMessages.Any())
            {
                return schemaFailureMessages;
            }

            var mm = JsonConvert.DeserializeObject<MergeMod1>(manifestText);
            M3Log.Information($@"M3MCompiler: Json is valid for schema");

            // Update manifest

            SortedSet<string> assets = new();
            // Get all assets.
            foreach (var fc in mm.FilesToMergeInto)
            {
                foreach (var mc in fc.MergeChanges)
                {
                    if (mc.PropertyUpdates is not null)
                    {
                        foreach (PropertyUpdate1 propertyUpdate in mc.PropertyUpdates)
                        {
                            if (propertyUpdate.PropertyType is @"ArrayProperty")
                            {
                                var assetFilePath = Path.Combine(sourceDir, propertyUpdate.PropertyAsset);
                                M3Log.Information($@"M3MCompiler: ArrayProperty file being added into m3m: {assetFilePath}");
                                if (!File.Exists(assetFilePath))
                                {
                                    throw new Exception(M3L.GetString(M3L.string_interp_error_mergefile_scriptNotFoundX, propertyUpdate.PropertyAsset));
                                }
                                propertyUpdate.PropertyValue = File.ReadAllText(assetFilePath);
                            }
                        }
                    }

                    if (mc.AssetUpdate?.AssetName != null)
                    {
                        var assetPath = Path.Combine(sourceDir, mc.AssetUpdate.AssetName);
                        if (!File.Exists(assetPath))
                        {
                            throw new Exception(M3L.GetString(M3L.string_interp_error_mergefile_assetNotFoundX, mc.AssetUpdate.AssetName));
                        }
                        M3Log.Information($@"M3MCompiler: Adding merge asset file to m3m: {assetPath}");
                        assets.Add(mc.AssetUpdate.AssetName);
                    }

                    if (mc.ScriptUpdate?.ScriptFileName != null)
                    {
                        var scriptDiskFile = Path.Combine(sourceDir, mc.ScriptUpdate.ScriptFileName);
                        if (!File.Exists(scriptDiskFile))
                        {
                            throw new Exception(M3L.GetString(M3L.string_interp_error_mergefile_scriptNotFoundX, mc.ScriptUpdate.ScriptFileName));
                        }
                        M3Log.Information($@"M3MCompiler: Adding script update file to m3m: {scriptDiskFile}");
                        mc.ScriptUpdate.ScriptText = File.ReadAllText(scriptDiskFile);
                    }

                    if (mc.AddToClassOrReplace?.ScriptFileNames is { Length: > 0 } fileNames)
                    {
                        mc.AddToClassOrReplace.Scripts = new string[fileNames.Length];
                        for (int i = 0; i < fileNames.Length; i++)
                        {
                            string fileName = fileNames[i];
                            string scriptDiskFile = Path.Combine(sourceDir, fileName);
                            if (!File.Exists(scriptDiskFile))
                            {
                                throw new Exception(M3L.GetString(M3L.string_interp_error_mergefile_scriptNotFoundX, fileName));
                            }

                            M3Log.Information($@"M3MCompiler: Adding AddToClassOrReplace file to m3m: {scriptDiskFile}");
                            mc.AddToClassOrReplace.Scripts[i] = File.ReadAllText(scriptDiskFile);
                        }
                    }
                }
            }

            M3Log.Information($@"M3MCompiler: Serializing merge mod object");
            outStream.WriteUnrealStringUnicode(JsonConvert.SerializeObject(mm, Formatting.None));

            M3Log.Information($@"M3MCompiler: Serializing {assets.Count} referenced assets");
            outStream.WriteInt32(assets.Count);
            foreach (var asset in assets)
            {
                M3Log.Information($@"M3MCompiler: Serializing {asset} into m3m");
                outStream.WriteStringASCII(MMV1_ASSETMAGIC); // MAGIC
                outStream.WriteUnrealStringUnicode(asset); // ASSET NAME
                var assetBytes = File.ReadAllBytes(Path.Combine(sourceDir, asset));
                outStream.WriteInt32(assetBytes.Length); // ASSET LENGTH
                outStream.Write(assetBytes); // ASSET DATA
            }

            M3Log.Information($@"M3MCompiler: V1 serialization complete");
            return null;
        }
    }
}
