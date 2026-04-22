using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Costs;

/// <summary>
/// Calculates LLM usage cost from model name and token counts.
/// Prices are per million tokens (USD). Unknown models return zero cost
/// unless budget enforcement is active — use <see cref="CanPrice"/> to check.
/// </summary>
public sealed class CostCalculator
{
    private readonly IReadOnlyDictionary<string, ModelPriceEntry> _prices;

    public CostCalculator() : this(DefaultPrices) { }

    public CostCalculator(IReadOnlyDictionary<string, ModelPriceEntry> prices)
    {
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
    }

    /// <summary>
    /// Calculate cost for a single LLM call.
    /// Returns 0 for unknown models (caller should check <see cref="CanPrice"/> when budgeting).
    /// </summary>
    public decimal Calculate(string model, int inputTokens, int outputTokens)
    {
        if (!_prices.TryGetValue(model, out var price))
            return 0m;
        return (inputTokens * price.InputPricePerMToken / 1_000_000m)
             + (outputTokens * price.OutputPricePerMToken / 1_000_000m);
    }

    /// <summary>Calculate cost from a model name and <see cref="TokenCount"/>.</summary>
    public decimal Calculate(string model, TokenCount tokens) =>
        Calculate(model, tokens.In, tokens.Out);

    /// <summary>Returns true if the model has a known price entry.</summary>
    public bool CanPrice(string model) => _prices.ContainsKey(model);

    /// <summary>
    /// Validate that all models used in a methodology are priced.
    /// Throws <see cref="InvalidOperationException"/> if budget is set and any model is unpriced.
    /// No-op when budget is null.
    /// </summary>
    public void ValidatePricingForBudget(MethodologyDefinition methodology)
    {
        if (methodology.Budget is null)
            return;

        foreach (var phase in methodology.Phases)
        {
            var genModel = ResolveModelFallback(phase.Model, methodology.ModelDefaults?.Generation, "gpt-4o");
            if (!CanPrice(genModel))
                throw new InvalidOperationException(
                    $"Budget enforcement requires pricing for model '{genModel}' " +
                    $"(generation, phase '{phase.Id}'). Add it to CostCalculator.");

            var judgeModel = ResolveModelFallback(phase.JudgeModel, methodology.ModelDefaults?.Judge, "gpt-4o-mini");
            if (!CanPrice(judgeModel))
                throw new InvalidOperationException(
                    $"Budget enforcement requires pricing for model '{judgeModel}' " +
                    $"(judge, phase '{phase.Id}'). Add it to CostCalculator.");
        }
    }

    private static string ResolveModelFallback(string? phaseOverride, string? methodologyDefault, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(phaseOverride)) return phaseOverride;
        if (!string.IsNullOrWhiteSpace(methodologyDefault)) return methodologyDefault;
        return fallback;
    }

    /// <summary>
    /// Default pricing table (USD per million tokens). Approximate as of 2026-04.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ModelPriceEntry> DefaultPrices =
        new Dictionary<string, ModelPriceEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o"] = new(2.50m, 10.00m),
            ["gpt-4o-2024-08-06"] = new(2.50m, 10.00m),
            ["gpt-4o-mini"] = new(0.15m, 0.60m),
            ["gpt-4.1"] = new(2.00m, 8.00m),
            ["gpt-4.1-mini"] = new(0.40m, 1.60m),
            ["gpt-4.1-nano"] = new(0.10m, 0.40m),
            ["o3-mini"] = new(1.10m, 4.40m),
            ["claude-opus-4.7"] = new(5.00m, 25.00m),
            ["claude-haiku-4.5"] = new(1.00m, 5.00m),
        };
}

/// <summary>Pricing per million tokens (input and output) in USD.</summary>
public sealed record ModelPriceEntry(decimal InputPricePerMToken, decimal OutputPricePerMToken);
