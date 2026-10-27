import * as cdk from 'aws-cdk-lib';
import { EnvironmentConfig } from './config';
import { DataStack } from './data-stack';
import { NetworkStack } from './network-stack';
import { ObservabilityStack } from './observability-stack';
import { ServerStack } from './server-stack';
import { WorkersStack } from './workers-stack';

/** The stacks of one environment, in deployment order. */
export interface BassiaStacks {
  network: NetworkStack;
  data: DataStack;
  server: ServerStack;
  workers: WorkersStack;
  observability: ObservabilityStack;
}

/**
 * Adds the stacks of the environment `config` describes to `app`: `<env>-network`, `-data`, `-server`, `-workers` and
 * `-observability`, each deployable on its own (`cdk deploy <env>-server`), wired by references.
 */
export function addEnvironment(app: cdk.App, config: EnvironmentConfig): BassiaStacks {
  const env = { account: config.account || process.env.CDK_DEFAULT_ACCOUNT, region: config.region };
  const tags = { application: 'bassia', environment: config.envName };
  const name = (part: string) => `${config.envName}-${part}`;

  const network = new NetworkStack(app, name('network'), { env, tags, config });
  const data = new DataStack(app, name('data'), { env, tags, config, vpc: network.vpc });
  const server = new ServerStack(app, name('server'), { env, tags, config, vpc: network.vpc, data });
  const workers = new WorkersStack(app, name('workers'), {
    env, tags, config, vpc: network.vpc, cluster: server.cluster, repository: data.workerRepository, serverUrl: server.url,
  });
  const observability = new ObservabilityStack(app, name('observability'), { env, tags, config, data, server });
  return { network, data, server, workers, observability };
}
