namespace Bassia.Tests;

public class CommitProvenanceTests
{
	[Fact]
	public void Parse_ResultCommitBody_NamesTheRun()
	{
		var provenance = CommitProvenance.Parse("[agentic_run]\nid = \"agent-run-magical-otter-vt9j3p\"\nsummary = \"x\"\n[agentic_run.component]\nname = \"app\"\n");

		Assert.Equal(new CommitProvenance(CommitProvenance.Result, "agent-run-magical-otter-vt9j3p", null), provenance);
	}

	[Fact]
	public void Parse_IntegrationMergeBody_NamesTheIntegrationAndTheRunItMerged()
	{
		var provenance = CommitProvenance.Parse("[integration]\nid = \"integration-ab\"\ncomponent = \"app\"\nrun_id = \"agent-run-cd\"\n");

		Assert.Equal(new CommitProvenance(CommitProvenance.Integration, "agent-run-cd", "integration-ab"), provenance);
	}

	[Theory]
	[InlineData("")]
	[InlineData("Fix the parser.\n\nSigned-off-by: someone")]
	[InlineData("[agentic_run]\nid = not toml")]
	[InlineData("[agentic_run]\nid = \"not-a-run\"\n")]
	public void Parse_TextWithoutBassiasRecord_HasNoProvenance(string body) => Assert.Null(CommitProvenance.Parse(body));
}
