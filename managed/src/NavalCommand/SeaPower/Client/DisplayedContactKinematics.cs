using System;
using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;

// Pure mapping of the inputs actually bound by ContactBaseViewModel(Vehicle).
// Classification and dormancy do not gate those native bindings.
internal static class DisplayedContactKinematics
{
    internal enum VerticalDisplay
    {
        Unavailable,
        AltitudeOrDepth,
        TorpedoDepth
    }

    internal static void Capture(ObservationKinematics target, PartialPosition position, bool hasObject,
        bool hasSpeedDisplay, double east, double vertical, double north, double? altitudeFeet, VerticalDisplay verticalDisplay)
    {
        const double MetersPerUnityUnit = 67.200066;
        const double MetersPerFoot = 0.3048;
        if (!hasObject)
        {
            return;
        }
        if (IsFinite(east) && IsFinite(vertical) && IsFinite(north))
        {
            if (hasSpeedDisplay)
            {
                // The panel uses full vector magnitude, including vertical motion.
                target.SpeedMetersPerSecond = Math.Sqrt(east * east + vertical * vertical + north * north) * MetersPerUnityUnit;
            }
            if (position.HasLatitudeDegrees)
            {
                // Observation heading preserves the panel's reported track heading, derived from movement.
                // It does not establish body orientation. Heading and course share this source and
                // must not be interpreted as independent measurements by downstream controllers.
                // atan2(0, 0) displays north; a zero vector supplies no physical course.
                target.HeadingRadians = NativeKinematicsMapping.GeographicBearing(east, north, position.LatitudeDegrees);
                if (east != 0.0 || north != 0.0)
                {
                    target.CourseRadians = target.HeadingRadians;
                }
            }
        }
        if (verticalDisplay != VerticalDisplay.Unavailable && altitudeFeet.HasValue && IsFinite(altitudeFeet.Value))
        {
            double displayedAltitudeFeet = verticalDisplay == VerticalDisplay.TorpedoDepth
                ? Math.Min(altitudeFeet.Value, 0.0) : altitudeFeet.Value;
            position.ElevationMeters = displayedAltitudeFeet * MetersPerFoot;
        }
    }

    static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
