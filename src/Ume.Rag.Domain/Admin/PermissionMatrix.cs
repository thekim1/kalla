namespace Ume.Rag.Domain.Admin;

/// <summary>Admin roles from the identity provider's role claim (identity design §1.3). Other values are ignored.</summary>
public static class RagRoles
{
    public const string PlatformAdmin = "rag-platform-admin";
    public const string DepartmentAdmin = "rag-department-admin";
    public const string PipelineEditor = "rag-pipeline-editor";
    public const string Reviewer = "rag-reviewer";

    /// <summary>P2 (tools design §3.6).</summary>
    public const string SkillEditor = "rag-skill-editor";

    /// <summary>P2.</summary>
    public const string Editor = "rag-editor";

    public const string Viewer = "rag-viewer";

    public static IReadOnlyList<string> All { get; } = [PlatformAdmin, DepartmentAdmin, PipelineEditor, Reviewer, SkillEditor, Editor, Viewer];
}

/// <summary>Authorization policy names (admin design §3.1).</summary>
public static class AdminPolicies
{
    /// <summary>Platform objects and every department.</summary>
    public const string Admin = "admin";

    /// <summary>Platform and department admins (department admins within their departments).</summary>
    public const string Manage = "manage";

    public const string EditPipelines = "edit-pipelines";
    public const string EditSkills = "edit-skills";
    public const string Edit = "edit";

    /// <summary>Open a conversation for review. No administrative role grants conversation content.</summary>
    public const string Review = "review";

    /// <summary>Reveal one feedback comment.</summary>
    public const string FeedbackComments = "feedback-comments";

    /// <summary>Every rag-* role. A user without one gets 403 on every <c>/api</c> call.</summary>
    public const string Read = "read";

    public static IReadOnlyList<string> All { get; } = [Admin, Manage, EditPipelines, EditSkills, Edit, Review, FeedbackComments, Read];
}

/// <summary>
/// Which roles hold which policies (admin design §3.1, §3.4), as data: the admin API builds its authorization policies
/// from it and the role tests are generated from it. Roles add up; <see cref="AdminPolicies.Review"/> and
/// <see cref="AdminPolicies.FeedbackComments"/> are never implied by <see cref="AdminPolicies.Admin"/>.
/// </summary>
public static class PermissionMatrix
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PoliciesByRole { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [RagRoles.PlatformAdmin] = [AdminPolicies.Admin, AdminPolicies.Manage, AdminPolicies.EditPipelines, AdminPolicies.EditSkills, AdminPolicies.Edit, AdminPolicies.Read],
        [RagRoles.DepartmentAdmin] = [AdminPolicies.Manage, AdminPolicies.EditPipelines, AdminPolicies.EditSkills, AdminPolicies.Edit, AdminPolicies.Read, AdminPolicies.FeedbackComments],
        [RagRoles.PipelineEditor] = [AdminPolicies.EditPipelines, AdminPolicies.Read],
        [RagRoles.Reviewer] = [AdminPolicies.Review, AdminPolicies.FeedbackComments, AdminPolicies.Read],
        [RagRoles.SkillEditor] = [AdminPolicies.EditSkills, AdminPolicies.Read],
        [RagRoles.Editor] = [AdminPolicies.Edit, AdminPolicies.Read],
        [RagRoles.Viewer] = [AdminPolicies.Read],
    };

    /// <summary>The roles that hold <paramref name="policy"/>.</summary>
    public static IReadOnlyList<string> RolesFor(string policy) =>
        [.. PoliciesByRole.Where(r => r.Value.Contains(policy, StringComparer.Ordinal)).Select(r => r.Key)];

    /// <summary>Whether any of <paramref name="roles"/> holds <paramref name="policy"/>.</summary>
    public static bool Allows(IEnumerable<string> roles, string policy) =>
        roles.Any(role => PoliciesByRole.TryGetValue(role, out var policies) && policies.Contains(policy, StringComparer.Ordinal));
}
