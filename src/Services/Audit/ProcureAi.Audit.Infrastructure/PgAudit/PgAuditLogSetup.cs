namespace ProcureAi.Audit.Infrastructure.PgAudit;

/// <summary>
/// The names of the file_fdw tables Audit reads the Postgres server's csvlog through (plan 06 ruling 14): in <c>auditdb</c>, schema
/// <see cref="PgAuditOptions.Schema"/>, one foreign table per weekday, <c>log_mon</c> ... <c>log_sun</c>, each over that weekday's file. Postgres
/// names its files after the weekday (<c>log_filename=postgresql-%a.log</c>), so the reader picks today's table and no file name ever changes at
/// run time.
/// <para>
/// The tables, the role <c>audit_log_reader</c> and its password are made by infra/postgres/roles-pgaudit.sql (part of roles.sql, run as the
/// superuser on every start, plan 08 Task 4): a foreign table over a server file needs the superuser, which Audit's own role <c>audit_app</c>
/// is not. Until then Audit's <c>--migrate</c> made them while it still connected as the superuser.
/// </para>
/// </summary>
public static class PgAuditLogSetup
{
    /// <summary>The weekdays as Postgres' strftime <c>%a</c> names them (the file names) and the tables are named (lower case).</summary>
    public static IReadOnlyList<string> Weekdays { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>The table of one weekday (<c>Mon</c> -> <c>log_mon</c>).</summary>
    public static string TableName(string weekday) => "log_" + weekday.ToLowerInvariant();
}
