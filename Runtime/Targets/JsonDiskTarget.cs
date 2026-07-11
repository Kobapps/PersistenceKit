using System.IO;
using UnityEngine;

namespace PersistenceKit.Targets
{
    /// <summary>
    /// JSON-flavoured disk target. Files are written under
    /// <c>&lt;persistentDataPath&gt;/PersistenceKit/&lt;key&gt;.json</c> by default. The serializer
    /// handler decides the byte content; this target only handles disk I/O.
    /// </summary>
    public sealed class JsonDiskTarget : DiskTargetBase
    {
        public JsonDiskTarget()
            : base(DefaultRoot(), ".json") { }

        public JsonDiskTarget(string rootDir)
            : base(rootDir, ".json") { }

        public override PersistTarget Target => PersistTarget.Json;

        private static string DefaultRoot()
        {
            // Application.persistentDataPath is only valid when called from the main thread of a
            // running Unity process. Tests pass an explicit rootDir to avoid touching this.
            return Path.Combine(Application.persistentDataPath, "PersistenceKit", "json");
        }
    }
}
