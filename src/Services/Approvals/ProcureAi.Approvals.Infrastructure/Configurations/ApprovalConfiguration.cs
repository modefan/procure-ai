using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Infrastructure.Configurations;

internal sealed class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    private const string MoneyColumn = "numeric(18,2)";

    public void Configure(EntityTypeBuilder<ApprovalStep> builder)
    {
        builder.ToTable("approval_steps");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasMaxLength(100).IsRequired();
        builder.Property(s => s.RequestId).HasColumnName("request_id");
        builder.Property(s => s.RequestNumber).HasColumnName("request_number").HasMaxLength(50).IsRequired();
        builder.Property(s => s.RequestTitle).HasColumnName("request_title").HasMaxLength(200).IsRequired();
        builder.Property(s => s.RequesterId).HasColumnName("requester_id").HasMaxLength(100).IsRequired();
        builder.Property(s => s.TotalInclVat).HasColumnName("total_incl_vat").HasColumnType(MoneyColumn)
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        builder.Property(s => s.StepNo).HasColumnName("step_no");
        builder.Property(s => s.Level).HasColumnName("level").HasConversion<string>().HasMaxLength(20);
        builder.Ignore(s => s.CoversLevels);
        builder.Property<string[]>("_coversLevels").HasColumnName("covers_levels").HasColumnType("text[]");
        builder.Ignore(s => s.ApproverIds);
        builder.Property<string[]>("_approverIds").HasColumnName("approver_ids").HasColumnType("text[]");
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.ActedBy).HasColumnName("acted_by").HasMaxLength(100);
        builder.Property(s => s.ActedAt).HasColumnName("acted_at");
        builder.Property(s => s.DueAt).HasColumnName("due_at");
        builder.Ignore(s => s.IsPending);
        builder.Ignore(s => s.IsOpen);

        // The Postgres system column xmin changes on every write of the row: an optimistic concurrency token (the ETag).
        builder.Property(s => s.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(s => new { s.TenantId, s.RequestId, s.StepNo });
        // The inbox: the pending steps of a tenant.
        builder.HasIndex(s => new { s.TenantId, s.Status });
    }
}

internal sealed class ApprovalSagaConfiguration : IEntityTypeConfiguration<ApprovalSaga>
{
    public void Configure(EntityTypeBuilder<ApprovalSaga> builder)
    {
        builder.ToTable("approval_sagas");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasMaxLength(100).IsRequired();
        builder.Property(s => s.RequestNumber).HasColumnName("request_number").HasMaxLength(50).IsRequired();
        builder.Property(s => s.RequestTitle).HasColumnName("request_title").HasMaxLength(200).IsRequired();
        builder.Property(s => s.RequesterId).HasColumnName("requester_id").HasMaxLength(100).IsRequired();
        builder.Property(s => s.CostCenterCode).HasColumnName("cost_center_code").HasMaxLength(50).IsRequired();
        builder.Property(s => s.TotalInclVat).HasColumnName("total_incl_vat").HasColumnType("numeric(18,2)");
        builder.Property(s => s.State).HasColumnName("state").HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.CurrentStepNo).HasColumnName("current_step_no");
        builder.Property(s => s.CancelRequested).HasColumnName("cancel_requested");
        builder.Property(s => s.OverdueAlertStepId).HasColumnName("overdue_alert_step_id");
        builder.Property(s => s.OverdueRemindersSent).HasColumnName("overdue_reminders_sent");
        builder.Property(s => s.RemindersSent).HasColumnName("reminders_sent");
        // Wolverine increments it on every save of the saga: of two messages for one request at once, the second save fails and is retried.
        builder.Property(s => s.Version).HasColumnName("version").IsConcurrencyToken();
    }
}
