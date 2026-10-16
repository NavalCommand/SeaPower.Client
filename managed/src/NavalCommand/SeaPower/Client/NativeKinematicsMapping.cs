using System;

namespace NavalCommand.SeaPower.Client;

// Sea Power's Unity X/Z axes use equally scaled longitude/latitude degrees.
// Domain bearings describe physical east/north directions instead of that grid.
internal static class NativeKinematicsMapping
{
    internal static double GeographicBearing(double gridEast, double gridNorth, double? latitudeDegrees)
    {
        double latitudeRadians = (latitudeDegrees ?? 0.0) * Math.PI / 180.0;
        return Math.Atan2(gridEast * Math.Cos(latitudeRadians), gridNorth);
    }

    internal static double GeographicHeading(double headingDegrees, double? latitudeDegrees)
    {
        double headingRadians = headingDegrees * Math.PI / 180.0;
        return GeographicBearing(Math.Sin(headingRadians), Math.Cos(headingRadians), latitudeDegrees);
    }
}
