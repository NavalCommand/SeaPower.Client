using System;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;

static void Check(bool passed, string description)
{
    if (!passed)
    {
        throw new InvalidOperationException(description);
    }
}

// Projecting a corrected bearing must recover the native waypoint grid slope.
foreach (double latitude in new[] { -70.0, -45.0, 0.0, 45.0, 70.0 })
{
    foreach (double headingDegrees in new[] { 0.0, 25.0, 45.0, 90.0, 135.0, 180.0, 225.0, 270.0, 315.0 })
    {
        double gridHeading = headingDegrees * Math.PI / 180.0;
        double bearing = NativeKinematicsMapping.GeographicHeading(headingDegrees, latitude);
        double projectedEast = Math.Sin(bearing) / Math.Cos(latitude * Math.PI / 180.0);
        double projectedNorth = Math.Cos(bearing);
        double crossProduct = projectedEast * Math.Cos(gridHeading) - projectedNorth * Math.Sin(gridHeading);
        Check(Math.Abs(crossProduct) < 1e-12, "geographic bearing projects along the native waypoint direction");
        double course = NativeKinematicsMapping.GeographicBearing(Math.Sin(gridHeading), Math.Cos(gridHeading), latitude);
        Check(Math.Abs(course - bearing) < 1e-12, "heading and velocity use the same grid conversion");
    }
}
Check(Math.Abs(NativeKinematicsMapping.GeographicHeading(45.0, null) - Math.PI / 4.0) < 1e-12,
    "unlocalized observations retain the grid bearing without reading hidden latitude");

var exactCandidates = new[] { new ClassificationCandidate("vessels/ssn", 14) };
var unresolvedExact = ClassificationMapping.ResolveCandidates(exactCandidates, false);
Check(unresolvedExact.Level == ClassificationResolutionLevel.SubCategory && unresolvedExact.Value == 14,
    "singleton observer candidate remains a subcategory before exact identification");
var identifiedExact = ClassificationMapping.ResolveCandidates(exactCandidates, true);
Check(identifiedExact.Level == ClassificationResolutionLevel.TypeId && identifiedExact.TypeId == "vessels/ssn",
    "identified singleton observer candidate resolves to a canonical type ID");
var sameCategory = ClassificationMapping.ResolveCandidates(new[]
{
    new ClassificationCandidate("vessels/ddg", 7),
    new ClassificationCandidate("vessels/ffg", 8)
}, false);
Check(sameCategory.Level == ClassificationResolutionLevel.Category && sameCategory.Value == 2,
    "multiple candidates resolve to their common category");
var sameDimension = ClassificationMapping.ResolveCandidates(new[]
{
    new ClassificationCandidate("aircraft/fighter", 0),
    new ClassificationCandidate("ammunition/weapon", 30)
}, false);
Check(sameDimension.Level == ClassificationResolutionLevel.Dimension && sameDimension.Value == 0,
    "different categories can resolve directly to their common dimension");
Check(ClassificationMapping.ResolveCandidates(new[]
{
    new ClassificationCandidate("vessels/ddg", 7),
    new ClassificationCandidate("unknown", null)
}, false).Level == ClassificationResolutionLevel.Unknown,
    "unmapped observer candidate prevents false consensus");
Check(ClassificationMapping.ResolveVisualType("Submarine").Level == ClassificationResolutionLevel.SubCategory
    && ClassificationMapping.ResolveVisualType("Submarine").Value == 33,
    "aggressive map-symbol fallback emits generic submarine classification");
Check(ClassificationMapping.ResolveVisualType("Surface").Level == ClassificationResolutionLevel.Category
    && ClassificationMapping.ResolveVisualType("Surface").Value == 2,
    "aggressive map-symbol fallback emits generic surface category");
Check(ClassificationMapping.MapAuthoritativeSubCategory("ammunition/usn_moss", "", "", "Torpedo", Array.Empty<string>()) == 21,
    "curated MOSS identity remains a semantic decoy");
Check(ClassificationMapping.MapAuthoritativeSubCategory("ammunition/usn_ssq-53", "", "", "Bomb", Array.Empty<string>()) == 22,
    "sonobuoy identity remains semantic despite bomb implementation");
Check(ClassificationMapping.MapAuthoritativeSubCategory("biologic/bio_blue_whale", "BIO", "Whale", "Biologic", Array.Empty<string>()) == 34,
    "encyclopedia whale type maps to SeaAnimal subcategory");
Check(ClassificationMapping.MapAuthoritativeSubCategory("biologic/bio_fin_whale", "", "", "Biologic", Array.Empty<string>()) == 34,
    "curated whale identity maps without relying on display text");
Check(ClassificationMapping.MapNativeRole("ASuW_LR").SequenceEqual(new[] { PlatformRole.SurfaceWarfare, PlatformRole.Strike }),
    "native ASuW_LR role maps to its configured platform roles");

var game = new FakeGame();
var provider = new Provider(game);
provider.Synchronize();
var session = Publication.Parser.ParseFrom(provider.Capture()).Session;
int request = 0;
Control Command(string client)
{
    return new Control { Client = client, Session = session, Request = (++request).ToString(),
        ExpiresUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds() };
}
void Pause(string client, bool paused)
{
    var control = Command(client);
    control.Pause = paused;
    Check(provider.Apply(control.ToByteArray()).Result.ValueCase == Result.ValueOneofCase.Success, "pause applied");
}
void Factor(double factor)
{
    var control = Command("first");
    control.Compression = factor;
    Check(provider.Apply(control.ToByteArray()).Result.ValueCase == Result.ValueOneofCase.Success, "factor applied");
}

Pause("first", true);
Pause("second", true);
Pause("first", false);
Check(game.Paused, "second client retains pause");
provider.ObservePlayerPause(true);
Pause("second", false);
Check(game.Paused, "player retains pause");
provider.ObservePlayerPause(false);
Pause("first", false);
Check(!game.Paused, "all claims released");

Pause("first", true);
Factor(4);
Check(game.Paused && provider.SelectedCompression == 4 && game.SelectedCompression == 4, "factor selection while paused preserves claim");
provider.ObservePlayerCompression(2);
game.SelectedCompression = 2;
Check(provider.EffectivePause && provider.SelectedCompression == 2, "later native factor wins while external pause remains");
provider.Apply(new Control { Client = "first", Disconnected = new Empty() }.ToByteArray());
Check(!game.Paused && game.SelectedCompression == 2, "disconnect immediately releases claim with latest resume factor");
Pause("reconnected", false);
Check(!game.Paused, "reconnection has fresh ownership");

var unsupported = Command("first");
unsupported.Compression = 99;
var rejected = provider.Apply(unsupported.ToByteArray());
Check(rejected.Result.Failure.ErrorCase == Failure.ErrorOneofCase.Simulation && game.SelectedCompression == 2, "unsupported factor is a domain failure without mutation");
var expired = Command("first");
expired.Pause = true;
expired.ExpiresUnixMilliseconds = 1;
Check(provider.Apply(expired.ToByteArray()).Result.Failure.Infrastructure.Condition == 1 && !game.Paused, "expired queued claim never mutates game");

var old = Command("first");
old.Pause = true;
game.SessionKey = "new native mission";
provider.Synchronize();
Check(provider.Apply(old.ToByteArray()).Result.Failure.Infrastructure.Condition == 4 && !game.Paused, "old mission control rejected");
var retainedBytes = provider.Capture();
Check(Publication.Parser.ParseFrom(retainedBytes).Session != session, "mission gets a new public identity");
game.Ready = false;
provider.Synchronize();
Check(Publication.Parser.ParseFrom(provider.Capture()).Session.Length == 0, "unavailable mission invalidates publication");
Check(Publication.Parser.ParseFrom(retainedBytes).Session.Length != 0, "previous serialized publication remains immutable");

bool wrongThreadRejected = Task.Run(() =>
{
    try
    {
        provider.Capture();
        return false;
    }
    catch (InvalidOperationException exception)
    {
        return exception.Message == "engine_thread_required";
    }
}).GetAwaiter().GetResult();
Check(wrongThreadRejected, "worker cannot access game facade");
Console.WriteLine("Managed provider and classification: ownership, observer consensus, symbol fallback, roles, expiry, session and thread boundary passed.");
return 0;

sealed class FakeGame : IGameFacade
{
    public bool Ready { get; set; } = true;
    public string SessionKey { get; set; } = "native mission";
    public bool Paused { get; private set; }
    public double SelectedCompression { get; set; } = 1;
    public bool SupportsCompression(double factor) => factor == 1 || factor == 2 || factor == 4;
    public void ApplyPause(bool paused) { Paused = paused; }
    public void SelectCompression(double factor, bool remainPaused)
    {
        SelectedCompression = factor;
        Paused = remainPaused;
    }
    public Publication Capture(string session, ulong revision, bool paused, double factor)
    {
        return new Publication { Session = session, Revision = revision };
    }
    public void Reset() { }
}
