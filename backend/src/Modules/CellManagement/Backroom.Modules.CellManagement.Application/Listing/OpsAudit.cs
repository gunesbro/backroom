using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Backroom.Modules.CellManagement.Application.Listing;

/// <summary>
/// The audit event of an operations call: account id when known, action, result and trace id; no personal
/// data. It is a structured log event until the audit log exists.
/// </summary>
internal sealed partial class OpsAudit(ILogger<OpsAudit> logger)
{
    public const string Success = "success";

    public const string Invalid = "invalid";

    public const string Rejected = "rejected";

    public const string Cancelled = "cancelled";

    public const string Failed = "failed";

    public const string Unauthenticated = "denied.unauthenticated";

    public const string Forbidden = "denied.forbidden";

    public void Record(Guid? accountId, string action, string result)
    {
        var account = accountId?.ToString() ?? "unknown";
        var trace = Activity.Current?.TraceId.ToString() ?? "none";
        LogAudit(logger, account, action, result, trace);
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Audit: account {AccountId} {Action} {Result} (trace {TraceId}).")]
    private static partial void LogAudit(ILogger logger, string accountId, string action, string result, string traceId);
}
