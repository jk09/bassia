namespace Bassia.Feedback;

/// <summary>A feedback or crash report that could not be built or sent; reported as the command's TOML error.</summary>
internal sealed class FeedbackException(string message, Exception? inner = null) : Exception(message, inner);
