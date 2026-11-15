using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.ServiceDefaults;

namespace Ume.Rag.AdminApi;

/// <summary>
/// One admin change as the write path records it: the result for the caller and the audit entry's fields. Before and after
/// are snapshot records (<see cref="AuditSnapshots"/>) that never hold secrets.
/// </summary>
public sealed record AdminChange<T>(T Result, string Action, string EntityType, object? EntityId, object? Before, object? After)
{
    public Guid? DepartmentId { get; init; }
    public string? Classification { get; init; }
    public string? Reason { get; init; }

    /// <summary>Worker jobs the change starts (admin design §4.7), enqueued in the same transaction.</summary>
    public IReadOnlyList<JobRequest> Jobs { get; init; } = [];

    /// <summary>Changes to other entities made by the same write, each with its own audit entry in the same transaction.</summary>
    public IReadOnlyList<AuditAlso> Also { get; init; } = [];
}

/// <summary>
/// A related change audited with an <see cref="AdminChange{T}"/> (same actor, version and level; the change's department unless
/// <see cref="DepartmentId"/> names its own, e.g. a client's first key with a team).
/// </summary>
public sealed record AuditAlso(string Action, string EntityType, object? EntityId, object? Before, object? After)
{
    public Guid? DepartmentId { get; init; }
}

/// <summary>
/// The signed-in user's view of the configuration and the admin write path (admin design §3.2, §4.4), adapted from the
/// gateway's <c>AdminContext</c>: no invalidation bus (the outbox is in the transaction, data-model §5), and every config
/// change runs under the config version lock with its audit entry.
/// </summary>
public sealed class AdminContext(RagDbContext db, IJobQueue jobs, TimeProvider time, ILoggerFactory loggers, IHttpContextAccessor http,
    ConfigLockMetrics lockMetrics)
{
    public const string ServiceName = "rag-admin";

    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    public RagDbContext Db { get; } = db;
    public DateTimeOffset Now => time.GetUtcNow();

    private HttpContext Http => http.HttpContext
        ?? throw new InvalidOperationException("AdminContext is only available while handling an HTTP request.");

    /// <summary>The signed-in user of the current request. Every scoped query and audit entry is for this user.</summary>
    public ClaimsPrincipal User => Http.User;

    public bool IsPlatformAdmin => User.IsInRole(RagRoles.PlatformAdmin);

    /// <summary>The audit actor: <c>oid</c>, else <c>sub</c> (identity design §1.1).</summary>
    public string Actor => AdminActor.SubjectOf(User);

    /// <summary>
    /// Departments in the user's scope: every one for a platform admin, else those of the department claim and of member
    /// groups (identity design §2.3; both are <c>departmentCodes</c> after sign-in). Copied from the gateway.
    /// </summary>
    public IQueryable<Department> Departments()
    {
        if (IsPlatformAdmin)
        {
            return Db.Departments;
        }

        string[] codes = [.. User.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value)];
        return Db.Departments.Where(d => codes.Contains(d.CostCenterCode));
    }

    /// <summary>
    /// The owner team a create gives a new collection or assistant when the form names none (Decisions "Organisation":
    /// teams are hidden in create forms): the creator's first active team in <paramref name="departmentId"/> by name,
    /// from the <c>teamCodes</c> claim; null when the creator has none there.
    /// </summary>
    public async Task<Guid?> DefaultOwnerTeamAsync(Guid departmentId, CancellationToken ct)
    {
        string[] codes = [.. User.FindAll(AdminClaims.TeamCodes).Select(c => c.Value)];
        if (codes.Length == 0)
        {
            return null;
        }

        return await Db.Teams.AsNoTracking().Where(t => t.DepartmentId == departmentId && t.IsActive && t.Code != null && codes.Contains(t.Code))
            .OrderBy(t => t.Name).ThenBy(t => t.Id).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Runs one config change through the write path (admin design §4.4): the version lock first, then
    /// <paramref name="apply"/> (load, version check, rules, change), save (the triggers write the outbox and bump the
    /// version), then the audit entry with the new version and the change's jobs (<c>pg_notify('rag_jobs')</c> is delivered
    /// at commit), all in one transaction; the 6001 event after commit.
    /// <paramref name="apply"/> may run more than once (transient failures retry), so it loads everything it changes.
    /// </summary>
    public Task<T> WriteAsync<T>(Func<CancellationToken, Task<AdminChange<T>>> apply, CancellationToken ct) => WriteAsAsync(AdminActor.Of(Http), apply, ct);

    /// <summary>
    /// <see cref="WriteAsync{T}"/> with an explicit actor instead of the signed-in user: setup completion, whose actor is the person of
    /// the passed test sign-in (<see cref="AdminActor.ForSetup"/>, identity design §19), on an anonymous request.
    /// </summary>
    internal async Task<T> WriteAsAsync<T>(AdminActor actor, Func<CancellationToken, Task<AdminChange<T>>> apply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(apply);
        AuditLogEntry? audit = null;
        T result;
        try
        {
            result = await Db.ExecuteConfigWriteAsync(async (_, attempt) =>
            {
                var change = await apply(attempt);
                await Db.SaveChangesAsync(attempt);
                var version = await Db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(attempt);
                audit = Audit(actor, change, version);
                Db.AuditLog.Add(audit);
                foreach (var also in change.Also)
                {
                    Db.AuditLog.Add(Audit(actor, change with
                    {
                        Action = also.Action, EntityType = also.EntityType, EntityId = also.EntityId, Before = also.Before, After = also.After,
                        DepartmentId = also.DepartmentId ?? change.DepartmentId,
                    }, version));
                }

                await Db.SaveChangesAsync(attempt);
                if (change.Jobs.Count > 0)
                {
                    await jobs.EnqueueAsync(change.Jobs, Db.Database.CurrentTransaction!.GetDbTransaction(), attempt);
                }

                return change.Result;
            }, ct, lockMetrics);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw await ConcurrentChangeAsync(ex, ct);
        }

        SecurityEvents.AdminChange(_security, audit!.Actor, audit.Action, audit.EntityType, audit.EntityId);
        return result;
    }

    /// <summary>
    /// Records an audit-only action (admin design §4.4: reveal a key) without the config version lock: <paramref name="read"/> changes
    /// nothing, and its audit entry, with the config version current then, is saved before the result is returned, so a failed save
    /// returns nothing; the 6001 event after save, as <see cref="WriteAsync{T}"/>.
    /// </summary>
    public async Task<T> AuditAsync<T>(Func<CancellationToken, Task<AdminChange<T>>> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        var actor = AdminActor.Of(Http);
        var change = await read(ct);
        var version = await Db.ConfigVersion.AsNoTracking().Select(v => v.Version).SingleAsync(ct);
        var audit = Audit(actor, change, version);
        Db.AuditLog.Add(audit);
        await Db.SaveChangesAsync(ct);
        SecurityEvents.AdminChange(_security, audit.Actor, audit.Action, audit.EntityType, audit.EntityId);
        return change.Result;
    }

    /// <summary>Sets the version the caller saw, so the save fails with <c>concurrent_change</c> if the row has moved on.</summary>
    public void ExpectVersion<TEntity>(TEntity entity, string? version) where TEntity : class
    {
        if (!uint.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
        {
            throw AdminFaults.Field("version", AdminErrorCodes.InvalidValue);
        }

        Db.Entry(entity).Property("Version").OriginalValue = expected;
    }

    private AuditLogEntry Audit<T>(AdminActor actor, AdminChange<T> change, long version)
    {
        var entry = new AuditLogEntry
        {
            Timestamp = Now, Actor = actor.Subject, Action = change.Action, EntityType = change.EntityType, EntityId = change.EntityId?.ToString(),
            Details = JsonSerializer.Serialize(new AuditDetails(change.Before, change.After), AuditJson.Options),
            Service = ServiceName, ConfigVersion = version, DepartmentId = change.DepartmentId, Classification = change.Classification,
            Reason = change.Reason,
        };
        actor.Stamp(entry);
        return entry;
    }

    private static async Task<ApiFaultException> ConcurrentChangeAsync(DbUpdateConcurrencyException ex, CancellationToken ct)
    {
        // The current version lets the UI offer "Ladda om och jämför" (admin design §4.3); a deleted row has none.
        var entry = ex.Entries.Count > 0 ? ex.Entries[0] : null;
        var current = entry is null ? null : await entry.GetDatabaseValuesAsync(ct);
        var version = current?.TryGetValue<uint>("Version", out var v) == true ? v.ToString(CultureInfo.InvariantCulture) : null;
        return AdminFaults.Of(AdminErrorCodes.ConcurrentChange, extensions: version is null ? null : new Dictionary<string, object?> { ["version"] = version });
    }
}

/// <summary>Problem responses with an admin error code (admin design §4.1, §4.2).</summary>
public static class AdminFaults
{
    /// <summary>A fault with <c>code</c> (and <c>params</c> for the UI's Swedish template) and the code's English message.</summary>
    public static ApiFaultException Of(AdminErrorCode code, IReadOnlyDictionary<string, string>? parameters = null, IDictionary<string, object?>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        var members = new Dictionary<string, object?>(extensions ?? new Dictionary<string, object?>()) { ["code"] = code.Code };
        if (parameters is { Count: > 0 })
        {
            members["params"] = parameters;
        }

        return new ApiFaultException(code.Status, Format(code.Message, parameters), members);
    }

    /// <summary>A 400 validation problem for one field, with its code in <c>errorCodes</c>.</summary>
    public static ApiFaultException Field(string field, AdminErrorCode code, IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new ApiFaultException(400, ApiFaultException.ValidationTitle, new Dictionary<string, object?>
        {
            ["errors"] = new Dictionary<string, string[]> { [field] = [Format(code.Message, parameters)] },
            ["errorCodes"] = new Dictionary<string, ErrorCodeEntry[]> { [field] = [new(code.Code, parameters)] },
        });
    }

    /// <summary>
    /// A rule refusal about one of several objects a field names, as that field's error: the refusal's message after
    /// <paramref name="subject"/> (the object's name), its code and params in <c>errorCodes</c>, with the name as the param
    /// <c>subject</c> so the UI can put it before its own text for the code.
    /// </summary>
    public static ApiFaultException Field(string field, string subject, ApiFaultException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var code = refusal.Extensions?.TryGetValue("code", out var c) == true ? c as string : null;
        var parameters = new Dictionary<string, string>(
            refusal.Extensions?.TryGetValue("params", out var p) == true && p is IReadOnlyDictionary<string, string> given ? given : new Dictionary<string, string>())
        {
            ["subject"] = subject,
        };
        var members = new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { [field] = [$"{subject}: {refusal.Message}"] } };
        if (code is not null)
        {
            members["errorCodes"] = new Dictionary<string, ErrorCodeEntry[]> { [field] = [new(code, parameters)] };
        }

        return new ApiFaultException(400, ApiFaultException.ValidationTitle, members);
    }

    private static string Format(string message, IReadOnlyDictionary<string, string>? parameters) =>
        parameters?.Aggregate(message, (text, p) => text.Replace("{" + p.Key + "}", p.Value, StringComparison.Ordinal)) ?? message;
}

/// <summary>One entry of a validation problem's <c>errorCodes</c>.</summary>
public sealed record ErrorCodeEntry(string Code, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Params);

/// <summary>
/// Validates <see cref="AdminRequest"/> arguments (copied from the gateway); each field error also gets the code
/// <c>invalid_value</c> in <c>errorCodes</c>, so the UI has a Swedish text for it.
/// </summary>
public sealed class AdminValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var argument in context.Arguments.OfType<AdminRequest>())
        {
            Validate(argument, "", errors);
        }

        return errors.Count > 0
            ? Results.ValidationProblem(errors, title: ApiFaultException.ValidationTitle, extensions: new Dictionary<string, object?>
            {
                ["errorCodes"] = errors.ToDictionary(e => e.Key, _ => new[] { new ErrorCodeEntry(AdminErrorCodes.InvalidValue.Code, null) }),
            })
            : await next(context);
    }

    private static void Validate(AdminRequest argument, string prefix, Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(argument, new ValidationContext(argument), results, validateAllProperties: true);
        foreach (var result in results)
        {
            foreach (var member in result.MemberNames.DefaultIfEmpty("request"))
            {
                var field = prefix + JsonNamingPolicy.CamelCase.ConvertName(member);
                errors[field] = [result.ErrorMessage ?? "The value is invalid."];
            }
        }

        foreach (var property in argument.GetType().GetProperties())
        {
            if (property.GetValue(argument) is AdminRequest nested)
            {
                Validate(nested, prefix + JsonNamingPolicy.CamelCase.ConvertName(property.Name) + ".", errors);
            }
            else if (property.GetValue(argument) is IEnumerable<AdminRequest> children)
            {
                var i = 0;
                foreach (var child in children)
                {
                    var path = $"{prefix}{JsonNamingPolicy.CamelCase.ConvertName(property.Name)}[{i++}].";
                    if (child is not null) // a null entry is the endpoint's to skip or refuse (CollectionLinks skips it)
                    {
                        Validate(child, path, errors);
                    }
                }
            }
        }
    }
}

/// <summary>
/// Base of admin request bodies: DataAnnotations and <see cref="IValidatableObject"/> rules, checked by <see cref="AdminValidationFilter"/>.
/// Every admin type with validation attributes derives from it, nested ones included, or its attributes are never checked (#699;
/// <c>AdminRequestValidationTests</c> enforces it).
/// </summary>
public abstract record AdminRequest;
