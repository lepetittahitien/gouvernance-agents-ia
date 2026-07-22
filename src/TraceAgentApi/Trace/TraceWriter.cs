namespace TraceAgentApi.Trace;

/// Imprime la trace complète d'un run dans la console — la brique "instrumentation" de T1.
public static class TraceWriter
{
    public static void Print(AgentRunTrace trace)
    {
        var separator = new string('=', 70);

        Console.WriteLine();
        Console.WriteLine(separator);
        Console.WriteLine($"RUN {trace.RunId}");
        Console.WriteLine($"Modèle    : {trace.ModelName}");
        Console.WriteLine($"Démarré   : {trace.StartedAt:O}");
        Console.WriteLine($"Prompt    : {trace.Prompt}");
        Console.WriteLine(separator);

        foreach (var step in trace.Steps)
        {
            var kindLabel = step.Kind == TraceStepKind.ModelCall ? "MODEL" : "TOOL ";
            Console.WriteLine($"[{step.Index,2}] {kindLabel} | {step.DurationMs,6} ms | {step.Label}");
            Console.WriteLine($"      {step.Detail}");

            if (step.InputTokens is not null || step.OutputTokens is not null)
            {
                Console.WriteLine($"      tokens in={step.InputTokens ?? 0} out={step.OutputTokens ?? 0}");
            }
        }

        Console.WriteLine(separator);
        Console.WriteLine($"Réponse finale : {trace.FinalAnswer}");
        Console.WriteLine(separator);
        Console.WriteLine($"Total tokens   : in={trace.TotalInputTokens} out={trace.TotalOutputTokens}");
        Console.WriteLine($"Durée totale   : {trace.TotalDurationMs} ms");
        Console.WriteLine($"Coût estimé    : {trace.EstimatedCostEur:0.0000} €" +
                           (trace.EstimatedCostEur == 0 ? "  (modèle local Ollama — pas de facturation réelle)" : ""));

        if (trace.HasPiiViolation)
        {
            var breakdown = string.Join(", ", trace.PiiFindingsByType.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"⚠️  VIOLATION PII : {breakdown}");
        }

        if (trace.Budget is { } budget)
        {
            if (budget.PeriodTokenBudget > 0)
            {
                Console.WriteLine(
                    $"Budget période : {budget.PeriodTokensUsed}/{budget.PeriodTokenBudget} tokens " +
                    $"sur {budget.PeriodHours} h ({budget.PeriodUsagePercent}%)");
            }

            foreach (var breach in budget.Breaches)
            {
                Console.WriteLine($"⚠️  BUDGET DÉPASSÉ : {breach.Message}");
            }
        }

        Console.WriteLine(separator);
        Console.WriteLine();
    }
}
