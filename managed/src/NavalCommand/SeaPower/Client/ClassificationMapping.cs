using System;
using System.Collections.Generic;
using System.Linq;

namespace NavalCommand.SeaPower.Client;

public enum ClassificationMode
{
    Safe,
    Aggressive
}

// Values intentionally match Classification::Api::Role in Domain.Api.
public enum PlatformRole
{
    AirSuperiority,
    AirDefence,
    Strike,
    LandAttack,
    SurfaceWarfare,
    AntiSubmarineWarfare,
    MineWarfare,
    Reconnaissance,
    MaritimePatrol,
    AirborneEarlyWarning,
    ElectronicWarfare,
    Transport,
    AerialRefuelling,
    Surveillance,
    Decoy
}

public interface IPlatformRoleMappingProvider
{
    bool TryGetRoles(string typeId, out IReadOnlyCollection<PlatformRole> roles);
}

public interface IAlternativeDisplayNameProvider
{
    bool TryGetUnitAlternativeDisplayName(string typeId, out string displayName);
    bool TryGetEquipmentAlternativeDisplayName(string typeId, out string displayName);
}

public enum ClassificationResolutionLevel
{
    Unknown,
    TypeId,
    SubCategory,
    Category,
    Dimension
}

public readonly struct ClassificationResolution
{
    public ClassificationResolutionLevel Level { get; }
    public string TypeId { get; }
    public int Value { get; }

    public ClassificationResolution(ClassificationResolutionLevel level, string typeId, int value)
    {
        Level = level;
        TypeId = typeId;
        Value = value;
    }

    public static ClassificationResolution Unknown => new(ClassificationResolutionLevel.Unknown, "", 0);
}

public readonly struct ClassificationCandidate
{
    public string TypeId { get; }
    public int? SubCategory { get; }

    public ClassificationCandidate(string typeId, int? subCategory)
    {
        TypeId = typeId;
        SubCategory = subCategory;
    }
}

public static class ClassificationMapping
{
    enum Dimension
    {
        Air,
        Land,
        SeaSurface,
        SeaSubsurface,
        Space,
        Other
    }

    enum Category
    {
        Aircraft,
        Missile,
        SurfaceVessel,
        Submarine,
        Torpedo,
        Mine,
        LandEquipment,
        LandInstallation,
        LandUnit,
        SpaceObject,
        SurfaceDevice,
        SubsurfaceDevice,
        SeaAnimal,
        Other
    }

    enum SubCategory
    {
        FixedWingAircraft,
        RotaryWingAircraft,
        TiltRotorAircraft,
        CruiseMissile,
        BallisticMissile,
        AircraftCarrier,
        Cruiser,
        Destroyer,
        Frigate,
        Corvette,
        PatrolVessel,
        AmphibiousVessel,
        AuxiliaryVessel,
        MerchantVessel,
        AttackSubmarine,
        BallisticMissileSubmarine,
        CruiseMissileSubmarine,
        HeavyweightTorpedo,
        LightweightTorpedo,
        NavalMine,
        Noisemaker,
        SelfPropelledAcousticDecoy,
        Sonobuoy,
        GroundVehicle,
        SurfaceToAirMissileLauncher,
        AntiAircraftGun,
        Radar,
        Airfield,
        MilitaryInstallation,
        CivilianInstallation,
        Missile,
        Torpedo,
        SurfaceVessel,
        Submarine,
        Whale,
        Other
    }

    // Adapter interpretation of the native encyclopedia's principal platform type.
    // Never infer priority from the unordered capabilities or current loadout.
    public static PlatformRole? MapPrimaryPlatformRole(string nativeTypeFullName)
    {
        switch ((nativeTypeFullName ?? "").Trim().ToUpperInvariant())
        {
            case "AEW&C": return PlatformRole.AirborneEarlyWarning;
            case "EW AIRCRAFT":
            case "EW/TANKER": return PlatformRole.ElectronicWarfare;
            case "FIGHTER":
            case "FIGHTER BOMBER":
            case "INTERCEPTOR":
            case "VTOL,FIGHTER": return PlatformRole.AirSuperiority;
            case "ATTACK":
            case "ATTACK HELICOPTER": return PlatformRole.Strike;
            case "BOMBER":
            case "BOMBER,STRATEGIC BOMBER": return PlatformRole.LandAttack;
            case "ASW AIRCRAFT":
            case "ASW HELICOPTER": return PlatformRole.AntiSubmarineWarfare;
            case "MARITIME PATROL,MPA":
            case "MPA HELICOPTER": return PlatformRole.MaritimePatrol;
            case "RECON": return PlatformRole.Reconnaissance;
            case "TANKER": return PlatformRole.AerialRefuelling;
            case "TRANSPORT":
            case "VIP TRANSPORT":
            case "HEAVY LIFT HELICOPTER":
            case "MEDIUM LIFT HELICOPTER": return PlatformRole.Transport;
            default: return null;
        }
    }

    static readonly IReadOnlyDictionary<string, SubCategory> EncyclopediaTypes = CreateEncyclopediaTypes();

    static IReadOnlyDictionary<string, SubCategory> CreateEncyclopediaTypes()
    {
        var types = new Dictionary<string, SubCategory>(StringComparer.OrdinalIgnoreCase);
        AddTypes(types, "aircraft", SubCategory.FixedWingAircraft,
            "AEW&C", "Airliner", "ASW Aircraft", "Attack", "Bomber", "Bomber,Strategic Bomber", "EW Aircraft",
            "EW/Tanker", "Fighter", "Fighter Bomber", "General Aviation", "Interceptor", "Maritime Patrol,MPA",
            "MPA Helicopter", "Recon", "Tanker", "Trainer", "Transport", "VIP Transport", "VTOL,Fighter");
        AddTypes(types, "aircraft", SubCategory.RotaryWingAircraft,
            "ASW Helicopter", "Attack Helicopter", "Drone Helicopter", "Heavy Lift Helicopter", "Medium Lift Helicopter",
            "SAR Helicopter", "Utility Helicopter");
        AddTypes(types, "vessels", SubCategory.AircraftCarrier, "Aircraft Carrier", "Light Carrier");
        AddTypes(types, "vessels", SubCategory.Cruiser, "Cruiser", "Light Cruiser", "Missile Cruiser");
        AddTypes(types, "vessels", SubCategory.Destroyer, "Destroyer");
        AddTypes(types, "vessels", SubCategory.Frigate, "Frigate");
        AddTypes(types, "vessels", SubCategory.Corvette, "Corvette");
        AddTypes(types, "vessels", SubCategory.PatrolVessel, "Fast Attack Craft", "Missile Boat", "Patrol Boat", "Torpedo Boat");
        AddTypes(types, "vessels", SubCategory.AmphibiousVessel, "Landing Ship");
        AddTypes(types, "vessels", SubCategory.AuxiliaryVessel, "Fleet Auxiliary", "Minesweeper", "Spy Ship", "Submarine Tender");
        AddTypes(types, "vessels", SubCategory.MerchantVessel,
            "Barge Carrier", "Barge", "Bulk Carrier", "Container Ship", "Fishing Vessel", "General Cargo Ship", "Liner",
            "Roll-on Roll-off", "Tanker", "Vehicle Carrier");
        AddTypes(types, "vessels", SubCategory.AttackSubmarine, "Attack Submarine");
        AddTypes(types, "vessels", SubCategory.BallisticMissileSubmarine, "Ballistic Missile Submarine");
        AddTypes(types, "vessels", SubCategory.CruiseMissileSubmarine, "Cruise Missile Submarine");
        AddTypes(types, "vessels", SubCategory.Submarine, "Patrol Submarine");
        AddTypes(types, "vessels", SubCategory.NavalMine, "Mine");
        AddTypes(types, "land_units", SubCategory.Airfield, "Airbase");
        AddTypes(types, "land_units", SubCategory.AntiAircraftGun, "Anti-Aircraft Artillery", "SPAAG");
        AddTypes(types, "land_units", SubCategory.SurfaceToAirMissileLauncher, "Mobile SAM", "SAM Site");
        AddTypes(types, "land_units", SubCategory.Radar, "Radar", "Radar,ESM");
        AddTypes(types, "land_units", SubCategory.GroundVehicle,
            "APC", "AShM Site", "Coastal Artillery", "IFV", "Light tank", "MBT", "MLRS", "SPA", "SSM Site", "Vehicle");
        AddTypes(types, "land_units", SubCategory.MilitaryInstallation, "Bridge", "Installation", "Observation Post", "Port");
        AddTypes(types, "biologic", SubCategory.Whale, "Whale");
        AddTypes(types, "vessels", SubCategory.Other, "Raft", "SEPTAR");
        return types;
    }

    static void AddTypes(IDictionary<string, SubCategory> types, string folder, SubCategory subCategory, params string[] labels)
    {
        foreach (string label in labels)
        {
            string key = folder + "|" + NormalizeLabel(label);
            if (types.TryGetValue(key, out SubCategory existing) && existing != subCategory)
            {
                throw new InvalidOperationException("ambiguous_encyclopedia_classification:" + label);
            }
            types[key] = subCategory;
        }
    }

    static string NormalizeLabel(string label)
    {
        return new string((label ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    public static string CreateTypeId(string folder, string name)
    {
        string normalizedFolder = (folder ?? "").Trim().Trim('/').ToLowerInvariant();
        string normalizedName = (name ?? "").Trim().Trim('/').ToLowerInvariant();
        if (normalizedFolder.Length == 0 || normalizedName.Length == 0 || normalizedFolder.Contains("..") || normalizedName.Contains(".."))
        {
            return "";
        }
        return normalizedFolder + "/" + normalizedName;
    }

    public static int MapAuthoritativeSubCategory(
        string typeId,
        string typeAbbreviation,
        string typeFullName,
        string nativeObjectType,
        IEnumerable<string> nativeRoles)
    {
        string normalizedTypeId = (typeId ?? "").ToLowerInvariant();
        if (normalizedTypeId == "ammunition/usn_moss" || normalizedTypeId == "ammunition/wp_mg-44" || normalizedTypeId == "ammunition/wp_mg-74")
        {
            return (int)SubCategory.SelfPropelledAcousticDecoy;
        }
        if (normalizedTypeId == "biologic/bio_blue_whale" || normalizedTypeId == "biologic/bio_fin_whale"
            || normalizedTypeId == "biologic/bio_humpback_whale")
        {
            return (int)SubCategory.Whale;
        }
        if (normalizedTypeId.StartsWith("ammunition/", StringComparison.Ordinal)
            && IsSonobuoyIdentity(normalizedTypeId))
        {
            return (int)SubCategory.Sonobuoy;
        }

        string contentFolder = normalizedTypeId.Split('/').FirstOrDefault() ?? "";
        if (TryMapEncyclopediaLabel(contentFolder, typeAbbreviation, out SubCategory abbreviationCategory)
            || TryMapEncyclopediaLabel(contentFolder, typeFullName, out abbreviationCategory))
        {
            return (int)abbreviationCategory;
        }

        var roleNames = new HashSet<string>(nativeRoles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (roleNames.Contains("SeaMine"))
        {
            return (int)SubCategory.NavalMine;
        }

        switch ((nativeObjectType ?? "").Trim())
        {
            case "Aircraft":
                return (int)SubCategory.FixedWingAircraft;
            case "Helicopter":
                return (int)SubCategory.RotaryWingAircraft;
            case "Submarine":
                return (int)SubCategory.Submarine;
            case "Vessel":
                return (int)SubCategory.SurfaceVessel;
            case "Missile":
                return (int)SubCategory.Missile;
            case "Torpedo":
                return (int)SubCategory.Torpedo;
            case "Sonobuoy":
                return (int)SubCategory.Sonobuoy;
            case "Noisemaker":
                return (int)SubCategory.Noisemaker;
            case "Biologic":
                return (int)SubCategory.Other;
            case "LandUnit":
                return (int)SubCategory.Other;
            default:
                return (int)SubCategory.Other;
        }
    }

    static bool TryMapEncyclopediaLabel(string folder, string label, out SubCategory subCategory)
    {
        foreach (string part in (label ?? "").Split(','))
        {
            if (EncyclopediaTypes.TryGetValue(folder + "|" + NormalizeLabel(part), out subCategory))
            {
                return true;
            }
        }
        subCategory = default;
        return false;
    }

    static bool IsSonobuoyIdentity(string typeId)
    {
        return typeId == "ammunition/plan_skf-1" || typeId == "ammunition/plan_skf-2"
            || typeId == "ammunition/usn_ssq-101" || typeId == "ammunition/usn_ssq-41"
            || typeId == "ammunition/usn_ssq-47" || typeId == "ammunition/usn_ssq-53"
            || typeId == "ammunition/usn_ssq-62" || typeId == "ammunition/usn_ssq-77"
            || typeId == "ammunition/wp_rgb-1" || typeId == "ammunition/wp_rgb-3"
            || typeId == "ammunition/wp_rgb-nm1";
    }

    public static IReadOnlyList<PlatformRole> MapNativeRoles(IEnumerable<string> nativeRoles)
    {
        var mapped = new List<PlatformRole>();
        foreach (string nativeRole in nativeRoles ?? Array.Empty<string>())
        {
            foreach (PlatformRole role in MapNativeRole(nativeRole))
            {
                if (!mapped.Contains(role))
                {
                    mapped.Add(role);
                }
            }
        }
        return mapped;
    }

    public static IReadOnlyList<PlatformRole> MapNativeRole(string nativeRole)
    {
        switch ((nativeRole ?? "").Trim())
        {
            case "Deco": return new[] { PlatformRole.Decoy };
            case "SeaMine": return new[] { PlatformRole.MineWarfare };
            case "SmallCivilian": return Array.Empty<PlatformRole>();
            case "Merchant": return Array.Empty<PlatformRole>();
            case "Passenger": return new[] { PlatformRole.Transport };
            case "Transport": return new[] { PlatformRole.Transport };
            case "Landing": return new[] { PlatformRole.Transport };
            case "Minesweep": return new[] { PlatformRole.MineWarfare };
            case "RAS": return new[] { PlatformRole.Transport };
            case "Spy": return new[] { PlatformRole.Reconnaissance, PlatformRole.Surveillance };
            case "FAC": return new[] { PlatformRole.SurfaceWarfare };
            case "ASuW": return new[] { PlatformRole.SurfaceWarfare };
            case "ASuW_LR": return new[] { PlatformRole.SurfaceWarfare, PlatformRole.Strike };
            case "ASuW_VLR": return new[] { PlatformRole.SurfaceWarfare, PlatformRole.Strike };
            case "ASuW_Gun": return new[] { PlatformRole.SurfaceWarfare };
            case "NGFS": return new[] { PlatformRole.LandAttack };
            case "AAW": return new[] { PlatformRole.AirDefence };
            case "ASW": return new[] { PlatformRole.AntiSubmarineWarfare };
            case "Carrier": return Array.Empty<PlatformRole>();
            case "CVS": return Array.Empty<PlatformRole>();
            case "CVL": return Array.Empty<PlatformRole>();
            case "SS": return Array.Empty<PlatformRole>();
            case "SSN": return new[] { PlatformRole.AntiSubmarineWarfare };
            case "SSG": return new[] { PlatformRole.SurfaceWarfare };
            case "SSGN": return new[] { PlatformRole.SurfaceWarfare, PlatformRole.LandAttack };
            case "SSB": return new[] { PlatformRole.Strike };
            case "SSBN": return new[] { PlatformRole.Strike };
            case "Airliner": return new[] { PlatformRole.Transport };
            case "Fighter": return new[] { PlatformRole.AirSuperiority };
            case "Bomber": return new[] { PlatformRole.Strike };
            case "HeavyBomber": return new[] { PlatformRole.Strike };
            case "Recon": return new[] { PlatformRole.Reconnaissance, PlatformRole.Surveillance };
            case "AEW": return new[] { PlatformRole.AirborneEarlyWarning, PlatformRole.Surveillance };
            case "EW": return new[] { PlatformRole.ElectronicWarfare };
            case "MPA": return new[] { PlatformRole.MaritimePatrol, PlatformRole.AntiSubmarineWarfare };
            case "ESM": return new[] { PlatformRole.Surveillance };
            case "SEAD": return new[] { PlatformRole.LandAttack, PlatformRole.ElectronicWarfare };
            case "Attack": return new[] { PlatformRole.Strike, PlatformRole.LandAttack };
            case "Targeting": return new[] { PlatformRole.Surveillance };
            case "SAR": return new[] { PlatformRole.Transport };
            case "Airfield": return Array.Empty<PlatformRole>();
            case "Port": return Array.Empty<PlatformRole>();
            case "Gun": return new[] { PlatformRole.SurfaceWarfare };
            case "SAM": return new[] { PlatformRole.AirDefence };
            case "ASM": return new[] { PlatformRole.SurfaceWarfare };
            case "Radar": return new[] { PlatformRole.Surveillance };
            case "Target": return Array.Empty<PlatformRole>();
            case "LifeRaft": return new[] { PlatformRole.Transport };
            case "None": return Array.Empty<PlatformRole>();
            default: return Array.Empty<PlatformRole>();
        }
    }

    public static ClassificationResolution ResolveCandidates(IReadOnlyCollection<ClassificationCandidate> candidates, bool identified)
    {
        if (candidates == null || candidates.Count == 0 || candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.TypeId) || !candidate.SubCategory.HasValue))
        {
            return ClassificationResolution.Unknown;
        }

        int firstSubCategory = candidates.First().SubCategory.Value;
        int firstCategory = (int)GetCategory((SubCategory)firstSubCategory);
        int firstDimension = (int)GetDimension((Category)firstCategory);
        bool sameType = candidates.All(candidate => string.Equals(candidate.TypeId, candidates.First().TypeId, StringComparison.Ordinal));
        bool sameSubCategory = candidates.All(candidate => candidate.SubCategory.Value == firstSubCategory);
        bool sameCategory = candidates.All(candidate => (int)GetCategory((SubCategory)candidate.SubCategory.Value) == firstCategory);
        bool sameDimension = candidates.All(candidate => (int)GetDimension(GetCategory((SubCategory)candidate.SubCategory.Value)) == firstDimension);

        if (sameType && identified)
        {
            return new ClassificationResolution(ClassificationResolutionLevel.TypeId, candidates.First().TypeId, 0);
        }
        if (sameSubCategory)
        {
            return new ClassificationResolution(ClassificationResolutionLevel.SubCategory, "", firstSubCategory);
        }
        if (sameCategory)
        {
            return new ClassificationResolution(ClassificationResolutionLevel.Category, "", firstCategory);
        }
        if (sameDimension)
        {
            return new ClassificationResolution(ClassificationResolutionLevel.Dimension, "", firstDimension);
        }
        return ClassificationResolution.Unknown;
    }

    public static ClassificationResolution ResolveVisualType(string visualType)
    {
        switch ((visualType ?? "").Trim())
        {
            case "Air": return CategoryResolution(Category.Aircraft);
            case "Helicopter": return SubCategoryResolution(SubCategory.RotaryWingAircraft);
            case "Surface": return CategoryResolution(Category.SurfaceVessel);
            case "Submarine": return SubCategoryResolution(SubCategory.Submarine);
            case "Missile": return SubCategoryResolution(SubCategory.Missile);
            case "Torpedo": return SubCategoryResolution(SubCategory.Torpedo);
            case "Airbase": return SubCategoryResolution(SubCategory.Airfield);
            case "Installation": return CategoryResolution(Category.LandInstallation);
            case "MobileUnit": return CategoryResolution(Category.LandUnit);
            case "AAA": return SubCategoryResolution(SubCategory.AntiAircraftGun);
            case "MobileAAA": return SubCategoryResolution(SubCategory.AntiAircraftGun);
            case "SAM": return SubCategoryResolution(SubCategory.SurfaceToAirMissileLauncher);
            case "Radar": return SubCategoryResolution(SubCategory.Radar);
            case "Port": return CategoryResolution(Category.LandInstallation);
            case "Bridge": return CategoryResolution(Category.LandInstallation);
            case "AirDecoy": return CategoryResolution(Category.Other);
            case "SubDecoy": return SubCategoryResolution(SubCategory.SelfPropelledAcousticDecoy);
            case "ActiveSonobouy": return SubCategoryResolution(SubCategory.Sonobuoy);
            case "PassiveSonobouy": return SubCategoryResolution(SubCategory.Sonobuoy);
            case "Biologic": return CategoryResolution(Category.SeaAnimal);
            default: return ClassificationResolution.Unknown;
        }
    }

    static ClassificationResolution SubCategoryResolution(SubCategory subCategory)
    {
        return new ClassificationResolution(ClassificationResolutionLevel.SubCategory, "", (int)subCategory);
    }

    static ClassificationResolution CategoryResolution(Category category)
    {
        return new ClassificationResolution(ClassificationResolutionLevel.Category, "", (int)category);
    }

    static Category GetCategory(SubCategory subCategory)
    {
        switch (subCategory)
        {
            case SubCategory.FixedWingAircraft:
            case SubCategory.RotaryWingAircraft:
            case SubCategory.TiltRotorAircraft: return Category.Aircraft;
            case SubCategory.CruiseMissile:
            case SubCategory.BallisticMissile:
            case SubCategory.Missile: return Category.Missile;
            case SubCategory.AircraftCarrier:
            case SubCategory.Cruiser:
            case SubCategory.Destroyer:
            case SubCategory.Frigate:
            case SubCategory.Corvette:
            case SubCategory.PatrolVessel:
            case SubCategory.AmphibiousVessel:
            case SubCategory.AuxiliaryVessel:
            case SubCategory.MerchantVessel:
            case SubCategory.SurfaceVessel: return Category.SurfaceVessel;
            case SubCategory.AttackSubmarine:
            case SubCategory.BallisticMissileSubmarine:
            case SubCategory.CruiseMissileSubmarine:
            case SubCategory.Submarine: return Category.Submarine;
            case SubCategory.HeavyweightTorpedo:
            case SubCategory.LightweightTorpedo:
            case SubCategory.Torpedo: return Category.Torpedo;
            case SubCategory.NavalMine: return Category.Mine;
            case SubCategory.Noisemaker:
            case SubCategory.SelfPropelledAcousticDecoy: return Category.SubsurfaceDevice;
            case SubCategory.Sonobuoy: return Category.SurfaceDevice;
            case SubCategory.GroundVehicle:
            case SubCategory.SurfaceToAirMissileLauncher:
            case SubCategory.AntiAircraftGun:
            case SubCategory.Radar: return Category.LandEquipment;
            case SubCategory.Airfield:
            case SubCategory.MilitaryInstallation:
            case SubCategory.CivilianInstallation: return Category.LandInstallation;
            case SubCategory.Whale: return Category.SeaAnimal;
            case SubCategory.Other: return Category.Other;
            default: throw new ArgumentOutOfRangeException(nameof(subCategory));
        }
    }

    static Dimension GetDimension(Category category)
    {
        switch (category)
        {
            case Category.Aircraft:
            case Category.Missile: return Dimension.Air;
            case Category.SurfaceVessel:
            case Category.SurfaceDevice: return Dimension.SeaSurface;
            case Category.Submarine:
            case Category.Torpedo:
            case Category.Mine:
            case Category.SubsurfaceDevice:
            case Category.SeaAnimal: return Dimension.SeaSubsurface;
            case Category.LandEquipment:
            case Category.LandInstallation:
            case Category.LandUnit: return Dimension.Land;
            case Category.SpaceObject: return Dimension.Space;
            case Category.Other: return Dimension.Other;
            default: throw new ArgumentOutOfRangeException(nameof(category));
        }
    }
}
