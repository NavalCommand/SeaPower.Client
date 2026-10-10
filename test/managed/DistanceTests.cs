using System;
using NavalCommand.SeaPower.Client;

internal static class DistanceTests
{
    private const double DistanceToleranceMeters = 0.000001;
    public static void Run()
    {
        // Expected values were captured from the pre-refactor .NET provider binary.
        // The equatorial case also corresponds to one degree on a 6,371 km sphere.
        CheckDistance("coincident", 0, 0, 0, 0, 0);
        CheckDistance("one_degree_equator", 0, 0, 0, 1, 111_194.92664455874);
        CheckDistance("typical_waypoint", 61.96, -10.37, 62, -10.37, 4_447.797065782254);
        CheckDistance("date_line", 10, 179.9, 10, -179.9, 21_901.124835754712);
        CheckDistance("near_north_pole", 89.999, 0, 89.999, 180, 222.38985328931798);
        CheckDistance("near_south_pole", -89.999, 45, -89.999, -135, 222.38985328931798);
        CheckDistance("near_antipodal", 0, 0, 0, 179.999999, 20_015_086.79602057);
    }

    private static void CheckDistance(string label, double startLatitudeDegrees, double startLongitudeDegrees, double endLatitudeDegrees,
        double endLongitudeDegrees, double expectedDistanceMeters)
    {
        double distanceMeters = Provider.Distance(la: startLatitudeDegrees, lo: startLongitudeDegrees, lb: endLatitudeDegrees, lob: endLongitudeDegrees);
        double reverseDistanceMeters = Provider.Distance(endLatitudeDegrees, endLongitudeDegrees, startLatitudeDegrees, startLongitudeDegrees);
        if (!double.IsFinite(distanceMeters) || distanceMeters < 0 || Math.Abs(distanceMeters - expectedDistanceMeters) > DistanceToleranceMeters
            || Math.Abs(distanceMeters - reverseDistanceMeters) > DistanceToleranceMeters)
        {
            throw new InvalidOperationException($"Distance regression: {label}, expected {expectedDistanceMeters:R} m, got {distanceMeters:R} m");
        }

        Console.WriteLine("PASS distance_" + label);
    }
}
