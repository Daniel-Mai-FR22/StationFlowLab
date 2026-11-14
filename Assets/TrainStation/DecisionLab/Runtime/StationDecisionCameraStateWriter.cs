using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Owned by one ordered camera worker. Only the last exact serialized state is retained.
    /// A state is flushed before its ID can be written into a frame row.</summary>
    public sealed class StationDecisionCameraStateWriter
    {
        public const string FileName = "camera-states.jsonl";
        string previousCameraJson;
        long stateId, previousSyncSampleId;
        public long StateCount => stateId;

        public long Reference(TextWriter states, long syncSampleId, string cameraJson)
        {
            if (states == null) throw new ArgumentNullException(nameof(states));
            if (syncSampleId <= 0 || syncSampleId < previousSyncSampleId) throw new ArgumentOutOfRangeException(nameof(syncSampleId));
            if (string.IsNullOrEmpty(cameraJson) || cameraJson[0] != '{' || cameraJson[cameraJson.Length - 1] != '}')
                throw new ArgumentException("A serialized camera object is required.", nameof(cameraJson));
            if (string.Equals(previousCameraJson, cameraJson, StringComparison.Ordinal))
            { previousSyncSampleId = syncSampleId; return stateId; }
            long next = checked(stateId + 1);
            states.WriteLine("{\"cameraStateId\":" + Number(next) + ",\"effectiveFromSyncSampleId\":" + Number(syncSampleId) + ",\"camera\":" + cameraJson + "}");
            states.Flush(); // Publication fence: no referencing frame is emitted if writing or flushing fails.
            previousCameraJson = cameraJson; stateId = next; previousSyncSampleId = syncSampleId;
            return stateId;
        }

        public static string FrameLine(int segment, int videoFrame, double videoTimeSeconds, long requestId,
            long syncSampleId, long cameraStateId, StationDecisionVideoRateSample rate)
        {
            if (segment < 1 || videoFrame < 0 || requestId < 0 || syncSampleId <= 0 || cameraStateId <= 0)
                throw new ArgumentOutOfRangeException(nameof(videoFrame));
            var text = new StringBuilder(320);
            text.Append("{\"segment\":").Append(Number(segment)).Append(",\"videoFrame\":").Append(Number(videoFrame))
                .Append(",\"videoTimeSeconds\":").Append(Number(videoTimeSeconds)).Append(",\"requestId\":").Append(Number(requestId))
                .Append(",\"captureIntervalRealSeconds\":").Append(NullableNumber(rate.intervalRealSeconds))
                .Append(",\"captureFpsInstantaneous\":").Append(NullableNumber(rate.fpsInstantaneous))
                .Append(",\"captureFpsRolling1s\":").Append(Number(rate.rollingFps1s))
                .Append(",\"syncSampleId\":").Append(Number(syncSampleId)).Append(",\"cameraStateId\":").Append(Number(cameraStateId)).Append('}');
            return text.ToString();
        }
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
        static string NullableNumber(double? value) => value.HasValue ? Number(value.Value) : "null";
    }
}
