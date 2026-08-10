using HouseConsensus.Shared;

namespace HouseConsensus.Client.Services;

public enum ListingSort { FamilyScore, Newest, PriceLow, PriceHigh, SizeHigh, SizeLow, GardenHigh, YearNewest, CommuteFastest, NewFirst, DaysOnMarket }

public sealed class ListingFilterState
{
    public string Search { get; set; } = "";
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public int? MinArea { get; set; }
    public int? MaxArea { get; set; }
    public int? MinGarden { get; set; }
    public int? MinRooms { get; set; }
    public int? MinYear { get; set; }
    public int? MaxYear { get; set; }
    public int? MaxCommute { get; set; }
    public int? MaxMonthlyExpense { get; set; }
    public int? MaxDaysOnMarket { get; set; }
    public double? MinFamilyScore { get; set; }
    public int? MinPrivacy { get; set; }
    public int? MaxPrivacy { get; set; }
    public bool OnlyPreferred { get; set; }
    public bool OnlyQuiet { get; set; }
    public bool OnlyNew { get; set; }
    public bool OnlyAiAssessed { get; set; }
    public List<string> Municipalities { get; set; } = [];
    public List<string> MultigenFits { get; set; } = [];
    public List<string> BuildableStatuses { get; set; } = [];
    public List<string> Conditions { get; set; } = [];
    public List<string> EnergyLabels { get; set; } = [];
    public List<string> GardenOrientations { get; set; } = [];
    public List<string> FamilyUnits { get; set; } = [];
    public List<AsbestosRoofStatus> AsbestosRoofStatuses { get; set; } = [];
    public bool OnlyAsbestosRoofHumanCorrected { get; set; }
    public List<VoteChoice> VoteChoices { get; set; } = [VoteChoice.Like, VoteChoice.Dislike];
    public ListingSort Sort { get; set; } = ListingSort.FamilyScore;

    public void NormalizeSavedState()
    {
        Search ??= "";
        Municipalities ??= [];
        MultigenFits ??= [];
        BuildableStatuses ??= [];
        Conditions ??= [];
        EnergyLabels ??= [];
        GardenOrientations ??= [];
        FamilyUnits ??= [];
        AsbestosRoofStatuses ??= [];
        VoteChoices ??= [VoteChoice.Like, VoteChoice.Dislike];
    }

    public bool Matches(ListingDto x)
    {
        if (!string.IsNullOrWhiteSpace(Search) &&
            !(x.Address.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
              (x.City?.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false) ||
              (x.PostalCode?.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))) return false;
        if (OnlyPreferred && x.Preferred is not true) return false;
        if (OnlyQuiet && x.Quiet is not true) return false;
        if (OnlyNew && x.IsNew is not true) return false;
        if (OnlyAiAssessed && !x.AiAssessed) return false;
        if (OnlyAsbestosRoofHumanCorrected && !x.AsbestosRoofHumanCorrected) return false;
        if ((AsbestosRoofStatuses?.Count ?? 0) > 0 && !AsbestosRoofStatuses!.Contains(x.EffectiveAsbestosRoofStatus)) return false;
        if (!Range(x.Price, MinPrice, MaxPrice)) return false;
        if (!Range(x.LivingArea, MinArea, MaxArea)) return false;
        if (!Minimum(x.LotArea, MinGarden) || !Minimum(x.Rooms, MinRooms)) return false;
        if (!Range(x.YearBuilt, MinYear, MaxYear)) return false;
        if (!Maximum(x.CommuteMinutes, MaxCommute) || !Maximum(x.MonthlyExpense, MaxMonthlyExpense) || !Maximum(x.DaysOnMarket, MaxDaysOnMarket)) return false;
        if (!Minimum(x.TrustedFamilyFitScore, MinFamilyScore) || !Range(x.PrivacyScore, MinPrivacy, MaxPrivacy)) return false;
        return Category(x.City, Municipalities) && Category(x.MultigenFit, MultigenFits) && Category(x.BuildableStatus, BuildableStatuses) && Category(x.Condition, Conditions) && Category(x.EnergyLabel, EnergyLabels) && Category(x.GardenOrientation, GardenOrientations) && Category(x.FamilyUnits, FamilyUnits);
    }

    public IReadOnlyList<ListingDto> Apply(IEnumerable<ListingDto> source)
    {
        var filtered = source.Where(Matches);
        return Sort switch
        {
            ListingSort.Newest => filtered.OrderByDescending(x => x.ImportedAt ?? DateTimeOffset.MinValue).ToList(),
            ListingSort.PriceLow => filtered.OrderBy(x => x.Price ?? decimal.MaxValue).ToList(),
            ListingSort.PriceHigh => filtered.OrderByDescending(x => x.Price ?? decimal.MinValue).ToList(),
            ListingSort.SizeHigh => filtered.OrderByDescending(x => x.LivingArea ?? int.MinValue).ToList(),
            ListingSort.SizeLow => filtered.OrderBy(x => x.LivingArea ?? int.MaxValue).ToList(),
            ListingSort.GardenHigh => filtered.OrderByDescending(x => x.LotArea ?? int.MinValue).ToList(),
            ListingSort.YearNewest => filtered.OrderByDescending(x => x.YearBuilt ?? int.MinValue).ToList(),
            ListingSort.CommuteFastest => filtered.OrderBy(x => x.CommuteMinutes ?? int.MaxValue).ToList(),
            ListingSort.NewFirst => filtered.OrderByDescending(x => x.IsNew is true).ThenByDescending(x => x.TrustedFamilyFitScore).ToList(),
            ListingSort.DaysOnMarket => filtered.OrderBy(x => x.DaysOnMarket ?? int.MaxValue).ToList(),
            _ => filtered.OrderByDescending(x => x.TrustedFamilyFitScore).ToList(),
        };
    }

    public ListingFilterState Clone() => new()
    {
        Search = Search, MinPrice = MinPrice, MaxPrice = MaxPrice, MinArea = MinArea, MaxArea = MaxArea,
        MinGarden = MinGarden, MinRooms = MinRooms, MinYear = MinYear, MaxYear = MaxYear,
        MaxCommute = MaxCommute, MaxMonthlyExpense = MaxMonthlyExpense, MaxDaysOnMarket = MaxDaysOnMarket,
        MinFamilyScore = MinFamilyScore, MinPrivacy = MinPrivacy, MaxPrivacy = MaxPrivacy,
        OnlyPreferred = OnlyPreferred, OnlyQuiet = OnlyQuiet, OnlyNew = OnlyNew, OnlyAiAssessed = OnlyAiAssessed,
        Municipalities = [.. Municipalities], MultigenFits = [.. MultigenFits], BuildableStatuses = [.. BuildableStatuses], Conditions = [.. Conditions],
        EnergyLabels = [.. EnergyLabels], GardenOrientations = [.. GardenOrientations], FamilyUnits = [.. FamilyUnits], AsbestosRoofStatuses = [.. (AsbestosRoofStatuses ?? [])], OnlyAsbestosRoofHumanCorrected = OnlyAsbestosRoofHumanCorrected, VoteChoices = [.. VoteChoices], Sort = Sort,
    };

    public int ActiveCount => new object?[] { string.IsNullOrWhiteSpace(Search) ? null : Search, MinPrice, MaxPrice, MinArea, MaxArea, MinGarden, MinRooms, MinYear, MaxYear, MaxCommute, MaxMonthlyExpense, MaxDaysOnMarket, MinFamilyScore, MinPrivacy, MaxPrivacy }.Count(x => x is not null)
        + new[] { Municipalities, MultigenFits, BuildableStatuses, Conditions, EnergyLabels, GardenOrientations, FamilyUnits }.Count(x => x.Count > 0)
        + ((AsbestosRoofStatuses?.Count ?? 0) > 0 ? 1 : 0)
        + new[] { OnlyPreferred, OnlyQuiet, OnlyNew, OnlyAiAssessed, OnlyAsbestosRoofHumanCorrected }.Count(x => x);

    private static bool Category(string? value, List<string> selected) => selected.Count == 0 || (value is not null && selected.Contains(value, StringComparer.OrdinalIgnoreCase));
    private static bool Range<T>(T? value, T? min, T? max) where T : struct, IComparable<T> => (!min.HasValue && !max.HasValue) || (value.HasValue && (!min.HasValue || value.Value.CompareTo(min.Value) >= 0) && (!max.HasValue || value.Value.CompareTo(max.Value) <= 0));
    private static bool Minimum<T>(T? value, T? min) where T : struct, IComparable<T> => !min.HasValue || (value.HasValue && value.Value.CompareTo(min.Value) >= 0);
    private static bool Maximum<T>(T? value, T? max) where T : struct, IComparable<T> => !max.HasValue || (value.HasValue && value.Value.CompareTo(max.Value) <= 0);
}
