using Application.Chat;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Infrastructure.Chat;

/// <summary>
/// Hybrid retrieval in a single SQL statement: the best vector matches (pgvector cosine distance, HNSW) and the
/// best full-text matches (Postgres <c>websearch_to_tsquery</c>) are merged with Reciprocal Rank Fusion, which
/// rewards a passage that ranks well in either list and best of all in both, without having to calibrate two
/// incomparable score scales.
///
/// The permission rules live inside the query (the <c>visible</c> CTE): same tenant, workspace membership (or tenant
/// admin), workspace and document not deleted, document fully ingested, and embedded by the current model. Nothing
/// is filtered after the fact in application code, so no caller or later refactor can leak a chunk.
/// </summary>
public sealed class PostgresRetrievalService(NpgsqlDataSource dataSource) : IRetrievalService
{
    private const string VisibleFilter = """
        c.tenant_id = @tenant
        AND c.embedding_model = @model
        AND d.tenant_id = @tenant AND NOT d.is_deleted AND d.status = 'Ready'
        AND w.tenant_id = @tenant AND NOT w.is_deleted
        AND (@ws::uuid IS NULL OR c.workspace_id = @ws::uuid)
        AND (@admin OR EXISTS (
              SELECT 1 FROM workspace_members m
               WHERE m.workspace_id = c.workspace_id AND m.user_id = @user AND m.tenant_id = @tenant))
        """;

    private const string SearchSql = $"""
        WITH visible AS NOT MATERIALIZED (
            SELECT c.id, c.document_id, c.workspace_id, c.index, c.content, c.embedding, c.search_vector, d.title
              FROM document_chunks c
              JOIN documents d  ON d.id = c.document_id
              JOIN workspaces w ON w.id = c.workspace_id
             WHERE {VisibleFilter}
        ),
        vec AS (
            SELECT id, ROW_NUMBER() OVER (ORDER BY embedding <=> @q) AS rnk, 1 - (embedding <=> @q) AS sim
              FROM visible
             ORDER BY embedding <=> @q
             LIMIT @candidates
        ),
        fts AS (
            SELECT id, ROW_NUMBER() OVER (ORDER BY ts_rank_cd(search_vector, qry) DESC) AS rnk
              FROM visible, websearch_to_tsquery('english', @text) qry
             WHERE search_vector @@ qry
             ORDER BY ts_rank_cd(search_vector, qry) DESC
             LIMIT @candidates
        )
        SELECT v.id, v.document_id, v.workspace_id, v.title, v.index, v.content,
               COALESCE(1.0 / (60 + vec.rnk), 0) + COALESCE(1.0 / (60 + fts.rnk), 0) AS score
          FROM visible v
          LEFT JOIN vec ON vec.id = v.id
          LEFT JOIN fts ON fts.id = v.id
         WHERE (vec.id IS NOT NULL AND vec.sim >= @minsim) OR fts.id IS NOT NULL
         ORDER BY score DESC, v.document_id, v.index
         LIMIT @limit
        """;

    private const string CanReadSql = """
        SELECT count(*)
          FROM documents d
          JOIN workspaces w ON w.id = d.workspace_id
         WHERE d.id = ANY(@ids)
           AND d.tenant_id = @tenant AND NOT d.is_deleted AND d.status = 'Ready'
           AND w.tenant_id = @tenant AND NOT w.is_deleted
           AND (@admin OR EXISTS (
                 SELECT 1 FROM workspace_members m
                  WHERE m.workspace_id = d.workspace_id AND m.user_id = @user AND m.tenant_id = @tenant))
        """;

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var cmd = dataSource.CreateCommand(SearchSql);
        cmd.Parameters.AddWithValue("tenant", query.TenantId);
        cmd.Parameters.AddWithValue("model", query.EmbeddingModel);
        cmd.Parameters.AddWithValue("user", query.UserId);
        cmd.Parameters.AddWithValue("admin", query.IsAdmin);
        cmd.Parameters.Add(new NpgsqlParameter("ws", NpgsqlDbType.Uuid) { Value = (object?)query.WorkspaceId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("q", new Vector(query.QueryEmbedding)));
        cmd.Parameters.AddWithValue("text", ToOrQuery(query.QueryText));
        cmd.Parameters.AddWithValue("minsim", query.MinSimilarity);
        // Fetch more candidates than we return: fusion needs depth in both lists to work well.
        cmd.Parameters.AddWithValue("candidates", Math.Max(query.Limit * 4, 20));
        cmd.Parameters.AddWithValue("limit", query.Limit);

        var results = new List<RetrievedChunk>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RetrievedChunk(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetDouble(6)));
        }

        return results;
    }

    /// <summary>
    /// A natural-language question is a poor AND-query (every word must appear), so the words are OR-ed together:
    /// <c>ts_rank_cd</c> then ranks passages by how many of them they contain, and the vector arm supplies precision.
    /// Only letters and digits survive, so user text can never inject query operators.
    /// </summary>
    public static string ToOrQuery(string text)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), "[\\p{L}\\p{N}]+")
            .Select(m => m.Value)
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.Ordinal)
            .Take(24);
        return string.Join(" or ", words);
    }

    public async Task<bool> CanReadAllAsync(
        Guid tenantId, string userId, bool isAdmin, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        var distinct = documentIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return true;
        }

        await using var cmd = dataSource.CreateCommand(CanReadSql);
        cmd.Parameters.AddWithValue("ids", distinct);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("admin", isAdmin);

        var readable = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return readable == distinct.Length;
    }
}
