using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Audit;
using TraceAgentApi.Trace;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Compliance;

public record ComplianceRunSummary(
    Guid RunId,
    DateTimeOffset StartedAt,
    string ModelName,
    string Prompt,
    int TotalTokens,
    long DurationMs,
    bool HasPiiViolation);

public record ComplianceExport(
    DateTimeOffset GeneratedAt,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo,
    bool PiiRedacted,
    /// Preuve d'intégrité jointe à l'export : un auditeur reçoit les données ET
    /// la vérification que le journal n'a pas été altéré.
    AuditChainVerification IntegrityProof,
    int RunsTotal,
    int RunsWithPiiViolation,
    int ToolCallsDenied,
    long TotalTokens,
    List<ComplianceRunSummary> Runs,
    List<AuditEntryDto> AuditEntries);

public class ComplianceExporter(TraceDbContext dbContext, AuditLogger auditLogger)
{
    public async Task<ComplianceExport> BuildAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        bool redactPii,
        CancellationToken cancellationToken = default)
    {
        var runs = await dbContext.AgentRuns
            .Where(r => r.StartedAt >= from && r.StartedAt <= to)
            .OrderBy(r => r.StartedAt)
            .Select(r => new ComplianceRunSummary(
                r.Id, r.StartedAt, r.ModelName, r.Prompt,
                r.TotalInputTokens + r.TotalOutputTokens, r.TotalDurationMs, r.HasPiiViolation))
            .ToListAsync(cancellationToken);

        var auditEntities = await dbContext.AuditEntries
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        // La vérification porte sur la chaîne entière, pas seulement sur la période exportée :
        // une altération hors période invaliderait quand même le journal.
        var integrity = await auditLogger.VerifyChainAsync(cancellationToken);

        var auditEntries = auditEntities
            .Select(e => new AuditEntryDto(
                e.Sequence, e.Timestamp, e.ActorType, e.ActorId, e.Action,
                e.ResourceType, e.ResourceId,
                redactPii ? PiiScanner.RedactAll(e.Details) : e.Details,
                e.Hash, e.PreviousHash))
            .ToList();

        if (redactPii)
        {
            runs = runs
                .Select(r => r with { Prompt = PiiScanner.RedactAll(r.Prompt) })
                .ToList();
        }

        return new ComplianceExport(
            GeneratedAt: DateTimeOffset.UtcNow,
            PeriodFrom: from,
            PeriodTo: to,
            PiiRedacted: redactPii,
            IntegrityProof: integrity,
            RunsTotal: runs.Count,
            RunsWithPiiViolation: runs.Count(r => r.HasPiiViolation),
            ToolCallsDenied: auditEntities.Count(e => e.Action == AuditAction.ToolDenied),
            TotalTokens: runs.Sum(r => (long)r.TotalTokens),
            Runs: runs,
            AuditEntries: auditEntries);
    }

    /// Export CSV du journal d'audit — le format qu'un auditeur ouvrira dans un tableur.
    public static string ToCsv(ComplianceExport export)
    {
        var sb = new StringBuilder();

        // En-tête de contexte : un CSV nu sans période ni preuve d'intégrité n'a aucune
        // valeur probante. Préfixé par '#' pour rester distinguable des données.
        sb.AppendLine($"# Export de conformité — généré le {export.GeneratedAt:O}");
        sb.AppendLine($"# Période : {export.PeriodFrom:O} → {export.PeriodTo:O}");
        sb.AppendLine($"# PII caviardées : {(export.PiiRedacted ? "oui" : "non")}");
        sb.AppendLine($"# Intégrité du journal : {(export.IntegrityProof.IsIntact ? "INTACTE" : "COMPROMISE")}" +
                      $" ({export.IntegrityProof.EntriesChecked} entrées vérifiées)");
        if (!export.IntegrityProof.IsIntact)
        {
            sb.AppendLine($"# ⚠ {export.IntegrityProof.Explanation}");
        }
        sb.AppendLine($"# Runs : {export.RunsTotal} · violations PII : {export.RunsWithPiiViolation}" +
                      $" · appels refusés : {export.ToolCallsDenied} · tokens : {export.TotalTokens}");
        sb.AppendLine();

        sb.AppendLine("sequence,horodatage,type_acteur,acteur,action,type_ressource,ressource,details,hash");

        foreach (var e in export.AuditEntries)
        {
            sb.AppendLine(string.Join(',',
                Csv(e.Sequence.ToString(CultureInfo.InvariantCulture)),
                Csv(e.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                Csv(e.ActorType.ToString()),
                Csv(e.ActorId),
                Csv(e.Action.ToString()),
                Csv(e.ResourceType),
                Csv(e.ResourceId ?? ""),
                Csv(e.Details ?? ""),
                Csv(e.Hash)));
        }

        return sb.ToString();
    }

    /// Échappement CSV (RFC 4180) : sans ça, un prompt contenant une virgule, un guillemet
    /// ou un saut de ligne décalerait les colonnes et corromprait le document d'audit.
    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        return value;
    }
}
