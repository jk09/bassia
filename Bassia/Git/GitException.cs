namespace Bassia.Git;

internal sealed class GitException(string message) : Storage.StorageException(message);
