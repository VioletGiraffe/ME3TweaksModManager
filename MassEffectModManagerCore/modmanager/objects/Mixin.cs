﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MassEffectModManagerCore.modmanager.objects
{
    /// <summary>
    /// MixIns are patches that can be stacked onto the same file multipe times as long as the file size does not change.
    /// They are powered by JojoDiff patch files and applied through the JPatch class
    /// </summary>
    public class Mixin
    {
        public string PatchName { get; set; }
        public string PatchDesc { get; set; }
        public string PatchDeveloper { get; set; }
        public int PatchVersion { get; set; }
        //public string TargetVersion { get; set; }
        public ModJob.JobHeader TargetModule { get; set; }
        public string TargetFile { get; set; }
        public int TargetSize { get; set; }
        public bool IsFinalizer { get; set; }
        //public string patchurl { get; set; }
        public string FolderName { get; set; }
        public int ME3TweaksID { get; set; }
        public string Filename { get; internal set; }
        public MemoryStream PatchData { get; internal set; }
    }
}
