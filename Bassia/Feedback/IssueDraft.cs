namespace Bassia.Feedback;

/// <summary>
/// An issue as it will be filed: shown to the user in full before anything is sent. A crash report carries its
/// <see cref="Fingerprint"/>, which lets the same crash be recognized instead of filed twice.
/// </summary>
internal sealed record IssueDraft(string Repository, string Title, string Body, IReadOnlyList<string> Labels, string? Fingerprint = null);

/// <summary>An issue that was created (or, for a known crash, commented on).</summary>
internal sealed record IssueReceipt(int Number, string Url, bool Duplicate);

/// <summary>The transport to the issue tracker; replaced in tests so nothing reaches GitHub.</summary>
internal interface IIssueSubmitter
{
	/// <summary>Whether credentials are available; without them <see cref="SubmitAsync"/> cannot succeed.</summary>
	bool IsConfigured { get; }

	/// <summary>Files the issue, or comments on the open issue of the same fingerprint. Throws <see cref="FeedbackException"/>.</summary>
	Task<IssueReceipt> SubmitAsync(IssueDraft draft, CancellationToken cancellation);
}
