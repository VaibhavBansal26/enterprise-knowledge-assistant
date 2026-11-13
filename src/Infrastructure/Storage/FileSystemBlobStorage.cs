using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Application.Ingestion;
using Microsoft.Extensions.Options;

namespace Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Directory for uploaded files (a mounted volume in production).</summary>
    [Required, MinLength(1)]
    public string RootPath { get; init; } = "data/blobs";

    /// <summary>Largest accepted upload.</summary>
    [Range(1, 500L * 1024 * 1024)]
    public long MaxUploadBytes { get; init; } = 25L * 1024 * 1024;
}

/// <summary>
/// Local-disk blob store: <c>{root}/{tenant}/{workspace}/{random}</c>. Keys are random GUIDs, so user-supplied
/// file names never reach the file system (no path traversal). Writes go to a temp file first and are moved
/// into place, so readers never see a partial file.
/// </summary>
public sealed partial class FileSystemBlobStorage(IOptions<StorageOptions> options) : IBlobStorage
{
    [GeneratedRegex("^[0-9a-f]{32}/[0-9a-f]{32}/[0-9a-f]{32}$")]
    private static partial Regex ValidKey();

    private string Root => Path.GetFullPath(options.Value.RootPath);

    public async Task<StoredBlob> SaveAsync(
        Guid tenantId, Guid workspaceId, Stream content, CancellationToken cancellationToken)
    {
        var key = $"{tenantId:N}/{workspaceId:N}/{Guid.NewGuid():N}";
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        using var sha = SHA256.Create();
        long length = 0;

        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    length += read;
                }

                sha.TransformFinalBlock([], 0, 0);
            }

            File.Move(temp, path);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        return new StoredBlob(key, Convert.ToHexString(sha.Hash!).ToLowerInvariant(), length);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        Stream stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathFor(key)); // File.Delete ignores missing files
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        if (!ValidKey().IsMatch(key))
        {
            throw new ArgumentException("Invalid blob key.", nameof(key));
        }

        var root = Root;
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Blob key escapes the storage root.", nameof(key));
        }

        return full;
    }
}
