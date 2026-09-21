namespace Bassia.CliCommands.Agent;

/// <summary>What a finished <c>bassia agent -select ... -run ...</c> reports: the CLI serializes it, the frontend renders it.</summary>
internal sealed record AgentRunOutcome(bool Ok, string Message, RunMetadata Metadata, RunMetadataStore Store, IReadOnlyList<string> MetadataTags);
