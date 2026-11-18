// Isolated validation fixture only. Do not copy into a production project.
using UnityEngine;

namespace Suyao.ResourceOrganizer.ValidationFixtures
{
    public sealed class FR2_Cache : ScriptableObject
    {
        public int revision;
        public string[] AssetGuids;
        public Object ActualReference;
    }
}
