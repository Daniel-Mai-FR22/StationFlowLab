using System;
using System.IO;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Resolve capture storage from Unity's absolute application data path, independently of the launch working directory.</summary>
    public static class StationDecisionCaptureStorage
    {
        public static string ResolveRoot(string applicationDataPath, bool macApplicationBundle = false)
        {
            if (string.IsNullOrWhiteSpace(applicationDataPath) || !Path.IsPathRooted(applicationDataPath))
                throw new ArgumentException("The application data path must be absolute.", nameof(applicationDataPath));
            string root = Path.GetPathRoot(applicationDataPath);
            // On Windows, C:relative and \relative are resolved against process state. Neither is portable evidence storage.
            if (string.IsNullOrEmpty(root) || root.Length == 2 && root[1] == ':' || Path.DirectorySeparatorChar == '\\' && root == "\\")
                throw new ArgumentException("The application data path must include its complete drive or UNC share.", nameof(applicationDataPath));
            string full = Path.GetFullPath(applicationDataPath);
            string parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent)) throw new ArgumentException("The application data path has no parent directory.", nameof(applicationDataPath));
            if (macApplicationBundle)
            {
                if (!string.Equals(Path.GetFileName(full), "Contents", StringComparison.Ordinal) ||
                    !string.Equals(Path.GetExtension(parent), ".app", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("A macOS player data path must identify an .app/Contents directory.", nameof(applicationDataPath));
                parent = Path.GetDirectoryName(parent);
                if (string.IsNullOrEmpty(parent)) throw new ArgumentException("The application bundle has no parent directory.", nameof(applicationDataPath));
            }
            return Path.Combine(parent, "Captures", "TrainStationDaily");
        }
    }
}
