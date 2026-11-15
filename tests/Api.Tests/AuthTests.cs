using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Application.Common;
using Application.Workspaces;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Api.Tests;

/// <summary>Authentication, role policies, tenant isolation, workspace membership and auditing.</summary>
[Collection(ApiFixtureGroup.Name)]
public sealed class AuthTests(ApiFactory factory)
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<WorkspaceResponse> CreateWorkspaceAsync(HttpClient client, string? name = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(name ?? Unique("ws"), null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkspaceResponse>())!;
    }

    private static Task<HttpResponseMessage> PutMember(HttpClient client, Guid workspaceId, string userId, string role) =>
        client.PutAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members/{userId}", new UpsertMemberRequest(role));

    // ---- Authentication ----

    [Fact]
    public async Task Anonymous_requests_to_protected_endpoints_get_401()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Health_and_ping_stay_anonymous()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/ping")).StatusCode);
    }

    public static TheoryData<string> BadTokens()
    {
        var wrongKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-signing-key-0123456789"));
        return new TheoryData<string>
        {
            TestAuth.Token(TestAuth.AdminA, expires: DateTime.UtcNow.AddMinutes(-10)),       // expired
            TestAuth.Token(TestAuth.AdminA, audience: "some-other-api"),                       // wrong audience
            TestAuth.Token(TestAuth.AdminA, issuer: "https://evil.example/realms/kb"),         // wrong issuer
            TestAuth.Token(TestAuth.AdminA, key: wrongKey),                                    // forged signature
            "not-a-jwt",
        };
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Invalid_tokens_are_rejected_with_401(string token)
    {
        var client = factory.CreateClientWithToken(token);

        var response = await client.GetAsync("/api/v1/workspaces");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_without_a_tenant_is_refused_with_403()
    {
        var client = factory.CreateClientWithToken(TestAuth.Token(TestAuth.AdminA, includeTenant: false));

        var response = await client.GetAsync("/api/v1/workspaces");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_identity_tenant_and_roles()
    {
        var me = await factory.CreateClientAs(TestAuth.EditorA1).GetFromJsonAsync<MeResponse>("/api/v1/me");

        Assert.Equal(TestAuth.EditorA1.Sub, me!.UserId);
        Assert.Equal(TestAuth.TenantA, me.TenantId);
        Assert.Equal(["editor"], me.Roles);
    }

    // ---- Role policies ----

    [Fact]
    public async Task Viewer_cannot_create_workspaces()
    {
        var response = await factory.CreateClientAs(TestAuth.ViewerA).PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(Unique("ws"), null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Editor_can_create_but_only_admin_can_delete_workspaces()
    {
        var editor = factory.CreateClientAs(TestAuth.EditorA1);
        var workspace = await CreateWorkspaceAsync(editor);

        var editorDelete = await editor.DeleteAsync($"/api/v1/workspaces/{workspace.Id}");
        var adminDelete = await factory.AdminClient().DeleteAsync($"/api/v1/workspaces/{workspace.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, editorDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, adminDelete.StatusCode);
    }

    [Fact]
    public async Task Only_admins_can_read_audit_and_manage_members()
    {
        var editor = factory.CreateClientAs(TestAuth.EditorA1);
        var workspace = await CreateWorkspaceAsync(editor);

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/api/v1/workspaces/{workspace.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutMember(editor, workspace.Id, "someone", "Viewer")).StatusCode);
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task Another_tenants_admin_cannot_see_or_touch_a_workspace()
    {
        var workspace = await CreateWorkspaceAsync(factory.AdminClient(), Unique("tenant-a"));
        var otherAdmin = factory.CreateClientAs(TestAuth.AdminB);

        var get = await otherAdmin.GetAsync($"/api/v1/workspaces/{workspace.Id}");
        var put = await otherAdmin.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Id}", new UpdateWorkspaceRequest("hijack", null, workspace.Version));
        var delete = await otherAdmin.DeleteAsync($"/api/v1/workspaces/{workspace.Id}");
        var docs = await otherAdmin.GetAsync($"/api/v1/workspaces/{workspace.Id}/documents");
        var members = await otherAdmin.GetAsync($"/api/v1/workspaces/{workspace.Id}/members");
        var list = await otherAdmin.GetFromJsonAsync<PagedResult<WorkspaceResponse>>($"/api/v1/workspaces?q={workspace.Name}");

        Assert.All([get, put, delete, docs, members], r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Empty(list!.Items);

        // And the owner is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await factory.AdminClient().GetAsync($"/api/v1/workspaces/{workspace.Id}")).StatusCode);
    }

    [Fact]
    public async Task Cannot_add_a_document_to_another_tenants_workspace()
    {
        var workspace = await CreateWorkspaceAsync(factory.AdminClient());

        var response = await TestFiles.UploadAsync(factory.CreateClientAs(TestAuth.AdminB), workspace.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_no_tenant_context_sees_no_rows_at_all()
    {
        await CreateWorkspaceAsync(factory.AdminClient());

        // A DbContext that never had a tenant set must be closed, not open: the filter matches nothing.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(0, await db.Workspaces.CountAsync());
        Assert.Equal(0, await db.Documents.CountAsync());
        Assert.Equal(0, await db.WorkspaceMembers.CountAsync());
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }

    // ---- Workspace membership (resource-based authorization) ----

    [Fact]
    public async Task Non_admin_only_sees_workspaces_they_belong_to_and_gets_404_for_others()
    {
        var owner = factory.CreateClientAs(TestAuth.EditorA1);
        var outsider = factory.CreateClientAs(TestAuth.EditorA2);
        var workspace = await CreateWorkspaceAsync(owner, Unique("private"));

        var get = await outsider.GetAsync($"/api/v1/workspaces/{workspace.Id}");
        var list = await outsider.GetFromJsonAsync<PagedResult<WorkspaceResponse>>($"/api/v1/workspaces?q={workspace.Name}");
        var ownerGet = await owner.GetAsync($"/api/v1/workspaces/{workspace.Id}");
        var adminList = await factory.AdminClient()
            .GetFromJsonAsync<PagedResult<WorkspaceResponse>>($"/api/v1/workspaces?q={workspace.Name}");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode); // 404, not 403: existence is not revealed
        Assert.Empty(list!.Items);
        Assert.Equal(HttpStatusCode.OK, ownerGet.StatusCode);
        Assert.Single(adminList!.Items); // admins see the whole tenant
    }

    [Fact]
    public async Task Membership_lifecycle_controls_read_and_write_access()
    {
        var admin = factory.AdminClient();
        var owner = factory.CreateClientAs(TestAuth.EditorA1);
        var guest = factory.CreateClientAs(TestAuth.EditorA2);
        var workspace = await CreateWorkspaceAsync(owner);
        var docUrl = $"/api/v1/workspaces/{workspace.Id}/documents";

        // Viewer membership: can read, cannot write.
        Assert.Equal(HttpStatusCode.Created, (await PutMember(admin, workspace.Id, TestAuth.EditorA2.Sub, "Viewer")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(docUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TestFiles.UploadAsync(guest, workspace.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Id}", new UpdateWorkspaceRequest("x", null, workspace.Version))).StatusCode);

        // Upgrade to Editor: can write.
        Assert.Equal(HttpStatusCode.OK, (await PutMember(admin, workspace.Id, TestAuth.EditorA2.Sub, "Editor")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await TestFiles.UploadAsync(guest, workspace.Id)).StatusCode);

        // Removal: the workspace disappears again.
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/v1/workspaces/{workspace.Id}/members/{TestAuth.EditorA2.Sub}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(docUrl)).StatusCode);
    }

    [Fact]
    public async Task Tenant_viewer_role_cannot_write_even_as_an_editor_member()
    {
        var admin = factory.AdminClient();
        var workspace = await CreateWorkspaceAsync(factory.CreateClientAs(TestAuth.EditorA1));
        await PutMember(admin, workspace.Id, TestAuth.ViewerA.Sub, "Editor");

        // The token's tenant-wide role is the ceiling: membership cannot elevate a viewer.
        var response = await TestFiles.UploadAsync(factory.CreateClientAs(TestAuth.ViewerA), workspace.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_role_is_validated()
    {
        var workspace = await CreateWorkspaceAsync(factory.AdminClient());

        var response = await PutMember(factory.AdminClient(), workspace.Id, "someone", "Owner");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Audit log ----

    [Fact]
    public async Task Audit_log_records_who_changed_what_for_the_callers_tenant_only()
    {
        var admin = factory.AdminClient();
        var workspace = await CreateWorkspaceAsync(admin);
        await admin.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Id}", new UpdateWorkspaceRequest(Unique("renamed"), null, workspace.Version));
        await admin.DeleteAsync($"/api/v1/workspaces/{workspace.Id}");

        var audit = await admin.GetFromJsonAsync<PagedResult<AuditLogResponse>>(
            $"/api/v1/audit?entityType=Workspace&entityId={workspace.Id}");
        var actions = audit!.Items.Select(a => a.Action).Order().ToList();
        var updated = audit.Items.Single(a => a.Action == "Updated");

        Assert.Equal(["Created", "Deleted", "Updated"], actions);
        Assert.All(audit.Items, a => Assert.Equal(TestAuth.AdminA.Sub, a.UserId));
        Assert.Equal("Name", updated.ChangedFields); // property names only, never values
        Assert.All(audit.Items, a => Assert.False(string.IsNullOrEmpty(a.CorrelationId)));

        var otherTenant = await factory.CreateClientAs(TestAuth.AdminB).GetFromJsonAsync<PagedResult<AuditLogResponse>>(
            $"/api/v1/audit?entityId={workspace.Id}");
        Assert.Empty(otherTenant!.Items);
    }

    [Fact]
    public async Task Membership_changes_are_audited()
    {
        var admin = factory.AdminClient();
        var workspace = await CreateWorkspaceAsync(admin);
        await PutMember(admin, workspace.Id, "audited-user", "Viewer");

        var audit = await admin.GetFromJsonAsync<PagedResult<AuditLogResponse>>("/api/v1/audit?entityType=WorkspaceMember&pageSize=100");

        // At least the creator membership and the added member, both attributed to the admin.
        Assert.True(audit!.TotalCount >= 2);
        Assert.Contains(audit.Items, a => a.Action == "Created" && a.UserId == TestAuth.AdminA.Sub);
    }

    // ---- Per-user scoping of rate limits and idempotency ----

    [Fact]
    public async Task Rate_limits_are_tracked_per_user()
    {
        using var limited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:PermitLimit", "2");
            b.UseSetting("RateLimiting:KeyPrefix", $"rl-test-{Guid.NewGuid():N}");
        });
        var first = CreateClient(limited, TestAuth.EditorA1);
        var second = CreateClient(limited, TestAuth.EditorA2);

        var firstStatuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            firstStatuses.Add((await first.GetAsync("/api/v1/me")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], firstStatuses);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/me")).StatusCode); // unaffected
    }

    private static HttpClient CreateClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestAuth.Token(user));
        return client;
    }

    [Fact]
    public async Task Idempotency_keys_are_not_shared_between_users()
    {
        var key = Guid.NewGuid().ToString();
        var name = Unique("shared-key");
        HttpRequestMessage Create() => new(HttpMethod.Post, "/api/v1/workspaces")
        {
            Content = JsonContent.Create(new CreateWorkspaceRequest(name, null)),
            Headers = { { "Idempotency-Key", key } },
        };

        var fromAdmin = await factory.AdminClient().SendAsync(Create());
        var fromEditor = await factory.CreateClientAs(TestAuth.EditorA1).SendAsync(Create());

        // Same key and body, different callers: each gets its own workspace, never the other's response.
        Assert.Equal(HttpStatusCode.Created, fromAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fromEditor.StatusCode);
        Assert.False(fromEditor.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(fromAdmin.Headers.Location, fromEditor.Headers.Location);
    }

    [Fact]
    public async Task Unauthorized_requests_are_never_stored_for_idempotent_replay()
    {
        var key = Guid.NewGuid().ToString();
        var viewer = factory.CreateClientAs(TestAuth.ViewerA);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/workspaces")
        {
            Content = JsonContent.Create(new CreateWorkspaceRequest(Unique("denied"), null)),
            Headers = { { "Idempotency-Key", key } },
        };

        var response = await viewer.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(403, body.RootElement.GetProperty("status").GetInt32());
    }
}
