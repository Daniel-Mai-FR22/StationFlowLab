using System;
using UnityEngine;

namespace StationFlow.SyntheticTraffic
{
    /// <summary>Independent expected landmarks and inverse-ray round trips for the sensor calibration.</summary>
    public static class SensorProjectionChecks
    {
        public static int RunOrThrow()
        {
            int checkedPoints = 0;
            Check(new Vector3(0, 0, 10), false, 480, 270); ++checkedPoints;
            Check(new Vector3(0, 0, 10), true, 320, 320); ++checkedPoints;
            Check(new Vector3(10, 0, 10), true, 480, 320); ++checkedPoints;
            Check(new Vector3(-10, 0, 10), true, 160, 320); ++checkedPoints;
            Check(new Vector3(0, 10, 10), true, 320, 160); ++checkedPoints;
            Check(new Vector3(0, -10, 10), true, 320, 480); ++checkedPoints;
            Check(new Vector3(10, 0, 0), true, 640, 320); ++checkedPoints;
            Check(new Vector3(0, 10, 0), true, 320, 0); ++checkedPoints;
            Check(new Vector3(0, 10 * Mathf.Tan(35 * Mathf.Deg2Rad), 10), false, 480, 0); ++checkedPoints;
            Check(new Vector3(10 * Mathf.Tan(35 * Mathf.Deg2Rad) * 960 / 540, 0, 10), false, 960, 270); ++checkedPoints;
            for (int row = 1; row < 16; ++row) for (int column = 1; column < 16; ++column)
            {
                double x = (column - 8) / 8.0;
                double y = (8 - row) / 8.0;
                double radius = Math.Sqrt(x * x + y * y);
                if (radius > .98) continue;
                double theta = radius * Math.PI / 2;
                double scale = radius < 1e-10 ? 0 : Math.Sin(theta) / radius;
                var ray = new Vector3((float)(x * scale), (float)(y * scale), (float)Math.Cos(theta));
                Check(ray * 10, true, column * 40, row * 40);
                ++checkedPoints;
            }
            Vector2 ignored;
            Vector3[] invalid = { Vector3.zero, Vector3.back, new Vector3(float.NaN, 1, 1), new Vector3(float.PositiveInfinity, 1, 1) };
            foreach (Vector3 value in invalid) for (int sensor = 0; sensor < 2; ++sensor)
            {
                if (CameraRig.ProjectCameraLocal(value, sensor == 1, 640, 640, 70, .15f, 180, out ignored))
                    throw new InvalidOperationException("Projection accepted an invalid/behind-camera point: " + value);
                ++checkedPoints;
            }
            return checkedPoints;
        }

        static void Check(Vector3 point, bool fisheye, float expectedX, float expectedY)
        {
            Vector2 actual;
            if (!CameraRig.ProjectCameraLocal(point, fisheye, fisheye ? 640 : 960, fisheye ? 640 : 540, 70, .15f, 180, out actual)
                || Mathf.Abs(actual.x - expectedX) > .002f || Mathf.Abs(actual.y - expectedY) > .002f)
                throw new InvalidOperationException("Projection check failed: " + point + " expected " + expectedX + "," + expectedY + " actual " + actual);
        }
    }
}
