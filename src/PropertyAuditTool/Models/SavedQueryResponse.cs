namespace PropertyAuditTool.Models;

/// <summary>Saved query record returned to the client.</summary>
public class SavedQueryResponse
{
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    public bool IsShared { get; set; }

    public bool IsOwner { get; set; }

    /// <summary>Display-name snapshot; not an authorization identifier.</summary>
    public required string OwnerName { get; set; }

    public required SavedQueryDefinition Query { get; set; }

    public int Version { get; set; }

    public DateTime UpdatedDate { get; set; }
}
