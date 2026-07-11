using System.IO;
using UnityEngine;

namespace PersistenceKit.Targets
{
    /// <summary>
    /// Binary disk target. Same atomic-write plumbing as <see cref="JsonDiskTarget"/>; the
    /// difference is purely the file extension and the serializer handler wired alongside it.
    /// </summary>
    public sealed class BinaryDiskTarget : DiskTargetBase
    {
        public BinaryDiskTarget()
            : base(DefaultRoot(), ".bin") { }

        public BinaryDiskTarget(string rootDir)
            : base(rootDir, ".bin") { }

        public override PersistTarget Target => PersistTarget.Binary;

        private static string DefaultRoot()
            => Path.Combine(Application.persistentDataPath, "PersistenceKit", "binary");
    }
}
