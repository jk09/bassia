namespace Bassia.Core;

/// <summary>What a finished <c>bassia agent start -select ... -run ...</c> reports: the CLI serializes it, the frontend renders it.</summary>
internal sealed record AgentRunOutcome(bool Ok, string Message, RunMetadata Metadata, Storage.IRunRecordStore Store, IReadOnlyList<string> MetadataTags);
