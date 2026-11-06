namespace Bassia.Studio.Runs;

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;

/// <summary>How Studio starts runs as executions of an Azure Container Apps job.</summary>
public sealed record ContainerAppsJobOptions(
	string JobResourceId,
	string Image,
	string Container = "agent",
	double Cpu = 2,
	string Memory = "4Gi",
	IReadOnlyDictionary<string, string>? SecretEnvironment = null);

/// <summary>
/// Runs each job as one execution of an Azure Container Apps job (the worker image's entrypoint is
/// <c>bassia agent job</c>). An execution's template replaces the job's containers, so the per-run environment is
/// merged with the variables that come from the job's secrets (the server token, the agent's API key - kept in Key
/// Vault and referenced by the job, never seen by Studio). Studio signs in with its managed identity.
/// </summary>
public sealed class ContainerAppsJobRunHost(ContainerAppsJobOptions options, TokenCredential? credential = null) : IRunHost
{
	private readonly ArmClient arm = new(credential ?? new DefaultAzureCredential());

	public string Name => "azure-container-apps";

	/// <summary>The execution template for one run (public for tests: it is all Studio decides).</summary>
	public static ContainerAppJobExecutionTemplate Template(ContainerAppsJobOptions options, RunLaunch launch)
	{
		var container = new JobExecutionContainer
		{
			Name = options.Container,
			Image = options.Image,
			Resources = new AppContainerResources { Cpu = options.Cpu, Memory = options.Memory }
		};
		container.Command.Add("bassia");
		container.Args.Add("agent");
		container.Args.Add("job");
		foreach (var (name, value) in launch.Environment.OrderBy(item => item.Key, StringComparer.Ordinal))
		{
			container.Env.Add(new ContainerAppEnvironmentVariable { Name = name, Value = value });
		}

		foreach (var (name, secret) in (options.SecretEnvironment ?? new Dictionary<string, string>()).OrderBy(item => item.Key, StringComparer.Ordinal))
		{
			container.Env.Add(new ContainerAppEnvironmentVariable { Name = name, SecretRef = secret });
		}

		var template = new ContainerAppJobExecutionTemplate();
		template.Containers.Add(container);
		return template;
	}

	public async Task<string> LaunchAsync(RunLaunch launch, CancellationToken ct = default)
	{
		var job = arm.GetContainerAppJobResource(new ResourceIdentifier(options.JobResourceId));
		var started = await job.StartAsync(WaitUntil.Completed, Template(options, launch), ct);
		return started.Value.Id?.ToString() ?? throw new InvalidOperationException("Azure did not return the job execution's id.");
	}

	public async Task KillAsync(string execution, CancellationToken ct = default)
	{
		var resource = arm.GetContainerAppJobExecutionResource(new ResourceIdentifier(execution));
		await resource.StopExecutionJobAsync(WaitUntil.Started, ct);
	}
}
