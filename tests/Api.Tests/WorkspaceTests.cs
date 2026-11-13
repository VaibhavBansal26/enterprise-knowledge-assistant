using System.Net;
using System.Net.Http.Json;
using Application.Common;
using Application.Workspaces;

namespace Api.Tests;

[Collection(ApiFixtureGroup.Name)]
public sealed class WorkspaceTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<WorkspaceResponse> CreateAsync(string name, string? description = null)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest(name, description));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkspaceResponse>())!;
    }

    [Fact]
    public async Task Create_returns_201_with_location_and_body()
    {
        var name = Unique("ws");

        var response = await _client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest(name, "desc"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.Equal(name, created!.Name);
        Assert.Equal($"/api/v1/workspaces/{created.Id}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Create_with_empty_name_returns_400_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest("", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Name", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_returns_workspace_and_404_for_unknown_id()
    {
        var created = await CreateAsync(Unique("ws"));

        var found = await _client.GetAsync($"/api/v1/workspaces/{created.Id}");
        var missing = await _client.GetAsync($"/api/v1/workspaces/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task List_supports_filter_sort_and_paging()
    {
        var tag = Unique("list");
        await CreateAsync($"{tag}-b");
        await CreateAsync($"{tag}-a");
        await CreateAsync($"{tag}-c");

        var page = await _client.GetFromJsonAsync<PagedResult<WorkspaceResponse>>(
            $"/api/v1/workspaces?q={tag}&sort=name&page=1&pageSize=2");

        Assert.Equal(3, page!.TotalCount);
        Assert.Equal([$"{tag}-a", $"{tag}-b"], page.Items.Select(i => i.Name));

        var second = await _client.GetFromJsonAsync<PagedResult<WorkspaceResponse>>(
            $"/api/v1/workspaces?q={tag}&sort=name&page=2&pageSize=2");
        Assert.Equal([$"{tag}-c"], second!.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task List_rejects_bad_page_size_and_unknown_sort()
    {
        var tooBig = await _client.GetAsync("/api/v1/workspaces?pageSize=1000");
        var badSort = await _client.GetAsync("/api/v1/workspaces?sort=password");

        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);
    }

    [Fact]
    public async Task Update_changes_fields_and_bumps_version()
    {
        var created = await CreateAsync(Unique("ws"));

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/workspaces/{created.Id}",
            new UpdateWorkspaceRequest("renamed", "new", created.Version));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
        Assert.Equal("renamed", updated!.Name);
        Assert.NotEqual(created.Version, updated.Version);
    }

    [Fact]
    public async Task Update_with_stale_version_returns_409()
    {
        var created = await CreateAsync(Unique("ws"));
        var first = await _client.PutAsJsonAsync(
            $"/api/v1/workspaces/{created.Id}", new UpdateWorkspaceRequest("first", null, created.Version));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await _client.PutAsJsonAsync(
            $"/api/v1/workspaces/{created.Id}", new UpdateWorkspaceRequest("second", null, created.Version));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Delete_is_soft_and_hides_the_workspace()
    {
        var created = await CreateAsync(Unique("ws"));

        var deleted = await _client.DeleteAsync($"/api/v1/workspaces/{created.Id}");
        var afterGet = await _client.GetAsync($"/api/v1/workspaces/{created.Id}");
        var afterDelete = await _client.DeleteAsync($"/api/v1/workspaces/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }
}
