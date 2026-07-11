using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PersistenceKit.Editor.Skill
{
    /// <summary>
    /// Installs the bundled "persistencekit" Claude skill into the project's
    /// <c>.claude/skills/</c> folder so AI assistants working in this project get accurate
    /// integration guidance (how to declare states, wire the builder, save/load, encrypt,
    /// verify via the Unity MCP).
    /// </summary>
    /// <remarks>
    /// The skill source ships inside the package under <c>Editor/Skill/SkillTemplate~/</c>.
    /// The <c>~</c> suffix hides it from Unity's asset import but the files still travel with
    /// the package (UPM includes <c>~</c> folders) and remain on disk, so we can resolve and
    /// copy them at install time. The destination is the consuming project's root
    /// <c>.claude/skills/persistencekit/</c> — the location Claude Code / the Agent SDK scan
    /// for project-scoped skills.
    /// </remarks>
    internal static class PersistenceKitSkillInstaller
    {
        public const string SkillName = "persistencekit";

        /// <summary>Absolute path to the project root (one level above <c>Assets/</c>).</summary>
        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>Where the skill is installed to (project-scoped).</summary>
        public static string DestinationDir =>
            Path.Combine(ProjectRoot, ".claude", "skills", SkillName);

        /// <summary>True when the skill's SKILL.md already exists at the destination.</summary>
        public static bool IsInstalled() => File.Exists(Path.Combine(DestinationDir, "SKILL.md"));

        /// <summary>
        /// Resolve the on-disk folder holding the shipped skill template. Works whether the
        /// package is embedded in <c>Assets/</c>, an embedded package under <c>Packages/</c>,
        /// or resolved from the Package Cache — <see cref="FileUtil.GetPhysicalPath"/> maps the
        /// asset path to its real location.
        /// </summary>
        public static string SourceDir()
        {
            var guids = AssetDatabase.FindAssets("PersistenceKitSkillInstaller t:MonoScript");
            for (int i = 0; i < guids.Length; i++)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!assetPath.EndsWith("PersistenceKitSkillInstaller.cs", StringComparison.Ordinal))
                    continue;

                var physical = ToPhysicalPath(assetPath);
                var scriptDir = Path.GetDirectoryName(physical);
                if (string.IsNullOrEmpty(scriptDir)) continue;

                var candidate = Path.Combine(scriptDir, "SkillTemplate~", SkillName);
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string ToPhysicalPath(string assetPath)
        {
            // FileUtil.GetPhysicalPath resolves Packages/PackageCache paths; fall back to a
            // plain project-relative resolve for older editors / embedded-in-Assets layouts.
            try
            {
                var physical = FileUtil.GetPhysicalPath(assetPath);
                if (!string.IsNullOrEmpty(physical)) return Path.GetFullPath(physical);
            }
            catch { /* not available — fall through */ }
            return Path.GetFullPath(assetPath);
        }

        /// <summary>
        /// Copy the skill template into <see cref="DestinationDir"/>. Returns the destination
        /// path on success. Throws with a readable message the caller can surface in a dialog.
        /// </summary>
        public static string Install(bool overwrite)
        {
            var src = SourceDir();
            if (src == null)
                throw new FileNotFoundException(
                    "Could not locate the bundled skill template (Editor/Skill/SkillTemplate~/). " +
                    "Reimport the PersistenceKit package.");

            var dest = DestinationDir;
            if (Directory.Exists(dest) && !overwrite)
                return dest;   // caller decides whether to prompt for overwrite

            CopyDirectory(src, dest);
            return dest;
        }

        private static void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(src))
            {
                // Skip Unity meta files if any ever land in the template folder.
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            }
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
