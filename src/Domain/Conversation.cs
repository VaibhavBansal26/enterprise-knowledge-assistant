namespace Domain;

/// <summary>A private chat between one user and the assistant, optionally scoped to a single workspace.</summary>
public sealed class Conversation : Entity, ITenantOwned
{
    // Required by EF Core.
    private Conversation() { }

    public Conversation(Guid tenantId, string userId, Guid? workspaceId, string title)
    {
        TenantId = tenantId;
        UserId = userId;
        WorkspaceId = workspaceId;
        Title = title;
        UpdatedAt = CreatedAt;
    }

    public Guid TenantId { get; private set; }

    /// <summary>The owner. Conversations are private: not even tenant admins can read someone else's.</summary>
    public string UserId { get; private set; } = string.Empty;

    /// <summary>When set, answers draw only on this workspace; otherwise on everything the user may read.</summary>
    public Guid? WorkspaceId { get; private set; }

    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string title) => Title = title;

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

public enum ChatRole
{
    User,
    Assistant,
}

public sealed class ChatMessage : Entity, ITenantOwned
{
    public const string FeedbackUp = "up";
    public const string FeedbackDown = "down";

    // Required by EF Core.
    private ChatMessage() { }

    public ChatMessage(Guid tenantId, Guid conversationId, ChatRole role, string content)
    {
        TenantId = tenantId;
        ConversationId = conversationId;
        Role = role;
        Content = content;
    }

    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public ChatRole Role { get; private set; }
    public string Content { get; private set; } = string.Empty;

    /// <summary>JSON array of the sources the answer cited (assistant messages only).</summary>
    public string? CitationsJson { get; private set; }

    /// <summary>The model that produced the answer, or a marker such as "none" when no model was called.</summary>
    public string? Model { get; private set; }

    /// <summary>The prompt template version used, so answers can be traced back to the exact instructions.</summary>
    public string? PromptVersion { get; private set; }

    public bool FromCache { get; private set; }

    /// <summary>Fraction of the answer's sentences supported by its sources (0-1); null when not measured.</summary>
    public double? GroundingScore { get; private set; }

    /// <summary>True when the grounding score fell below the configured threshold: the answer may not stay within its sources.</summary>
    public bool LowGrounding { get; private set; }

    public int? InputTokens { get; private set; }
    public int? OutputTokens { get; private set; }

    /// <summary>"up", "down" or null.</summary>
    public string? Feedback { get; private set; }

    public void CompleteAssistant(
        string? citationsJson,
        string model,
        string promptVersion,
        bool fromCache,
        int? inputTokens,
        int? outputTokens,
        double? groundingScore = null,
        bool lowGrounding = false)
    {
        GroundingScore = groundingScore;
        LowGrounding = lowGrounding;
        CitationsJson = citationsJson;
        Model = model;
        PromptVersion = promptVersion;
        FromCache = fromCache;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public void SetFeedback(string? feedback) => Feedback = feedback;
}
