namespace HouseConsensus.Server.Scoring;

public static class FamilyScorer
{
    public const string RuleVersion = "family-score-native-v1";

    private const int PrivacyWeight = 30;
    private const int KidsSpaceWeight = 20;
    private const int GardenWeight = 20;
    private const int SharedLivingWeight = 15;
    private const int PracticalWeight = 15;

    public static FamilyScoreContract Score(FamilyScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var privacy = ScorePrivacy(input.Privacy);
        var kids = ScoreKidsSpace(input.KidsSpace);
        var garden = ScoreGarden(input.Garden);
        var shared = ScoreSharedLiving(input.SharedLiving);
        var practical = ScorePractical(input.Practical);
        Evaluation[] evaluations = [privacy, kids, garden, shared, practical];

        var blockers = evaluations
            .Where(evaluation => evaluation.InvalidReason is not null)
            .Select(evaluation => evaluation.InvalidReason!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (input.BrokerContradiction)
            blockers.Add("broker_contradiction");

        var coverage = evaluations
            .Where(evaluation => evaluation.Result.Available)
            .Sum(evaluation => evaluation.Result.Weight);

        var status = blockers.Count > 0
            ? HouseConsensus.Shared.ScoreStatus.NeedsReview
            : coverage < 100
                ? HouseConsensus.Shared.ScoreStatus.Incomplete
                : HouseConsensus.Shared.ScoreStatus.Complete;

        double? total = null;
        if (status == HouseConsensus.Shared.ScoreStatus.Complete)
        {
            total = Math.Round(
                privacy.Result.Score!.Value * PrivacyWeight / 100
                + kids.Result.Score!.Value * KidsSpaceWeight / 100
                + garden.Result.Score!.Value * GardenWeight / 100
                + shared.Result.Score!.Value * SharedLivingWeight / 100
                + practical.Result.Score!.Value * PracticalWeight / 100,
                1,
                MidpointRounding.AwayFromZero);
        }

        if (status == HouseConsensus.Shared.ScoreStatus.Incomplete)
        {
            blockers.AddRange(evaluations
                .Where(evaluation => !evaluation.Result.Available)
                .Select(evaluation => $"incomplete_{evaluation.Result.Name}_evidence"));
        }

        return new FamilyScoreContract(
            status,
            total,
            privacy.Result,
            kids.Result,
            garden.Result,
            shared.Result,
            practical.Result,
            coverage,
            RuleVersion,
            blockers);
    }

    private static Evaluation ScorePrivacy(PrivacyScoreInput input)
    {
        if (input is null)
            return Invalid("privacy", PrivacyWeight, "invalid_privacy_evidence");
        if (!Valid(input.Status))
            return Invalid("privacy", PrivacyWeight, "invalid_privacy_evidence");
        if (input.Status != EvidenceAssessmentStatus.Complete)
            return Unavailable("privacy", PrivacyWeight, input.Status);
        if (!Valid(input.SplitType) || input.SplitType == DwellingSplitType.Unknown
            || input.SeparateEntrance is null
            || input.SecondKitchen is null
            || input.InternalConnection is null
            || input.TwoDwellings is null
            || input.EnsuiteCount is null
            || input.BathroomCount is null
            || input.StaircaseCount is null
            || InvalidCount(input.EnsuiteCount)
            || InvalidCount(input.BathroomCount)
            || InvalidCount(input.StaircaseCount))
            return Invalid("privacy", PrivacyWeight, "invalid_privacy_evidence");

        var points = 0d;
        var notes = new List<string>();
        if (input.SeparateEntrance is true)
        {
            points += 35;
            notes.Add("separate_entrance:+35");
        }
        if (input.SecondKitchen is true)
        {
            points += 25;
            notes.Add("second_kitchen:+25");
        }

        var independentUnits = input.SeparateEntrance is true
            || input.SecondKitchen is true
            || input.TwoDwellings is true
            || input.SplitType is DwellingSplitType.Horizontal
                or DwellingSplitType.Vertical
                or DwellingSplitType.SideBySide;
        if (input.InternalConnection is true)
        {
            points -= 15;
            notes.Add("internal_connection:-15");
        }
        else if (input.InternalConnection is false && independentUnits)
        {
            points += 8;
            notes.Add("no_internal_connection:+8");
        }

        if (input.SplitType is DwellingSplitType.Horizontal
            or DwellingSplitType.Vertical
            or DwellingSplitType.SideBySide)
        {
            points += 15;
            notes.Add($"split_{input.SplitType.ToString().ToLowerInvariant()}:+15");
        }

        if (input.EnsuiteCount >= 2)
        {
            points += 12;
            notes.Add("ensuites_2_plus:+12");
        }
        else if (input.EnsuiteCount == 1)
        {
            points += 5;
            notes.Add("ensuites_1:+5");
        }
        else if (input.EnsuiteCount == 0 && input.BathroomCount >= 3)
        {
            points += 8;
            notes.Add("bathrooms_3_plus:+8");
        }
        else if (input.EnsuiteCount == 0 && input.BathroomCount == 2)
        {
            points += 4;
            notes.Add("bathrooms_2:+4");
        }

        if (input.StaircaseCount >= 2)
        {
            points += 8;
            notes.Add("staircases_2_plus:+8");
        }

        return Available("privacy", PrivacyWeight, points, notes);
    }

    private static Evaluation ScoreKidsSpace(KidsSpaceScoreInput input)
    {
        if (input is null)
            return Invalid("kids_space", KidsSpaceWeight, "invalid_kids_space_evidence");
        if (!Valid(input.Status))
            return Invalid("kids_space", KidsSpaceWeight, "invalid_kids_space_evidence");
        if (input.Status != EvidenceAssessmentStatus.Complete)
            return Unavailable("kids_space", KidsSpaceWeight, input.Status);
        if (input.Rooms is null || input.HousingAreaM2 is null
            || input.Basement is null || input.Floors is null
            || input.Storage is null || input.UtilityRoom is null
            || InvalidCount(input.Rooms) || InvalidNumber(input.HousingAreaM2)
            || InvalidCount(input.Floors))
            return Invalid("kids_space", KidsSpaceWeight, "invalid_kids_space_evidence");

        var points = input.Rooms switch
        {
            >= 8 => 40d,
            >= 6 => 28d,
            >= 5 => 18d,
            >= 4 => 10d,
            _ => 0d
        };
        points += input.HousingAreaM2 switch
        {
            >= 220 => 30,
            >= 180 => 22,
            >= 150 => 15,
            >= 120 => 8,
            _ => 0
        };
        if (input.Basement is true) points += 12;
        if (input.Floors >= 2) points += 8;
        if (input.Storage is true) points += 10;
        if (input.UtilityRoom is true) points += 5;

        return Available("kids_space", KidsSpaceWeight, points,
            [$"rooms:{input.Rooms}", $"housing_area_m2:{input.HousingAreaM2}"]);
    }

    private static Evaluation ScoreGarden(GardenScoreInput input)
    {
        if (input is null)
            return Invalid("garden", GardenWeight, "invalid_garden_evidence");
        if (!Valid(input.Status))
            return Invalid("garden", GardenWeight, "invalid_garden_evidence");
        if (input.Status != EvidenceAssessmentStatus.Complete)
            return Unavailable("garden", GardenWeight, input.Status);
        if (input.LotAreaM2 is null || input.HasStructure is null
            || input.PrivateZones is null || input.Terrace is null || input.HasGarden is null
            || InvalidNumber(input.LotAreaM2))
            return Invalid("garden", GardenWeight, "invalid_garden_evidence");

        var points = input.LotAreaM2 switch
        {
            >= 1200 => 70d,
            >= 800 => 58d,
            >= 600 => 46d,
            >= 400 => 35d,
            >= 200 => 20d,
            > 0 => 8d,
            _ => 0d
        };
        if (input.HasStructure is true) points += 10;
        if (input.PrivateZones is true) points += 12;
        if (input.Terrace is true) points += 8;
        if (input.HasGarden is true && (input.LotAreaM2 ?? 0) == 0) points += 15;

        return Available("garden", GardenWeight, points,
            [$"lot_area_m2:{input.LotAreaM2?.ToString() ?? "unknown"}"]);
    }

    private static Evaluation ScoreSharedLiving(SharedLivingScoreInput input)
    {
        if (input is null)
            return Invalid("shared_living", SharedLivingWeight, "invalid_shared_living_evidence");
        if (!Valid(input.Status) || !Valid(input.KitchenSize) || !Valid(input.Condition))
            return Invalid("shared_living", SharedLivingWeight, "invalid_shared_living_evidence");
        if (input.Status != EvidenceAssessmentStatus.Complete)
            return Unavailable("shared_living", SharedLivingWeight, input.Status);
        if (input.OpenPlan is null || input.DiningCapacity is null
            || input.KitchenSize == KitchenSize.Unknown
            || input.Condition == PropertyCondition.Unknown
            || InvalidCount(input.DiningCapacity))
            return Invalid("shared_living", SharedLivingWeight, "invalid_shared_living_evidence");

        var points = input.OpenPlan is true ? 30d : 0d;
        points += input.DiningCapacity switch { >= 8 => 20, >= 6 => 12, _ => 0 };
        points += input.KitchenSize switch { KitchenSize.Large => 20, KitchenSize.Medium => 12, _ => 0 };
        points += input.Condition switch
        {
            PropertyCondition.Excellent => 10,
            PropertyCondition.Good => 6,
            PropertyCondition.Fair => 2,
            _ => 0
        };

        return Available("shared_living", SharedLivingWeight, points,
            [$"open_plan:{input.OpenPlan?.ToString() ?? "unknown"}"]);
    }

    private static Evaluation ScorePractical(PracticalScoreInput input)
    {
        if (input is null)
            return Invalid("practical", PracticalWeight, "invalid_practical_evidence");
        if (!Valid(input.Status))
            return Invalid("practical", PracticalWeight, "invalid_practical_evidence");
        if (input.Status != EvidenceAssessmentStatus.Complete)
            return Unavailable("practical", PracticalWeight, input.Status);
        var energy = input.EnergyLabel?.Trim().Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (input.ParkingCount is null || input.GroundFloorBedroom is null
            || input.GarageOnPlan is null || input.GarageVisible is null
            || input.ToiletCount is null || string.IsNullOrWhiteSpace(energy)
            || energy is not ("A2020" or "A2015" or "A2010" or "A" or "B" or "C" or "D" or "E" or "F" or "G")
            || InvalidCount(input.ParkingCount) || InvalidCount(input.ToiletCount))
            return Invalid("practical", PracticalWeight, "invalid_practical_evidence");

        var noises = new[]
        {
            (Name: "road", Observation: input.RoadNoise),
            (Name: "rail", Observation: input.RailNoise),
            (Name: "air", Observation: input.AirNoise)
        };
        if (noises.Any(noise => noise.Observation is null
            || !Valid(noise.Observation.Status)
            || noise.Observation.Status == NoiseEvidenceStatus.Covered
                && (noise.Observation.Decibels is null
                    || !double.IsFinite(noise.Observation.Decibels.Value)
                    || noise.Observation.Decibels is < 0 or > 200)
            || noise.Observation.Status != NoiseEvidenceStatus.Covered
                && noise.Observation.Decibels is not null))
            return Invalid("practical", PracticalWeight, "invalid_practical_evidence");
        if (noises.Any(noise => noise.Observation.Status is NoiseEvidenceStatus.Unavailable
            or NoiseEvidenceStatus.Error or NoiseEvidenceStatus.Stale))
            return Unavailable("practical", PracticalWeight, EvidenceAssessmentStatus.Unavailable);

        var points = input.ParkingCount switch { >= 2 => 30d, 1 => 10d, _ => 0d };
        if (input.ParkingCount == 0 && (input.GarageOnPlan is true || input.GarageVisible is true))
            points += 15;
        if (input.GroundFloorBedroom is true) points += 15;

        var loudest = noises
            .Where(noise => noise.Observation.Status == NoiseEvidenceStatus.Covered)
            .OrderByDescending(noise => noise.Observation.Decibels)
            .FirstOrDefault();
        if (loudest.Observation is null)
        {
            points += 25;
        }
        else if (loudest.Observation.Decibels < 50)
        {
            points += 25;
        }
        else if (loudest.Observation.Decibels < 60)
        {
            points += 10;
        }
        else
        {
            points -= 10;
        }

        points += input.ToiletCount switch { >= 3 => 15, 2 => 8, _ => 0 };
        points += energy switch
        {
            "A2020" => 15,
            "A2015" => 12,
            "A2010" or "A" => 10,
            "B" => 6,
            "C" => 3,
            _ => 0
        };

        var noiseNote = loudest.Observation is null
            ? "noise:no_contour"
            : $"noise:{loudest.Name}={loudest.Observation.Decibels:0.#}";
        return Available("practical", PracticalWeight, points, [noiseNote]);
    }

    private static Evaluation Available(string name, int weight, double score, IReadOnlyList<string> notes) =>
        new(new ScoreDimensionResult(name, Clamp(score), weight, true, notes), null);

    private static Evaluation Unavailable(string name, int weight, EvidenceAssessmentStatus status) =>
        new(new ScoreDimensionResult(name, null, weight, false,
            [$"evidence_status:{status.ToString().ToLowerInvariant()}"]), null);

    private static Evaluation Invalid(string name, int weight, string reason) =>
        new(new ScoreDimensionResult(name, null, weight, false, [reason]), reason);

    private static bool InvalidCount(int? value) => value < 0;
    private static bool InvalidNumber(double? value) => value is < 0 || value is not null && !double.IsFinite(value.Value);
    private static bool Valid<T>(T value) where T : struct, Enum => Enum.IsDefined(value);
    private static double Clamp(double value) => Math.Max(0, Math.Min(100, value));

    private sealed record Evaluation(ScoreDimensionResult Result, string? InvalidReason);
}
