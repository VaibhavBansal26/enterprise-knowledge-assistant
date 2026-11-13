using System.Net;
using System.Net.Http.Json;
using Application.Common;
using Application.Documents;
using Application.Workspaces;

namespace Api.Tests;

[Collection(ApiFixtureGroup.Name)]
public sealed class DocumentTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.AdminClient();

    private async Task<Guid> CreateWorkspaceAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest($"ws-{Guid.NewGuid():N}", null));
        return (await response.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
    }

    private static CreateDocumentRequest NewDocument(string title, Dictionary<string, string>? metadata = null) =>
        new(title, "file.pdf", "application/pdf", 1234, metadata);

    private async Task<DocumentResponse> CreateAsync(Guid workspaceId, string title, Dictionary<string, string>? metadata = null)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/documents", NewDocument(title, metadata));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }

    [Fact]
    public async Task Create_persists_jsonb_metadata_and_starts_pending()
    {
        var workspaceId = await CreateWorkspaceAsync();

        var created = await CreateAsync(workspaceId, "Handbook", new() { ["team"] = "hr", ["year"] = "2026" });
        var fetched = await _client.GetFromJsonAsync<DocumentResponse>(
            $"/api/v1/workspaces/{workspaceId}/documents/{created.Id}");

        Assert.Equal("Pending", fetched!.Status);
        Assert.Equal("hr", fetched.Metadata["team"]);
        Assert.Equal("2026", fetched.Metadata["year"]);
    }

    [Fact]
    public async Task Create_in_unknown_workspace_returns_404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.NewGuid()}/documents", NewDocument("x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_validates_input()
    {
        var workspaceId = await CreateWorkspaceAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/documents",
            new CreateDocumentRequest("", "f.pdf", "application/pdf", 0, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_is_scoped_to_the_workspace_and_filterable()
    {
        var a = await CreateWorkspaceAsync();
        var b = await CreateWorkspaceAsync();
        await CreateAsync(a, "Alpha plan");
        await CreateAsync(a, "Beta plan");
        await CreateAsync(b, "Other workspace doc");

        var all = await _client.GetFromJsonAsync<PagedResult<DocumentResponse>>($"/api/v1/workspaces/{a}/documents");
        var filtered = await _client.GetFromJsonAsync<PagedResult<DocumentResponse>>(
            $"/api/v1/workspaces/{a}/documents?q=alpha");

        Assert.Equal(2, all!.TotalCount);
        Assert.Equal(["Alpha plan"], filtered!.Items.Select(d => d.Title));
    }

    [Fact]
    public async Task Document_is_not_reachable_through_another_workspace()
    {
        var a = await CreateWorkspaceAsync();
        var b = await CreateWorkspaceAsync();
        var doc = await CreateAsync(a, "Private");

        var response = await _client.GetAsync($"/api/v1/workspaces/{b}/documents/{doc.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_and_stale_update_behave_with_optimistic_concurrency()
    {
        var workspaceId = await CreateWorkspaceAsync();
        var doc = await CreateAsync(workspaceId, "Draft");
        var url = $"/api/v1/workspaces/{workspaceId}/documents/{doc.Id}";

        var ok = await _client.PutAsJsonAsync(url, new UpdateDocumentRequest("Final", new Dictionary<string, string> { ["k"] = "v" }, doc.Version));
        var stale = await _client.PutAsJsonAsync(url, new UpdateDocumentRequest("Again", null, doc.Version));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Final", (await ok.Content.ReadFromJsonAsync<DocumentResponse>())!.Title);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Delete_is_soft_and_hides_the_document()
    {
        var workspaceId = await CreateWorkspaceAsync();
        var doc = await CreateAsync(workspaceId, "Temp");
        var url = $"/api/v1/workspaces/{workspaceId}/documents/{doc.Id}";

        var deleted = await _client.DeleteAsync(url);
        var after = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }
}
