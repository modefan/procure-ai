namespace ProcureAi.Audit.Application.Abstractions;

/// <summary>How many pgaudit lines a database role wrote in one class (<c>DDL</c>, <c>ROLE</c>, ...) since <see cref="PgAuditSummary.From"/>.</summary>
public sealed record PgAuditCount(string Role, string Class, long Count);

/// <summary>
/// One statement pgaudit logged: who ran it as which role, in which database, the object it named (pgaudit's object name, e.g.
/// <c>public.budgets</c>; empty for statements without one, such as <c>CREATE ROLE</c>), and its text (cut to the configured length).
/// </summary>
public sealed record PgAuditStatement(DateTimeOffset At, string Role, string Database, string Class, string Command, string Text, string ObjectName = "");

/// <summary>
/// The pgaudit summary of the last day (plan 06 ruling 14): counts per database role and class, and the newest statements, newest first.
/// Empty when there is no log file for today yet (after midnight until the first statement, or on a server without the csvlog).
/// </summary>
public sealed record PgAuditSummary(DateTimeOffset From, IReadOnlyList<PgAuditCount> Counts, IReadOnlyList<PgAuditStatement> Latest)
{
    public static PgAuditSummary Empty(DateTimeOffset from) => new(from, [], []);
}

/// <summary>Reads what pgaudit wrote to the Postgres server's csvlog today (Infrastructure: the <c>file_fdw</c> table <c>pgaudit.log_today</c>).</summary>
public interface IPgAuditReader
{
    Task<PgAuditSummary> ReadAsync(CancellationToken cancellationToken);
}
