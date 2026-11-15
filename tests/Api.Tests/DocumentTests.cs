using System.Net;
using System.Net.Http.Json;
using Application.Common;
using Application.Documents;
using Application.Workspaces;

namespace Api.Tests;

/// <summary>Document CRUD around the upload flow. (Ingestion itself is covered in <see cref="IngestionTests"/>.)</summary>
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

    [Fact]
    public async Task Upload_returns_201_with_location_and_starts_pending()
    {
        var workspaceId = await CreateWorkspaceAsync();

        var response = await TestFiles.UploadAsync(_client, workspaceId, "Handbook.md", TestFiles.Markdown("# Hi\n\nHello"), title: "Employee handbook");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.Equal("Employee handbook", created!.Title);
        Assert.Equal("Handbook.md", created.FileName);
        Assert.Equal("text/markdown", created.ContentType);
        Assert.Equal("Pending", created.Status); // the shared host runs no worker
        Assert.Equal($"/api/v1/workspaces/{workspaceId}/documents/{created.Id}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Upload_title_defaults_to_the_file_name()
    {
        var workspaceId = await CreateWorkspaceAsync();

        var created = await TestFiles.UploadOkAsync(_client, workspaceId, "Quarterly Report.md");

        Assert.Equal("Quarterly Report", created.Title);
    }

    [Fact]
    public async Task Upload_into_unknown_workspace_returns_404()
    {
        var response = await TestFiles.UploadAsync(_client, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_scoped_to_the_workspace_and_filterable()
    {
        var a = await CreateWorkspaceAsync();
        var b = await CreateWorkspaceAsync();
        await TestFiles.UploadOkAsync(_client, a, "a1.md", title: "Alpha plan");
        await TestFiles.UploadOkAsync(_client, a, "a2.md", title: "Beta plan");
        await TestFiles.UploadOkAsync(_client, b, "b1.md", title: "Other workspace doc");

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
        var doc = await TestFiles.UploadOkAsync(_client, a);

        var response = await _client.GetAsync($"/api/v1/workspaces/{b}/documents/{doc.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_and_stale_update_behave_with_optimistic_concurrency()
    {
        var workspaceId = await CreateWorkspaceAsync();
        var doc = await TestFiles.UploadOkAsync(_client, workspaceId, title: "Draft");
        var url = $"/api/v1/workspaces/{workspaceId}/documents/{doc.Id}";

        var ok = await _client.PutAsJsonAsync(url, new UpdateDocumentRequest("Final", new Dictionary<string, string> { ["k"] = "v" }, doc.Version));
        var stale = await _client.PutAsJsonAsync(url, new UpdateDocumentRequest("Again", null, doc.Version));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.Equal("Final", updated!.Title);
        Assert.Equal("v", updated.Metadata["k"]);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Delete_is_soft_and_hides_the_document()
    {
        var workspaceId = await CreateWorkspaceAsync();
        var doc = await TestFiles.UploadOkAsync(_client, workspaceId);
        var url = $"/api/v1/workspaces/{workspaceId}/documents/{doc.Id}";

        var deleted = await _client.DeleteAsync(url);
        var after = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }
}
