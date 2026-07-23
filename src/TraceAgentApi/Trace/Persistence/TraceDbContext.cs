using Microsoft.EntityFrameworkCore;

namespace TraceAgentApi.Trace.Persistence;

public class TraceDbContext(DbContextOptions<TraceDbContext> options) : DbContext(options)
{
    public DbSet<AgentRunEntity> AgentRuns => Set<AgentRunEntity>();
    public DbSet<TraceStepEntity> TraceSteps => Set<TraceStepEntity>();
    public DbSet<EvalRunEntity> EvalRuns => Set<EvalRunEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<RunEmbeddingEntity> RunEmbeddings => Set<RunEmbeddingEntity>();
    public DbSet<RunChunkEmbeddingEntity> RunChunkEmbeddings => Set<RunChunkEmbeddingEntity>();
    public DbSet<ExternalScanEntity> ExternalScans => Set<ExternalScanEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<AgentRunEntity>().ToTable("agent_runs");
        modelBuilder.Entity<TraceStepEntity>().ToTable("trace_steps");
        modelBuilder.Entity<EvalRunEntity>().ToTable("eval_runs");

        modelBuilder.Entity<AuditEntryEntity>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(e => e.Sequence);
            entity.HasIndex(e => e.ResourceId);
        });

        modelBuilder.Entity<AgentRunEntity>()
            .HasMany(r => r.Steps)
            .WithOne(s => s.AgentRun)
            .HasForeignKey(s => s.AgentRunId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RunEmbeddingEntity>(entity =>
        {
            entity.ToTable("run_embeddings");
            entity.HasKey(e => e.RunId);
            entity.Property(e => e.Embedding).HasColumnType("vector(768)");

            entity.HasOne(e => e.Run)
                .WithOne()
                .HasForeignKey<RunEmbeddingEntity>(e => e.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalScanEntity>(entity =>
        {
            entity.ToTable("external_scans");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<RunChunkEmbeddingEntity>(entity =>
        {
            entity.ToTable("run_chunk_embeddings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Embedding).HasColumnType("vector(768)");
            entity.HasIndex(e => e.RunId);

            entity.HasOne(e => e.Run)
                .WithMany()
                .HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
