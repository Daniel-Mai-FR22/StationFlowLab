using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class StationDecisionDataSchemaMetadata
    {
        public string schemaVersion, contractFingerprint, htmlFile, jsonFile, readerFile, readerReadmeFile;
    }

    /// <summary>The offline data contract packaged with the player and copied, unchanged, into each capture.</summary>
    public static class StationDecisionDataSchemaInfo
    {
        public const string SchemaVersion = "stationflow.station_data_schema.v1";
        public const string RelativeDirectory = "TrainStationData";
        public const string HtmlFileName = "DataSchema.fr.html";
        public const string JsonFileName = "data-schema.json";
        public const string ReaderFileName = "read_capture.py";
        public const string ReaderReadmeFileName = "LireDonnees.fr.txt";

        public static StationDecisionDataSchemaMetadata ReadPackagedMetadata()
        {
            string folder = Path.Combine(Application.streamingAssetsPath, RelativeDirectory);
            string json = File.ReadAllText(Path.Combine(folder, JsonFileName), Encoding.UTF8);
            string html = File.ReadAllText(Path.Combine(folder, HtmlFileName), Encoding.UTF8);
            return ValidateDocumentPair(json, html);
        }

        public static StationDecisionDataSchemaMetadata CopyToCapture(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("A capture output directory is required.");
            string source = Path.Combine(Application.streamingAssetsPath, RelativeDirectory);
            byte[] json = File.ReadAllBytes(Path.Combine(source, JsonFileName));
            byte[] html = File.ReadAllBytes(Path.Combine(source, HtmlFileName));
            var result = ValidateDocumentPair(Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(html));
            string destination = Path.Combine(Path.GetFullPath(outputDirectory), RelativeDirectory);
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(Path.Combine(destination, JsonFileName), json);
            File.WriteAllBytes(Path.Combine(destination, HtmlFileName), html);
            foreach (string filename in new[] { ReaderFileName, ReaderReadmeFileName })
            {
                byte[] support = File.ReadAllBytes(Path.Combine(source, filename));
                if (support.Length == 0) throw new InvalidDataException("The packaged normalized-data reader is empty: " + filename);
                File.WriteAllBytes(Path.Combine(destination, filename), support);
            }
            return result;
        }

        public static StationDecisionDataSchemaMetadata ValidateDocumentPair(string json, string html)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(html))
                throw new InvalidDataException("The packaged data schema is empty.");
            var header = JsonUtility.FromJson<Header>(json.TrimStart('\uFEFF'));
            if (header == null || header.schemaVersion != SchemaVersion || string.IsNullOrEmpty(header.canonicalContract) ||
                header.contractFingerprint != ComputeFingerprint(header.canonicalContract))
                throw new InvalidDataException("The packaged data schema version or contract fingerprint is invalid.");
            if (!html.Contains("name=\"data-contract-fingerprint\" content=\"" + header.contractFingerprint + "\"") ||
                !html.Contains("name=\"data-schema-version\" content=\"" + SchemaVersion + "\"") || !html.Contains("<html lang=\"fr\""))
                throw new InvalidDataException("The French HTML and JSON data contracts do not match.");
            return new StationDecisionDataSchemaMetadata { schemaVersion = header.schemaVersion, contractFingerprint = header.contractFingerprint,
                htmlFile = RelativeDirectory + "/" + HtmlFileName, jsonFile = RelativeDirectory + "/" + JsonFileName,
                readerFile = RelativeDirectory + "/" + ReaderFileName, readerReadmeFile = RelativeDirectory + "/" + ReaderReadmeFileName };
        }

        public static string ComputeFingerprint(string canonicalContract)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(canonicalContract ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        [Serializable] sealed class Header
        { public string schemaVersion, contractFingerprint, canonicalContract; }
    }
}
