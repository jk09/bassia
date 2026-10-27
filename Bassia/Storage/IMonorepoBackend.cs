namespace Bassia.Storage;

/// <summary>
/// Everything a monorepo keeps, each behind its contract: the component histories, the registry, the run and integration
/// records, the lock table, the live jobs, the task cache, who acts, and the settings. Commands, the dashboard and the
/// integration get them from here (through <see cref="Monorepo"/>) and nowhere else, so a monorepo on a server is a
/// matter of another backend. <see cref="LocalBackend"/> is the monorepo on this machine's disk.
/// </summary>
internal interface IMonorepoBackend
{
	/// <summary>The monorepo root on this machine: where its views, workspace and caches are.</summary>
	string Root { get; }

	IComponentStorage Components { get; }

	IMetaStore Meta { get; }

	IRunRecordStore Records { get; }

	ILockStore Locks { get; }

	IJobScheduler Jobs { get; }

	ITaskCache TaskCache { get; }

	IIdentity Identity { get; }

	IConfigStore Config { get; }
}
