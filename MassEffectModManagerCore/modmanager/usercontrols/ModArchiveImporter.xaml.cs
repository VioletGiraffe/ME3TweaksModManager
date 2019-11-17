﻿using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IniParser.Model;

using MassEffectModManagerCore.modmanager.helpers;
using SevenZip;
using MassEffectModManagerCore.modmanager.me3tweaks;
using MassEffectModManagerCore.modmanager.objects;
using MassEffectModManagerCore.ui;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Threading;

namespace MassEffectModManagerCore.modmanager.usercontrols
{
    /// <summary>
    /// Interaction logic for ModArchiveImporter.xaml
    /// </summary>
    public partial class ModArchiveImporter : MMBusyPanelBase
    {
        public bool TaskRunning { get; private set; }
        public string NoModSelectedText { get; } = "Select a mod on the left to view its description";
        public bool ArchiveScanned { get; set; }
        public override void HandleKeyPress(object sender, KeyEventArgs e)
        {
            if (!TaskRunning && e.Key == Key.Escape)
            {
                OnClosing(DataEventArgs.Empty);
            }
        }

        public bool CompressPackages { get; set; }

        public int CompressionProgressValue { get; set; }
        public int CompressionProgressMaximum { get; set; } = 100;

        public Mod SelectedMod { get; private set; }

        private string ArchiveFilePath;

        public string ScanningFile { get; private set; } = "Please wait";
        public string ActionText { get; private set; }
        public int ProgressValue { get; private set; }
        public int ProgressMaximum { get; private set; }
        public bool ProgressIndeterminate { get; private set; }

        public bool CanCompressPackages { get; private set; }

        public ObservableCollectionExtended<Mod> CompressedMods { get; } = new ObservableCollectionExtended<Mod>();
        public ModArchiveImporter(string file)
        {
            DataContext = this;
            LoadCommands();
            ArchiveFilePath = file;
            InitializeComponent();
        }

        /// <summary>
        /// Begins inspection of archive file. This method will spawn a background thread that will
        /// run asynchronously.
        /// </summary>
        /// <param name="filepath">Path to the archive file</param>
        private void InspectArchiveFile(string filepath)
        {
            ScanningFile = Path.GetFileName(filepath);
            NamedBackgroundWorker bw = new NamedBackgroundWorker("ModArchiveInspector");
            bw.DoWork += InspectArchiveBackgroundThread;
            ProgressValue = 0;
            ProgressMaximum = 100;
            ProgressIndeterminate = true;

            bw.RunWorkerCompleted += (a, b) =>
            {
                if (CompressedMods.Count > 0)
                {
                    ActionText = $"Select mods to import into Mod Manager library";
                    if (CompressedMods.Count == 1)
                    {
                        CompressedMods_ListBox.SelectedIndex = 0; //Select the only item
                    }
                    ArchiveScanned = true;
                    //Initial release disables this.
                    CanCompressPackages = false && CompressedMods.Any() && CompressedMods.Any(x => x.Game == Mod.MEGame.ME3); //Change to include ME2 when support for LZO is improved
                }
                else
                {
                    ActionText = "No compatible mods found in archive";
                }

                ProgressValue = 0;
                ProgressIndeterminate = false;
                TaskRunning = false;
                CommandManager.InvalidateRequerySuggested();
            };
            ActionText = $"Scanning {Path.GetFileName(filepath)}";

            bw.RunWorkerAsync(filepath);
        }

        /// <summary>
        /// Notifies listeners when given property is updated.
        /// </summary>
        /// <param name="propertyname">Name of property to give notification for. If called in property, argument can be ignored as it will be default.</param>
        protected virtual void hack_NotifyPropertyChanged([CallerMemberName] string propertyname = null)
        {
            hack_PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyname));
        }

        private void InspectArchiveBackgroundThread(object sender, DoWorkEventArgs e)
        {
            TaskRunning = true;
            ActionText = $"Opening {ScanningFile}";

            var archive = e.Argument as string;

            //Embedded executables.
            var archiveSize = new FileInfo(archive).Length;
            var knownModsOfThisSize = ThirdPartyServices.GetImportingInfosBySize(archiveSize);
            string pathOverride = null;
            if (knownModsOfThisSize.Count > 0 && knownModsOfThisSize.Any(x => x.zippedexepath != null))
            {
                //might have embedded exe
                if (archive.RepresentsFileArchive())
                {
                    SevenZipExtractor sve = new SevenZipExtractor(archive);
                    string embeddedExePath = null;
                    foreach (var importingInfo in knownModsOfThisSize)
                    {
                        if (importingInfo.zippedexepath == null) continue;
                        if (sve.ArchiveFileNames.Contains(importingInfo.zippedexepath))
                        {
                            embeddedExePath = importingInfo.zippedexepath;
                            //Ensure embedded exe is supported at least by decompressed size
                            var exedata = sve.ArchiveFileData.FirstOrDefault(x => x.FileName == embeddedExePath);
                            if (exedata.FileName != null)
                            {
                                var importingInfo2 = ThirdPartyServices.GetImportingInfosBySize((long)exedata.Size);
                                if (importingInfo2.Count == 0)
                                {
                                    Log.Warning("zip wrapper for this file has importing information but the embedded exe does not!");
                                    break; //no importing info
                                }

                                ActionText = "Reading zipped executable";
                                pathOverride = Path.Combine(Utilities.GetTempPath(), Path.GetFileName(embeddedExePath));
                                using var outstream = new FileStream(pathOverride, FileMode.Create);
                                sve.Extracting += (o, pea) => { ActionText = $"Reading zipped executable {pea.PercentDone}%"; };
                                sve.ExtractFile(embeddedExePath, outstream);
                                ArchiveFilePath = pathOverride; //set new path so further extraction calls use correct archive path.
                                break;
                            }
                        }
                    }

                }

            }

            void AddCompressedModCallback(Mod m)
            {
                Application.Current.Dispatcher.Invoke(delegate
                {
                    CompressedMods.Add(m);
                    CompressedMods.Sort(x => x.ModName);
                });
            }
            void ActionTextUpdateCallback(string newText)
            {
                ActionText = newText;
            }
            InspectArchive(pathOverride ?? archive, AddCompressedModCallback, ActionTextUpdateCallback);
        }

        //this should be private but no way to test it private for now...

        /// <summary>
        /// Inspects and loads compressed mods from an archive.
        /// </summary>
        /// <param name="filepath">Path of the archive</param>
        /// <param name="addCompressedModCallback">Callback indicating that the mod should be added to the collection of found mods</param>
        /// <param name="currentOperationTextCallback">Callback to tell caller what's going on'</param>
        /// <param name="forcedOverrideData">Override data about archive. Used for testing only</param>
        public static void InspectArchive(string filepath, Action<Mod> addCompressedModCallback = null, Action<string> currentOperationTextCallback = null, string forcedMD5 = null, int forcedSize = -1)
        {
            string relayVersionResponse = "-1";
            List<Mod> internalModList = new List<Mod>(); //internal mod list is for this function only so we don't need a callback to get our list since results are returned immediately
            var archiveFile = filepath.EndsWith(".exe") ? new SevenZipExtractor(filepath, InArchiveFormat.Nsis) : new SevenZipExtractor(filepath);
            using (archiveFile)
            {
                var moddesciniEntries = new List<ArchiveFileInfo>();
                var sfarEntries = new List<ArchiveFileInfo>(); //ME3 DLC
                var bioengineEntries = new List<ArchiveFileInfo>(); //ME2 DLC
                foreach (var entry in archiveFile.ArchiveFileData)
                {
                    string fname = Path.GetFileName(entry.FileName);
                    if (!entry.IsDirectory && fname.Equals("moddesc.ini", StringComparison.InvariantCultureIgnoreCase))
                    {
                        moddesciniEntries.Add(entry);
                    }
                    else if (!entry.IsDirectory && fname.Equals("Default.sfar", StringComparison.InvariantCultureIgnoreCase))
                    {
                        //for unofficial lookups
                        sfarEntries.Add(entry);
                    }
                    else if (!entry.IsDirectory && fname.Equals("BIOEngine.ini", StringComparison.InvariantCultureIgnoreCase))
                    {
                        //for unofficial lookups
                        bioengineEntries.Add(entry);
                    }
                }

                if (moddesciniEntries.Count > 0)
                {
                    foreach (var entry in moddesciniEntries)
                    {
                        currentOperationTextCallback?.Invoke($"Reading {entry.FileName}");
                        Mod m = new Mod(entry, archiveFile);
                        if (m.ValidMod)
                        {
                            addCompressedModCallback?.Invoke(m);
                            internalModList.Add(m);
                        }
                    }
                }
                else
                {
                    Log.Information("Querying third party importing service for information about this file");
                    currentOperationTextCallback?.Invoke($"Querying Third Party Importing Service");
                    var md5 = forcedMD5 ?? Utilities.CalculateMD5(filepath);
                    long size = forcedSize > 0 ? forcedSize : new FileInfo(filepath).Length;
                    var potentialImportinInfos = ThirdPartyServices.GetImportingInfosBySize(size);
                    var importingInfo = potentialImportinInfos.FirstOrDefault(x => x.md5 == md5);
                    if (importingInfo?.servermoddescname != null)
                    {
                        //Partially supported unofficial third party mod
                        //Mod has a custom written moddesc.ini stored on ME3Tweaks
                        Log.Information("Fetching premade moddesc.ini from ME3Tweaks for this mod archive");
                        string custommoddesc = OnlineContent.FetchThirdPartyModdesc(importingInfo.servermoddescname);
                        Mod virutalCustomMod = new Mod(custommoddesc, "", archiveFile); //Load virutal mod
                        if (virutalCustomMod.ValidMod)
                        {
                            addCompressedModCallback?.Invoke(virutalCustomMod);
                            internalModList.Add(virutalCustomMod);
                            return; //Don't do further parsing as this is custom written
                        }
                        else
                        {
                            Log.Error("Server moddesc was not valid for this mod. This shouldn't occur. Please report to Mgamerz.");
                            return;
                        }
                    }

                    //Fully unofficial third party mod.

                    //ME3
                    foreach (var sfarEntry in sfarEntries)
                    {
                        var vMod = AttemptLoadVirtualMod(sfarEntry, archiveFile, Mod.MEGame.ME3, md5);
                        if (vMod.ValidMod)
                        {
                            addCompressedModCallback?.Invoke(vMod);
                            internalModList.Add(vMod);
                        }
                    }

                    //TODO: ME2
                    //foreach (var entry in bioengineEntries)
                    //{
                    //    var vMod = AttemptLoadVirtualMod(entry, archiveFile, Mod.MEGame.ME2, md5);
                    //    if (vMod.ValidMod)
                    //    {
                    //        addCompressedModCallback?.Invoke(vMod);
                    //        internalModList.Add(vMod);
                    //    }
                    //}

                    //TODO: ME1

                    if (importingInfo?.version != null)
                    {
                        foreach (Mod compressedMod in internalModList)
                        {
                            compressedMod.ModVersionString = importingInfo.version;
                            Version.TryParse(importingInfo.version, out var parsedValue);
                            compressedMod.ParsedModVersion = parsedValue;
                        }
                    }
                    else if (relayVersionResponse == "-1")
                    {
                        //If no version information, check ME3Tweaks to see if it's been added recently
                        //see if server has information on version number
                        currentOperationTextCallback?.Invoke($"Getting additional information about file from ME3Tweaks");
                        Log.Information("Querying ME3Tweaks for additional information");
                        var modInfo = OnlineContent.QueryModRelay(md5, size);
                        //todo: make this work offline.
                        if (modInfo != null && modInfo.TryGetValue("version", out string value))
                        {
                            Log.Information("ME3Tweaks reports version number for this file is: " + value);
                            foreach (Mod compressedMod in internalModList)
                            {
                                compressedMod.ModVersionString = value;
                                Version.TryParse(value, out var parsedValue);
                                compressedMod.ParsedModVersion = parsedValue;
                            }

                            relayVersionResponse = value;
                        }
                        else
                        {
                            Log.Information("ME3Tweaks does not have additional version information for this file");
                        }
                    }

                    else
                    {
                        //Try straight up TPMI import?

                        Log.Warning($"No importing information is available for file with hash {md5}. No mods could be found.");
                    }
                }
            }
        }


        private static Mod AttemptLoadVirtualMod(ArchiveFileInfo sfarEntry, SevenZipExtractor archive, Mod.MEGame game, string md5)
        {
            var sfarPath = sfarEntry.FileName;
            var cookedPath = FilesystemInterposer.DirectoryGetParent(sfarPath, true);
            //Todo: Check if value is CookedPC/CookedPCConsole as further validation
            if (!string.IsNullOrEmpty(FilesystemInterposer.DirectoryGetParent(cookedPath, true)))
            {
                var dlcDir = FilesystemInterposer.DirectoryGetParent(cookedPath, true);
                var dlcFolderName = Path.GetFileName(dlcDir);
                if (!string.IsNullOrEmpty(dlcFolderName))
                {
                    var thirdPartyInfo = ThirdPartyServices.GetThirdPartyModInfo(dlcFolderName, game);
                    if (thirdPartyInfo != null)
                    {
                        Log.Information($"Third party mod found: {thirdPartyInfo.modname}, preparing virtual moddesc.ini");
                        //We will have to load a virtual moddesc. Since Mod constructor requires reading an ini, we will build an feed it a virtual one.
                        IniData virtualModDesc = new IniData();
                        virtualModDesc["ModManager"]["cmmver"] = App.HighestSupportedModDesc.ToString();
                        virtualModDesc["ModInfo"]["modname"] = thirdPartyInfo.modname;
                        virtualModDesc["ModInfo"]["moddev"] = thirdPartyInfo.moddev;
                        virtualModDesc["ModInfo"]["modsite"] = thirdPartyInfo.modsite;
                        virtualModDesc["ModInfo"]["moddesc"] = thirdPartyInfo.moddesc;
                        virtualModDesc["ModInfo"]["unofficial"] = "true";
                        if (int.TryParse(thirdPartyInfo.updatecode, out var updatecode) && updatecode > 0)
                        {
                            virtualModDesc["ModInfo"]["updatecode"] = updatecode.ToString();
                            virtualModDesc["ModInfo"]["modver"] = 0.001.ToString(); //This will force mod to check for update after reload
                        }
                        else
                        {
                            virtualModDesc["ModInfo"]["modver"] = 0.0.ToString(); //Will attempt to look up later after mods have parsed.
                        }

                        virtualModDesc["CUSTOMDLC"]["sourcedirs"] = dlcFolderName;
                        virtualModDesc["CUSTOMDLC"]["destdirs"] = dlcFolderName;
                        virtualModDesc["UPDATES"]["originalarchivehash"] = md5;

                        var archiveSize = new FileInfo(archive.FileName).Length;
                        var importingInfos = ThirdPartyServices.GetImportingInfosBySize(archiveSize);
                        if (importingInfos.Count == 1 && importingInfos[0].GetParsedRequiredDLC().Count > 0)
                        {
                            OnlineContent.QueryModRelay(importingInfos[0].md5, archiveSize); //Tell telemetry relay we are accessing the TPIS for an existing item so it can update latest for tracking
                            virtualModDesc["ModInfo"]["requireddlc"] = importingInfos[0].requireddlc;
                        }

                        return new Mod(virtualModDesc.ToString(), FilesystemInterposer.DirectoryGetParent(dlcDir, true), archive);
                    }
                }
                else
                {
                    Log.Information($"No third party mod information for importing {dlcFolderName}. Should this be supported for import? Contact Mgamerz on the ME3Tweaks Discord if it should.");
                }
            }

            return null;
        }

        private void BeginImportingMods()
        {
            var modsToExtract = CompressedMods.Where(x => x.SelectedForImport).ToList();
            NamedBackgroundWorker bw = new NamedBackgroundWorker("ModExtractor");
            bw.DoWork += ExtractModsBackgroundThread;
            bw.RunWorkerCompleted += (a, b) =>
            {
                TaskRunning = false;
                if (b.Result is List<Mod> modList)
                {
                    OnClosing(new DataEventArgs(modList));
                    return;
                }
                if (b.Result is int resultcode)
                {
                    if (resultcode == USER_ABORTED_IMPORT)
                    {
                        ProgressValue = 0;
                        ProgressMaximum = 100;
                        ProgressIndeterminate = false;
                        ActionText = "Select mods to import or install";
                        return; //Don't do anything.
                    } 
                    if (resultcode == ERROR_COULD_NOT_DELETE_EXISTING_DIR)
                    {
                        ProgressValue = 0;
                        ProgressMaximum = 100;
                        ProgressIndeterminate = false;
                        ActionText = "Error: Unable to delete existing mod directory";
                        return; //Don't do anything.
                    }
                }
                //Close.
                OnClosing(DataEventArgs.Empty);
            };
            TaskRunning = true;
            bw.RunWorkerAsync(modsToExtract);
        }

        private void ExtractModsBackgroundThread(object sender, DoWorkEventArgs e)
        {
            List<Mod> mods = (List<Mod>)e.Argument;
            List<Mod> extractedMods = new List<Mod>();

            void TextUpdateCallback(string x)
            {
                ActionText = x;
            }

            foreach (var mod in mods)
            {
                //Todo: Extract files
                Log.Information("Extracting mod: " + mod.ModName);
                ActionText = $"Extracting {mod.ModName}";
                ProgressValue = 0;
                ProgressMaximum = 100;
                ProgressIndeterminate = true;
                //Ensure directory
                var modDirectory = Utilities.GetModDirectoryForGame(mod.Game);
                var sanitizedPath = Path.Combine(modDirectory, Utilities.SanitizePath(mod.ModName));
                if (Directory.Exists(sanitizedPath))
                {
                    //Will delete on import
                    bool abort = false;
                    Application.Current.Dispatcher.Invoke(delegate
                    {
                        var result = Xceed.Wpf.Toolkit.MessageBox.Show(Window.GetWindow(this), $"Importing this mod will delete an existing mod directory in the mod library:\n{sanitizedPath}\n\nContinue?", "Mod already exists", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                        if (result == MessageBoxResult.No)
                        {
                            e.Result = USER_ABORTED_IMPORT;
                            abort = true;
                            return;
                        }

                        try
                        {
                            if (!Utilities.DeleteFilesAndFoldersRecursively(sanitizedPath))
                            {
                                Log.Error("Could not delete existing mod directory.");
                                e.Result = ERROR_COULD_NOT_DELETE_EXISTING_DIR;
                                Xceed.Wpf.Toolkit.MessageBox.Show(Window.GetWindow(this), $"Error occured while deleting existing mod directory. It is likely an open program has a handle to a file or folder in it. See the Mod Manager logs for more information", "Error deleting existing mod", MessageBoxButton.OK, MessageBoxImage.Error);
                                abort = true;
                                return;
                            }

                        }
                        catch (Exception ex)
                        {
                            //I don't think this can be triggered but will leave as failsafe anyways.
                            Log.Error("Error while deleting existing output directory: " + App.FlattenException(ex));
                            Xceed.Wpf.Toolkit.MessageBox.Show(Window.GetWindow(this), $"Error occured while deleting existing mod directory:\n{ex.Message}", "Error deleting existing mod", MessageBoxButton.OK, MessageBoxImage.Error);
                            e.Result = ERROR_COULD_NOT_DELETE_EXISTING_DIR;
                            abort = true;
                        }
                    });
                    if (abort)
                    {
                        Log.Warning("Aborting mod import.");
                        return;
                    }
                }

                //Debug only.
                var f = File.ReadAllText(@"C:\users\mgame\desktop\mehem_0.5_exeextract.xml");
                ExeTransform exet = new ExeTransform(f);
                //End Debug only.

                Directory.CreateDirectory(sanitizedPath);
                mod.ExtractFromArchive(ArchiveFilePath, sanitizedPath, CompressPackages, TextUpdateCallback, ExtractionProgressCallback, CompressedPackageCallback, exet);
                extractedMods.Add(mod);
            }
            e.Result = extractedMods;
        }

        /// <summary>
        /// Class for exe-file extraction transformations
        /// </summary>
        public class ExeTransform
        {
            public ExeTransform(string xml)
            {
                var doc = XDocument.Parse(xml);
                VPatches.ReplaceAll(doc.Root.Elements("vpatch")
                    .Select(d => new VPatchDirective
                    {
                        inputfile = (string)d.Attribute("inputfile"),
                        outputfile = (string)d.Attribute("outputfile"),
                        patchfile = (string)d.Attribute("patchfile")
                    }).ToList());
                PatchRedirects.ReplaceAll(doc.Root.Elements("patchredirect")
                    .Select(d => ((int)d.Attribute("index"), (string)d.Attribute("outfile"))).ToList());
            }
            public List<VPatchDirective> VPatches = new List<VPatchDirective>();
            public List<(int index, string outfile)> PatchRedirects = new List<(int index, string outfile)>();

            public class VPatchDirective
            {
                public string inputfile;
                public string outputfile;
                public string patchfile;
            }
        }

        private void ExtractionProgressCallback(ProgressEventArgs args)
        {
            ProgressValue = args.PercentDone;
            ProgressMaximum = 100;
            ProgressIndeterminate = false;
        }

        private void CompressedPackageCallback(string activityString, int numDone, int numToDo)
        {
            //progress for compression
            if (ProgressValue >= ProgressMaximum)
            {
                ActionText = activityString;
            }
            CompressionProgressMaximum = numToDo;
            CompressionProgressValue = numDone;

        }

        private SerialQueue fileCompressionQueue = new SerialQueue();

        public ICommand ImportModsCommand { get; set; }
        public ICommand CancelCommand { get; set; }
        public ICommand InstallModCommand { get; set; }

        public string InstallModText
        {
            get
            {
                if (SelectedMod != null)
                {
                    return "Install " + SelectedMod.ModName;
                }

                return "Install";
            }
        }

        private void LoadCommands()
        {
            ImportModsCommand = new GenericCommand(BeginImportingMods, CanImportMods);
            CancelCommand = new GenericCommand(Cancel, CanCancel);
            InstallModCommand = new GenericCommand(InstallCompressedMod, CanInstallCompressedMod);
        }

        private static ModJob.JobHeader[] CurrentlyDirectInstallSupportedJobs = { ModJob.JobHeader.BASEGAME, ModJob.JobHeader.BALANCE_CHANGES, ModJob.JobHeader.CUSTOMDLC };
        private readonly int USER_ABORTED_IMPORT = 1;
        private readonly int ERROR_COULD_NOT_DELETE_EXISTING_DIR = 2;

        private bool CanInstallCompressedMod()
        {
            //This will have to pass some sort of validation code later.
            return CompressedMods_ListBox != null && CompressedMods_ListBox.SelectedItem is Mod cm && !TaskRunning/*&& CurrentlyDirectInstallSupportedJobs.ContainsAll(cm.Mod.InstallationJobs.Select(x => x.Header)*/;
        }

        private void InstallCompressedMod()
        {
            OnClosing(new DataEventArgs(CompressedMods_ListBox.SelectedItem));
        }

        private void Cancel()
        {
            OnClosing(DataEventArgs.Empty);
        }

        private bool CanCancel() => !TaskRunning;

        private bool CanImportMods() => !TaskRunning && CompressedMods.Any(x => x.SelectedForImport);

        public event PropertyChangedEventHandler hack_PropertyChanged;

        private void SelectedMod_Changed(object sender, SelectionChangedEventArgs e)
        {
            SelectedMod = CompressedMods_ListBox.SelectedItem as Mod;
        }

        public override void OnPanelVisible()
        {
            InspectArchiveFile(ArchiveFilePath);
        }
    }
}
