using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using Campfire.Patching;

namespace Campfire.Launcher
{
    /// <summary>
    /// Applies all patches to a launcher file, and restores it from its backup.
    /// </summary>
    internal static class LauncherFixer
    {
        private const string BackupExtension = ".bak";

        public static string GetBackupPath(string launcherPath)
        {
            return launcherPath + BackupExtension;
        }

        public static bool HasBackup(string launcherPath)
        {
            return File.Exists(GetBackupPath(launcherPath));
        }

        /// <summary>
        /// Patches the launcher in place. The original file is kept next to it as a backup.
        /// </summary>
        /// <returns>The report of the patched file.</returns>
        public static LauncherReport Fix(string launcherPath)
        {
            return Fix(launcherPath, null);
        }

        /// <summary>
        /// Patches only the selected repair items. The original file is kept next to it as a backup.
        /// </summary>
        /// <param name="launcherPath">Launcher file to modify.</param>
        /// <param name="selectedPatchIds">Patch identifiers to apply; <c>null</c> selects every pending item.</param>
        /// <returns>The report of the patched file, including any items that were intentionally left unselected.</returns>
        public static LauncherReport Fix(string launcherPath, IReadOnlyCollection<string> selectedPatchIds)
        {
            LauncherFile.EnsureNotRunning(launcherPath);

            // The community edition is a bundle of assemblies; it needs an in-place fix instead of a module rewrite.
            var initial = LauncherInspector.Inspect(launcherPath);
            if (initial.Kind == LauncherKind.Community)
            {
                if (initial.Status == LauncherStatus.Unsupported)
                    throw new LauncherException(initial.MessageKey, initial.MessageArgs);
                if (selectedPatchIds != null
                    && !selectedPatchIds.Contains("RegionLimit", StringComparer.Ordinal))
                {
                    throw new LauncherException("Fix.NothingSelected");
                }
                return CommunityLauncher.Fix(launcherPath);
            }

            var selected = selectedPatchIds == null
                ? null
                : new HashSet<string>(selectedPatchIds, StringComparer.Ordinal);
            byte[] patched;
            string[] appliedPatchIds;
            using (var module = LoadLauncher(launcherPath))
            {
                var report = LauncherInspector.Inspect(launcherPath, module);
                if (!report.CanFix)
                    throw new LauncherException(report.MessageKey, report.MessageArgs);

                var pending = PatchCatalog.All
                    .Where(p => p.GetState(module) == PatchState.Pending)
                    .Where(p => selected == null || selected.Contains(p.Id))
                    .ToList();
                if (pending.Count == 0)
                    throw new LauncherException("Fix.NothingSelected");

                foreach (var patch in pending)
                {
                    try
                    {
                        patch.Apply(module);
                    }
                    catch (PatchException ex)
                    {
                        throw new LauncherException(ex, "Fix.PatchFailed", patch.Title, ex.Message);
                    }
                }

                appliedPatchIds = pending.Select(p => p.Id).ToArray();
                AddPatchMarker(module, report.Version);
                patched = Write(module);
            }

            var verified = Verify(launcherPath, patched, appliedPatchIds);
            LauncherFile.Replace(launcherPath, GetBackupPath(launcherPath), patched, preserveExistingBackup: true);
            return verified;
        }

        /// <summary>
        /// Replaces the launcher with the backup made by <see cref="Fix"/>.
        /// </summary>
        public static LauncherReport Restore(string launcherPath)
        {
            string backupPath = GetBackupPath(launcherPath);
            if (!File.Exists(backupPath))
                throw new LauncherException("Fix.NoBackup");

            var backup = LauncherInspector.Inspect(backupPath);
            if (backup.Status == LauncherStatus.NotLauncher)
                throw new LauncherException("Fix.BadBackup");

            LauncherFile.Restore(launcherPath, backupPath);
            return LauncherInspector.Inspect(launcherPath);
        }

        /// <summary>
        /// Reverts only the selected applied repairs. The original backup is retained when other repairs remain.
        /// </summary>
        public static LauncherReport Restore(string launcherPath, IReadOnlyCollection<string> selectedPatchIds)
        {
            LauncherFile.EnsureNotRunning(launcherPath);
            string backupPath = GetBackupPath(launcherPath);
            if (!File.Exists(backupPath))
                throw new LauncherException("Fix.NoBackup");

            var current = LauncherInspector.Inspect(launcherPath);
            var backup = LauncherInspector.Inspect(backupPath);
            if (current.Status == LauncherStatus.NotLauncher || backup.Status == LauncherStatus.NotLauncher)
                throw new LauncherException("Fix.BadBackup");
            if (current.Kind != backup.Kind || !string.Equals(current.Version, backup.Version, StringComparison.Ordinal))
                throw new LauncherException("Fix.BadBackup");

            var selected = selectedPatchIds == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(selectedPatchIds, StringComparer.Ordinal);
            if (current.Kind == LauncherKind.Community)
            {
                if (!selected.Contains("RegionLimit")
                    || current.Patches.All(p => p.Id != "RegionLimit" || p.State != PatchState.Applied))
                {
                    throw new LauncherException("Fix.NothingToRestore");
                }
                return Restore(launcherPath);
            }

            var backupStates = backup.Patches.ToDictionary(p => p.Id, p => p.State, StringComparer.Ordinal);
            var restoredIds = current.Patches
                .Where(p => p.State == PatchState.Applied && selected.Contains(p.Id))
                .Where(p => backupStates.TryGetValue(p.Id, out var state) && state == PatchState.Pending)
                .Select(p => p.Id)
                .ToList();
            if (restoredIds.Count == 0)
                throw new LauncherException("Fix.NothingToRestore");

            var restoredSet = new HashSet<string>(restoredIds, StringComparer.Ordinal);
            byte[] original = LauncherFile.Read(backupPath);
            byte[] restored;
            string[] keptPatchIds;
            using (var module = LauncherInspector.LoadModule(original))
            {
                var toKeep = PatchCatalog.All
                    .Where(p => !restoredSet.Contains(p.Id))
                    .Where(p => p.GetState(module) == PatchState.Pending)
                    .Where(p => current.Patches.Any(report =>
                        report.Id == p.Id && report.State == PatchState.Applied))
                    .ToList();

                foreach (var patch in toKeep)
                {
                    try
                    {
                        patch.Apply(module);
                    }
                    catch (PatchException ex)
                    {
                        throw new LauncherException(ex, "Fix.PatchFailed", patch.Title, ex.Message);
                    }
                }

                keptPatchIds = toKeep.Select(p => p.Id).ToArray();
                if (toKeep.Count > 0)
                {
                    AddPatchMarker(module, backup.Version);
                    restored = Write(module);
                }
                else
                {
                    restored = original;
                }
            }

            var verified = VerifyRestore(launcherPath, restored, restoredIds, keptPatchIds);
            if (keptPatchIds.Length == 0)
            {
                LauncherFile.Restore(launcherPath, backupPath);
                return LauncherInspector.Inspect(launcherPath);
            }

            LauncherFile.Replace(launcherPath, backupPath, restored, preserveExistingBackup: true);
            return verified;
        }

        private static ModuleDefMD LoadLauncher(string path)
        {
            byte[] data = LauncherFile.Read(path);
            try
            {
                return LauncherInspector.LoadModule(data);
            }
            catch (BadImageFormatException ex)
            {
                throw new LauncherException(ex, "Fix.ReadFailed", ex.Message);
            }
        }

        private static void AddPatchMarker(ModuleDef module, string launcherVersion)
        {
            string toolVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            string content = $"Campfire {toolVersion}\nLauncher {launcherVersion}\nPatched {DateTime.UtcNow:O}\n";

            var existing = module.Resources.FindEmbeddedResource(LauncherSignature.PatchMarkerResource);
            if (existing != null)
                module.Resources.Remove(existing);
            module.Resources.Add(new EmbeddedResource(LauncherSignature.PatchMarkerResource,
                Encoding.UTF8.GetBytes(content), ManifestResourceAttributes.Private));
        }

        private static byte[] Write(ModuleDefMD module)
        {
            var options = new ModuleWriterOptions(module)
            {
                Logger = DummyLogger.ThrowModuleWriterExceptionOnErrorInstance,
            };
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;

            try
            {
                using (var stream = new MemoryStream())
                {
                    module.Write(stream, options);
                    return stream.ToArray();
                }
            }
            catch (ModuleWriterException ex)
            {
                throw new LauncherException(ex, "Fix.BuildFailed", ex.Message);
            }
        }

        /// <summary>Reloads the patched image and checks every required patch point.</summary>
        private static LauncherReport Verify(string launcherPath, byte[] patched, IReadOnlyCollection<string> appliedPatchIds)
        {
            using (var module = LauncherInspector.LoadModule(patched))
            {
                var report = LauncherInspector.Inspect(launcherPath, module);
                var states = report.Patches.ToDictionary(p => p.Id, p => p.State, StringComparer.Ordinal);
                bool complete = appliedPatchIds.All(id =>
                    states.TryGetValue(id, out var state) && state == PatchState.Applied);
                if (!complete)
                    throw new LauncherException("Fix.VerifyFailed");
                return report;
            }
        }

        private static LauncherReport VerifyRestore(string launcherPath, byte[] restored,
            IReadOnlyCollection<string> restoredPatchIds, IReadOnlyCollection<string> keptPatchIds)
        {
            using (var module = LauncherInspector.LoadModule(restored))
            {
                var report = LauncherInspector.Inspect(launcherPath, module);
                var states = report.Patches.ToDictionary(p => p.Id, p => p.State, StringComparer.Ordinal);
                bool complete = restoredPatchIds.All(id =>
                                   states.TryGetValue(id, out var state) && state == PatchState.Pending)
                               && keptPatchIds.All(id =>
                                   states.TryGetValue(id, out var state) && state == PatchState.Applied);
                if (!complete)
                    throw new LauncherException("Fix.VerifyFailed");
                return report;
            }
        }
    }
}
