using System;
using System.IO;
using System.Text;
using StationFlow.TrainStation.DecisionLab;

static class Program
{
    static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]), fixture = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        CheckStorage(output);
        string video = Path.Combine(output, "streaming-mjpeg.avi");
        if (File.Exists(video)) File.Delete(video);
        if (File.Exists(video + ".index.tmp")) File.Delete(video + ".index.tmp");
        byte[] jpeg = File.ReadAllBytes(fixture);
        var commented = new byte[jpeg.Length + 5];
        Array.Copy(jpeg, 0, commented, 0, jpeg.Length - 2);
        Array.Copy(new byte[] { 0xff, 0xfe, 0, 3, 65, 0xff, 0xd9 }, 0, commented, jpeg.Length - 2, 7);
        using (var writer = new StationMjpegAviWriter(video, 320, 180, 5))
        {
            for (int i = 0; i < 15; ++i) if (writer.AppendJpeg(i % 2 == 0 ? jpeg : commented) != i) throw new Exception("Frame numbering is discontinuous.");
            try { writer.AppendJpeg(new byte[] { 0xff, 0xd8, 0, 0 }); throw new Exception("Truncated JPEG was accepted."); } catch (ArgumentException) { }
            writer.Checkpoint();
            using (var input = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(input))
            {
                input.Position = 4;
                if (reader.ReadUInt32() != input.Length - 8) throw new Exception("Checkpoint RIFF size is wrong.");
                input.Position = 48;
                if (reader.ReadUInt32() != 15) throw new Exception("Checkpoint total frame count is wrong.");
            }
        }
        if (File.Exists(video + ".index.tmp")) throw new Exception("Successful close did not clean the index journal.");
        using (var input = File.OpenRead(video))
        using (var reader = new BinaryReader(input))
        {
            input.Position = input.Length - 15 * 16 - 8;
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "idx1" || reader.ReadUInt32() != 15 * 16) throw new Exception("AVI index is missing.");
            int offset = 4;
            for (int i = 0; i < 15; ++i)
            {
                int length = (i % 2 == 0 ? jpeg : commented).Length;
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "00dc" || reader.ReadUInt32() != 0x10 || reader.ReadUInt32() != offset || reader.ReadUInt32() != length)
                    throw new Exception("Frame offset/index content is wrong.");
                offset += length + 8 + (length & 1);
            }
        }
        try { using var writer = new StationMjpegAviWriter(video, 320, 180, 5); throw new Exception("Existing evidence file was overwritten."); } catch (IOException) { }
        Console.WriteLine("PASS: finalized AVI, checkpoint prefix, frame counts and index offsets, invalid-JPEG rejection, evidence overwrite protection, bounded index journal.");
        return 0;
    }

    static void CheckStorage(string output)
    {
        string original = Environment.CurrentDirectory;
        string first = Path.Combine(output, "Portable A", "Station_Data"), second = Path.Combine(output, "Moved Station", "Station_Data");
        string expectedA = Path.Combine(output, "Portable A", "Captures", "TrainStationDaily"), expectedB = Path.Combine(output, "Moved Station", "Captures", "TrainStationDaily");
        if (StationDecisionCaptureStorage.ResolveRoot(first) != expectedA || StationDecisionCaptureStorage.ResolveRoot(second) != expectedB)
            throw new Exception("Relocating the player did not relocate its capture directory.");
        try
        {
            Environment.CurrentDirectory = output;
            if (StationDecisionCaptureStorage.ResolveRoot(first) != expectedA) throw new Exception("Storage depends on launch working directory.");
        }
        finally { Environment.CurrentDirectory = original; }
        foreach (string invalid in new[] { "", "Station_Data", ".", "C:Station_Data" })
        {
            try { StationDecisionCaptureStorage.ResolveRoot(invalid); throw new Exception("A relative data path was accepted: " + invalid); }
            catch (ArgumentException) { }
        }
        string mac = Path.Combine(output, "Portable Mac", "Station.app", "Contents");
        if (StationDecisionCaptureStorage.ResolveRoot(mac, true) != Path.Combine(output, "Portable Mac", "Captures", "TrainStationDaily"))
            throw new Exception("The macOS bundle stored captures inside its application package.");
        Console.WriteLine("PASS: portable storage relocates with absolute player path, preserves spaces, ignores launch working directory, rejects relative paths, and stores beside macOS bundle.");
    }
}
