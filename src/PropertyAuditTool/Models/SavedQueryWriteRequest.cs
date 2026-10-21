namespace PropertyAuditTool.Models;

/// <summary>
/// Write payload for creating or updating a saved query. The owner is always
/// the authenticated user; owner keys are never accepted.
/// </summary>
public class SavedQueryWriteRequest
{
    public required string Name { get; set; }

    public bool IsShared { get; set; }

    public required SavedQueryDefinition Query { get; set; }

    /// <summary>Required for updates (optimistic concurrency); ignored on create.</summary>
    public int? Version { get; set; }
}
