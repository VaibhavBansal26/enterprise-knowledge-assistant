using System.Text;
using Application.Ingestion;
using Format = Application.Ingestion.DocumentFormat;
using Domain;
using Infrastructure.Ingestion;

namespace Api.Tests;

public sealed class TextChunkerTests
{
    [Fact]
    public void Empty_or_whitespace_text_yields_no_chunks()
    {
        Assert.Empty(TextChunker.Chunk(""));
        Assert.Empty(TextChunker.Chunk("  \n\n \t "));
    }

    [Fact]
    public void Short_text_is_a_single_chunk()
    {
        var chunks = TextChunker.Chunk("A short note.\n\nWith two paragraphs.");

        Assert.Equal(["A short note. With two paragraphs."], chunks);
    }

    [Fact]
    public void Long_text_is_split_and_no_chunk_exceeds_the_limit()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"Paragraph number {i} with a few words to take up room."));

        var chunks = TextChunker.Chunk(text, maxChars: 300, overlapChars: 40);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 300));
    }

    [Fact]
    public void Consecutive_chunks_overlap_so_context_is_not_lost_at_the_boundary()
    {
        var text = string.Join(' ', Enumerable.Range(1, 200).Select(i => $"word{i}."));

        var chunks = TextChunker.Chunk(text, maxChars: 200, overlapChars: 50);

        for (var i = 1; i < chunks.Count; i++)
        {
            var firstWordOfNext = chunks[i].Split(' ')[0];
            Assert.Contains(firstWordOfNext, chunks[i - 1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_word_of_the_input_survives_chunking()
    {
        var words = Enumerable.Range(1, 400).Select(i => $"token{i}").ToList();

        var chunks = TextChunker.Chunk(string.Join(' ', words), maxChars: 250, overlapChars: 30);
        var covered = string.Join(' ', chunks);

        Assert.All(words, w => Assert.Contains(w, covered, StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_enormous_word_is_hard_split_within_the_limit()
    {
        var chunks = TextChunker.Chunk(new string('a', 2500), maxChars: 1000, overlapChars: 100);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 1000));
    }

    [Fact]
    public void Windows_line_endings_and_NUL_bytes_are_normalised()
    {
        var chunks = TextChunker.Chunk("one\r\n\r\ntwo\0three");

        Assert.Equal(["one twothree"], chunks);
    }

    [Fact]
    public void Chunking_is_deterministic()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Section {i}. " + new string('x', 120)));

        Assert.Equal(TextChunker.Chunk(text, 300, 40), TextChunker.Chunk(text, 300, 40));
    }
}

public sealed class HashingEmbeddingTests
{
    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

    [Fact]
    public void Embeddings_have_the_column_dimension_and_unit_length()
    {
        var v = HashingEmbeddingClient.Embed("The quick brown fox jumps over the lazy dog");

        Assert.Equal(DocumentChunk.EmbeddingDimensions, v.Length);
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), precision: 5);
    }

    [Fact]
    public void Embedding_is_deterministic_and_case_insensitive()
    {
        Assert.Equal(HashingEmbeddingClient.Embed("Hello World"), HashingEmbeddingClient.Embed("hello   WORLD!"));
    }

    [Fact]
    public void Texts_sharing_vocabulary_are_closer_than_unrelated_texts()
    {
        var query = HashingEmbeddingClient.Embed("renewable solar energy panels");
        var related = HashingEmbeddingClient.Embed("solar panels generate renewable energy from sunlight");
        var unrelated = HashingEmbeddingClient.Embed("the lawyer reviewed the merger agreement clauses");

        Assert.True(Cosine(query, related) > Cosine(query, unrelated) + 0.2);
    }

    [Fact]
    public void Text_without_words_still_yields_a_valid_non_zero_vector()
    {
        var v = HashingEmbeddingClient.Embed("... !!! ???");

        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), precision: 5);
    }

    [Fact]
    public async Task The_client_embeds_a_batch_in_order()
    {
        var result = await new HashingEmbeddingClient().EmbedAsync(["alpha", "beta"], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(HashingEmbeddingClient.Embed("beta"), result[1]);
    }
}

public sealed class SupportedFormatsTests
{
    [Theory]
    [InlineData("a.pdf", Format.Pdf)]
    [InlineData("A.PDF", Format.Pdf)]
    [InlineData("memo.docx", Format.Docx)]
    [InlineData("readme.md", Format.Markdown)]
    [InlineData("notes.markdown", Format.Markdown)]
    [InlineData("plain.txt", Format.PlainText)]
    public void Known_extensions_map_to_formats(string name, Format expected) =>
        Assert.Equal(expected, SupportedFormats.FromFileName(name));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("doc.pdf.exe")]
    public void Other_extensions_are_unsupported(string name) =>
        Assert.Null(SupportedFormats.FromFileName(name));

    [Fact]
    public void Content_type_round_trips_for_every_format()
    {
        foreach (var format in Enum.GetValues<Format>())
        {
            Assert.Equal(format, SupportedFormats.FromContentType(SupportedFormats.ContentTypeOf(format)));
        }
    }

    [Fact]
    public void Header_signatures_are_checked_per_format()
    {
        Assert.True(SupportedFormats.HeaderMatches(Format.Pdf, "%PDF-1.7 ..."u8));
        Assert.False(SupportedFormats.HeaderMatches(Format.Pdf, "MZ\x90\x00"u8));
        Assert.True(SupportedFormats.HeaderMatches(Format.Docx, "PK\x03\x04rest"u8));
        Assert.False(SupportedFormats.HeaderMatches(Format.Docx, "%PDF-1.7"u8));
        Assert.True(SupportedFormats.HeaderMatches(Format.Markdown, "# Title"u8));
    }

    [Fact]
    public void Text_formats_reject_binary_and_invalid_utf8_but_accept_multibyte_text()
    {
        Assert.False(SupportedFormats.HeaderMatches(Format.PlainText, [0x41, 0x00, 0x42]));        // NUL byte
        Assert.False(SupportedFormats.HeaderMatches(Format.PlainText, [0xFF, 0xFE, 0xFD]));        // invalid UTF-8
        Assert.True(SupportedFormats.HeaderMatches(Format.PlainText, Encoding.UTF8.GetBytes("Café ☕ 日本語")));
    }

    [Fact]
    public void A_header_sample_cut_in_the_middle_of_a_character_is_still_accepted()
    {
        var bytes = Encoding.UTF8.GetBytes("abc日本語");
        var cut = bytes.AsSpan(0, bytes.Length - 1).ToArray(); // last 3-byte character is incomplete

        Assert.True(SupportedFormats.HeaderMatches(Format.Markdown, cut));
    }
}

public sealed class RetryBackoffTests
{
    [Fact]
    public void Delay_doubles_each_attempt_with_bounded_jitter()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(OutboxWorker.BackoffFor(1, 5).TotalSeconds, 5, 6);
            Assert.InRange(OutboxWorker.BackoffFor(2, 5).TotalSeconds, 10, 12);
            Assert.InRange(OutboxWorker.BackoffFor(3, 5).TotalSeconds, 20, 24);
        }
    }

    [Fact]
    public void Delay_is_capped_at_fifteen_minutes_plus_jitter()
    {
        Assert.True(OutboxWorker.BackoffFor(40, 5).TotalSeconds <= 15 * 60 * 1.2 + 0.001);
    }

    [Fact]
    public void A_zero_base_means_retry_immediately()
    {
        Assert.Equal(TimeSpan.Zero, OutboxWorker.BackoffFor(3, 0));
    }
}
