using System.Text;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Ingestion;

/// <summary>The upload type check (ruling 12) and the version rules of a policy document.</summary>
public sealed class PolicyDocumentRulesTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("policy.pdf", "%PDF-1.7\n...", PolicyFileType.Pdf)]
    [InlineData("POLICY.PDF", "%PDF-1.4", PolicyFileType.Pdf)]
    [InlineData("policy.md", "# Policy\n\nRand amounts: R 20 000,00 – fine.", PolicyFileType.Markdown)]
    [InlineData("policy.markdown", "text", PolicyFileType.Markdown)]
    [InlineData("policy.pdf", "MZ\u0090 not a pdf", null)]
    [InlineData("policy.md", "%PDF-1.7 but called md\0", null)]
    [InlineData("policy.txt", "plain text", null)]
    [InlineData("policy", "%PDF-1.7", null)]
    [InlineData("policy.md", "", null)]
    public void File_type_comes_from_content_and_extension(string fileName, string content, string? expected) =>
        PolicyFileType.Detect(fileName, Encoding.UTF8.GetBytes(content)).ShouldBe(expected);

    [Fact]
    public void Retry_schedule_defaults_to_10_seconds_1_minute_5_minutes_and_rejects_nonsense()
    {
        new IngestionOptions().RetryDelaySchedule().ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)]);
        new IngestionOptions { RetryDelays = "00:00:00.200,00:00:01" }.RetryDelaySchedule()
            .ShouldBe([TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)]);
        foreach (var invalid in new[] { "", "soon", "00:00:00", "02:00:00", "-00:00:05" })
        {
            var options = new IngestionOptions { RetryDelays = invalid };
            options.RetryDelaySchedule().ShouldBeEmpty(invalid);
            options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).ShouldNotBeEmpty(invalid);
        }
    }

    [Fact]
    public void Invalid_utf8_is_not_markdown() =>
        PolicyFileType.Detect("policy.md", [0x23, 0x20, 0xC3, 0x28]).ShouldBeNull();

    [Fact]
    public void Replace_starts_a_new_version_and_keeps_the_current_one()
    {
        var document = Ready();

        document.ReplaceFile("policy-v2.md", "ada", At.AddHours(1));

        document.Version.ShouldBe(2);
        document.CurrentVersion.ShouldBe(1);
        document.ChunkCount.ShouldBe(5);
        document.Status.ShouldBe(PolicyDocumentStatus.Uploaded);
        document.Progress.ShouldBe(0);
        document.FileName.ShouldBe("policy-v2.md");
        // The old version's job is stale now: it can no longer move the document.
        document.IsNewest(1).ShouldBeFalse();
        document.MarkReady(1, 9, At).ShouldBeFalse();
        document.ChunkCount.ShouldBe(5);
        document.MarkReady(2, 7, At).ShouldBeTrue();
        document.CurrentVersion.ShouldBe(2);
        document.ChunkCount.ShouldBe(7);
    }

    [Fact]
    public void Flag_and_fail_keep_the_current_version_and_record_the_reason()
    {
        var document = Ready();
        document.ReplaceFile("v2.md", "ada", At);
        document.Flag(2, "Injected instructions.", At).ShouldBeTrue();
        document.Status.ShouldBe(PolicyDocumentStatus.Flagged);
        document.FlaggedReason.ShouldBe("Injected instructions.");
        document.CurrentVersion.ShouldBe(1);

        document.ReplaceFile("v3.md", "ada", At);
        document.FlaggedReason.ShouldBeNull();
        document.Fail(3, new string('x', 600), At).ShouldBeTrue();
        document.FailureReason!.Length.ShouldBe(PolicyDocument.ReasonMaxLength);
        document.FailureReason.ShouldEndWith("…");
        document.CurrentVersion.ShouldBe(1);
    }

    [Fact]
    public void A_removed_document_takes_no_more_versions()
    {
        var document = Ready();
        document.Remove(At);

        document.Removed.ShouldBeTrue();
        document.IsNewest(1).ShouldBeFalse();
        Should.Throw<DomainException>(() => document.ReplaceFile("v2.md", "ada", At));
        Should.Throw<DomainException>(() => document.Remove(At));
    }

    private static PolicyDocument Ready()
    {
        var document = new PolicyDocument(Guid.NewGuid(), "acme", "Policy", "policy.md", "ada", At);
        document.StartChecking(1, At).ShouldBeTrue();
        document.StartEmbedding(1, At).ShouldBeTrue();
        document.SetProgress(1, 40, At).ShouldBeTrue();
        document.MarkReady(1, 5, At).ShouldBeTrue();
        return document;
    }
}
